using System.Collections.Immutable;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

using SourceGeneratorCommon;

namespace COWSourceGenerator;


public static partial class CodeAnalysisExtensions {
    public static bool IsNullable( this ITypeSymbol typeSymbol ) =>
        typeSymbol.NullableAnnotation == NullableAnnotation.Annotated;

    public static bool IsNullableValueType( this ITypeSymbol typeSymbol ) =>
        typeSymbol.IsValueType && typeSymbol.IsNullable();

    public static bool TryGetNullableValueUnderlyingType( this ITypeSymbol typeSymbol, out ITypeSymbol? underlyingType ) {
        if ( typeSymbol is INamedTypeSymbol namedType && typeSymbol.IsNullableValueType() && namedType.IsGenericType ) {
            var typeParameters = namedType.TypeArguments;
            // Assert the generic is named System.Nullable<T> as expected.
            underlyingType = typeParameters[0];
            // TODO: decide what to return when the underlying type is not declared due to some compilation error.
            // TypeKind.Error indicats a compilation error, specifically a nullable type where the underlying type was not found.
            // I have observed that IsValueType will be true in such cases even though it is actually unknown whether the missing type is a value type
            // I chose to return false but you may prefer something else. 
            return underlyingType.TypeKind == TypeKind.Error ? false : true;
        }
        underlyingType = null;
        return false;
    }

    public static bool IsEnum( this ITypeSymbol typeSymbol ) =>
        typeSymbol is INamedTypeSymbol namedType && namedType.EnumUnderlyingType != null;

    public static bool IsNullableEnumType( this ITypeSymbol typeSymbol ) =>
        typeSymbol.TryGetNullableValueUnderlyingType( out var underlyingType ) == true && (underlyingType?.IsEnum() ?? false);
}

// struct eq array https://github.com/CommunityToolkit/dotnet/blob/7b53ae23dfc6a7fb12d0fc058b89b6e948f48448/src/CommunityToolkit.Mvvm.SourceGenerators/Helpers/EquatableArray%7BT%7D.cs
record GlobalConfig(
    string UpdatableInterface,
    string UpdatableInterfaceCloneMethod,
    EquatableArray<string> UsingNamespaces,
    string ChangeTrackerType,
    string ChangeTrackerSetPropertyMethod
    ) {
    const string OPTIONS_PREFIX = nameof( COWSourceGenerator );
    public static GlobalConfig Load( AnalyzerConfigOptions opt ) =>
        new GlobalConfig(
            UpdatableInterface: opt.GetStringOrDefault( OPTIONS_PREFIX, nameof( UpdatableInterface ) ) ?? "IUpdatable",
            UpdatableInterfaceCloneMethod: opt.GetStringOrDefault( OPTIONS_PREFIX, nameof( UpdatableInterfaceCloneMethod ) ) ?? "CloneForUpdate",
            UsingNamespaces: (opt.GetStringOrDefault( OPTIONS_PREFIX, nameof( UsingNamespaces ) )
                ?.Split( new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries ) ?? [])
                .Select( _ => _.Trim() )
                .Where( _ => !string.IsNullOrWhiteSpace( _ ) )
                .ToImmutableArray(),
            ChangeTrackerType: opt.GetStringOrDefault( OPTIONS_PREFIX, nameof( ChangeTrackerType ) ) ?? "ChangeTracker",
            ChangeTrackerSetPropertyMethod: opt.GetStringOrDefault( OPTIONS_PREFIX, nameof( ChangeTrackerSetPropertyMethod ) ) ?? "SetProperty"
        );

    public static GlobalConfig Load( AnalyzerConfigOptionsProvider optProv, CancellationToken _ ) =>
        Load( optProv.GlobalOptions );
}

record Property(
    string Name,
    //string Type,
    string TypeWithoutNullable,
    bool TypeIsNullable,
    bool IsVirtual,
    bool IsEnum,
    Accessibility SetterAccessibility,
    int? PrimaryKeyOrder,
    bool IsGeneratedKey,
    bool IsTenantKey,
    bool IsIgnored
    ) {
    public bool IsPrimaryKey => PrimaryKeyOrder.HasValue;

    public static readonly Property None = new( default!, default!, default, default, default, default, default, default, default, default );
}

record COWClassConfig(
    string ContainingNamespace,
    string Name,
    EquatableArray<Property> RelevantProperties,
    EquatableArray<string> Interfaces,
    string? TypescriptPath
    );

static class COWClassConfigExt {

    public static IEnumerable<Property> GetAssocIdProps( this COWClassConfig cls ) =>
        cls.RelevantProperties.Where( prop =>
            prop.Name.EndsWith( "Id" )
            && !prop.IsVirtual );

    public static IEnumerable<Property> GetAssocProps( this COWClassConfig cls ) =>
        cls.RelevantProperties.Where( prop => prop.IsVirtual );


    public static IEnumerable<(Property AssocProp, Property IdProp, bool IsProtected)>
        GetAssocWithIdPropPairs( this COWClassConfig cls, IEnumerable<Property> assocProps ) {
        //Accessibility[] assocAccessibilityFilter = [Accessibility.ProtectedOrInternal, Accessibility.Protected, Accessibility.Internal];

        //assocProps = assocProps.Where(_ => assocAccessibilityFilter.Contains(_.SetterAccessibility)).ToList();
        var assocIdProps = cls.GetAssocIdProps()
            //.Where(_ => _.SetterAccessibility == Accessibility.Protected )
            .ToDictionary( _ => _.Name );

        return assocProps.Select( _ =>
                assocIdProps.TryGetValue( _.Name + "Id", out var idProp )
                ? (_, idProp, _.SetterAccessibility == Accessibility.Protected || _.SetterAccessibility == Accessibility.Private)
                : (Property.None, Property.None, default) )
            .Where( _ => _.Item1 != Property.None );
    }

    public static IEnumerable<Property> GetKeys( this COWClassConfig cls ) =>
        cls.RelevantProperties.Where( _ => _.IsPrimaryKey ).OrderBy( _ => _.PrimaryKeyOrder ?? 0 ).ToList();

    public static Property? GetTenantKeyProp( this COWClassConfig cls ) =>
        cls.RelevantProperties.SingleOrDefault( _ => _.IsTenantKey && !_.IsVirtual );
}


[Generator]
public class IncrementalGenerator: IIncrementalGenerator {

    static bool IsCOWObjectCandidate( SyntaxNode s, CancellationToken t ) =>
        s is ClassDeclarationSyntax cls
        && (cls.Modifiers.Any( m => m.IsKind( SyntaxKind.PublicKeyword ) ) || cls.Modifiers.Any( m => m.IsKind( SyntaxKind.InternalKeyword ) ))
        && cls.Modifiers.Any( m => m.IsKind( SyntaxKind.PartialKeyword ) )
        && !cls.Modifiers.Any( m => m.IsKind( SyntaxKind.StaticKeyword ) );

    static int? GetPrimaryKeyOrder( IPropertySymbol p ) {
        var attr = p.GetAttributes().SingleOrDefault( a => a.AttributeClass?.Name == "PrimaryKeyAttribute" );
        if ( attr is null ) return null;

        if ( attr.ConstructorArguments.Length == 1 ) {
            return (int)attr.ConstructorArguments[0].Value!;
        }

        return 0;
    }

    public void Initialize( IncrementalGeneratorInitializationContext initContext ) {
        var configProv = initContext.AnalyzerConfigOptionsProvider.Select( GlobalConfig.Load );

        // Collect classes with the attribute
        var cowClasses = initContext.SyntaxProvider
            .ForAttributeWithMetadataName(
                "COWEntities.COWEntityAttribute",
                IsCOWObjectCandidate,
                ( context, _ ) => {
                    var clsSymbol = (INamedTypeSymbol)context.TargetSymbol;

                    var mutableProps = clsSymbol.GetMembers()
                        .OfType<IPropertySymbol>()
                        //.Where( _ => _.SetMethod != null )
                        .Select( p => {
                            var (propType, isNullableValueType) = p.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                                ? (((INamedTypeSymbol)p.Type).TypeArguments[0], true)
                                : (p.Type, false);

                            return new Property(
                                Name: p.Name,
                                //Type: propType.ToDisplayString( SymbolDisplayFormat.MinimallyQualifiedFormat ),
                                TypeWithoutNullable: propType.WithNullableAnnotation( NullableAnnotation.NotAnnotated ).ToDisplayString( SymbolDisplayFormat.MinimallyQualifiedFormat ),
                                TypeIsNullable: p.NullableAnnotation == NullableAnnotation.Annotated || isNullableValueType,
                                IsVirtual: p.IsVirtual,
                                IsEnum: propType.IsEnum() || propType.IsNullableEnumType(),
                                SetterAccessibility: p.SetMethod?.DeclaredAccessibility ?? Accessibility.NotApplicable,
                                PrimaryKeyOrder: GetPrimaryKeyOrder( p ),
                                IsGeneratedKey: p.GetAttributes().Any( a => a.AttributeClass?.Name == "GeneratedKeyAttribute" ),
                                IsTenantKey: p.GetAttributes().Any( a => a.AttributeClass?.Name == "TenantKeyAttribute" ),
                                IsIgnored: p.GetAttributes().Any( a => a.AttributeClass?.Name == "JsonIgnoreAttribute" )
                            );
                        } )
                        .OrderBy( _ => !_.IsTenantKey )
                        .ToImmutableArray();


                    return new COWClassConfig(
                        ContainingNamespace: clsSymbol.ContainingNamespace.Name,
                        Name: clsSymbol.Name,
                        RelevantProperties: mutableProps,
                        Interfaces: clsSymbol.AllInterfaces.Select( _ => _.Name ).ToImmutableArray(),
                        TypescriptPath:
                            clsSymbol.Locations
                                .Where( _ => _.IsInSource )
                                .OrderByDescending( _ => (_.SourceTree?.FilePath?.Contains( ".g." ) ?? false) || (_.SourceTree?.FilePath?.Contains( ".generated." ) ?? false) )
                                .FirstOrDefault()
                                ?.SourceTree?.FilePath is string sourceFilePath
                            ? Path.Combine( Path.GetDirectoryName( sourceFilePath ), "typescript", $"{clsSymbol.Name}.ts" )
                            : ""
                        );
                } );

        var globalConfig = initContext.AnalyzerConfigOptionsProvider
            .Select( GlobalConfig.Load );

        initContext.RegisterSourceOutput(
            cowClasses.Combine( globalConfig ),
            ( spc, ivp ) => {
                COWClassConfig classConfig = ivp.Left;
                GlobalConfig globalConfig = ivp.Right;
                GenerateSource( spc, globalConfig, classConfig );
            } );
    }


    private void GenerateSource( SourceProductionContext context, GlobalConfig conf, COWClassConfig cls ) {
        StringBuilder usings = new();
        foreach ( var ns in conf.UsingNamespaces ) {
            usings.AppendLine( $"using {ns};" );
        }

        var extensionMethods = GeneratePropExtensions( conf, cls );
        var assocMethods = GenerateAssocProps( conf, cls );
        var (forTestParams, forTestAssignments) = GenerateForTest( cls );
        var (ctrArgs, ctrAssignments) = GenerateConstructor( cls );

        var copyChanges = GenerateCopyChanges( conf, cls );

        var sb = new StringBuilder();

        bool isInsertable = cls.Interfaces.Contains( $"IInsertable" );
        bool hasPublicProps = cls.RelevantProperties.Any( _ => _.SetterAccessibility == Accessibility.Public );
        bool isUpdatable = cls.RelevantProperties.Any( _ => _.SetterAccessibility != Accessibility.Private && _.SetterAccessibility != Accessibility.Public );

        var insertableImpl = isInsertable ? GenerateIInsertableImpl( cls ) : new();
        var updatableImpl = isUpdatable ? GenerateIUpdatableImpl( cls ) : new();

        var tenantImpl = GenerateTenantSpecific( cls );

        var (filterExpr, extFilterExpr) = GenerateFilterExpr( cls );

        string ctr = $$"""
            public {{cls.Name}} (
        {{ctrArgs}}
                int _ignore = default
            ) {
        {{ctrAssignments}}
            }
        """;

        // Generate the partial class with ForTest method
        sb.AppendLine( $$"""
        #nullable enable
        {{usings}}

        namespace {{cls.ContainingNamespace}};
        public partial class {{cls.Name}}{{(isUpdatable ? $": {conf.UpdatableInterface}<{cls.Name}>" : "")}}
        {
            {{(hasPublicProps ? "public" : "private")}} {{cls.Name}}() {}

        {{(isInsertable ? ctr : "")}}

            internal static {{cls.Name}} ForTest(
        {{forTestParams}}
                int _ignore = default
            ) =>
            new()
            {
        {{forTestAssignments}}
            };

            {{(isUpdatable ? $"{cls.Name} {conf.UpdatableInterface}<{cls.Name}>.{conf.UpdatableInterfaceCloneMethod}() => ({cls.Name})this.MemberwiseClone();" : "")}}

        {{insertableImpl}}
        
        {{updatableImpl}}
        
        {{filterExpr}}

        {{tenantImpl}}

        }


        """ );

        // Generate the partial extension class
        sb.AppendLine( $$"""
        public static partial class {{cls.Name}}Ext
        {
        {{extFilterExpr}}
        
        {{extensionMethods}}

        {{assocMethods}}

        {{copyChanges}}
        }
        """ );


        sb.AppendLine( $$"""
        /*
        {{cls}}

        """ );

        foreach ( var p in cls.RelevantProperties ) {
            sb.AppendLine( $"{p}" );
        }
        foreach ( var p in cls.Interfaces ) {
            sb.AppendLine( p );
        }

        sb.AppendLine( $$"""
        */
        """ );

        context.AddSource( $"{cls.Name}.cow.g.cs", SourceText.From( sb.ToString(), Encoding.UTF8 ) );

#pragma warning disable RS1035
        if ( cls.TypescriptPath is not null ) {
            Directory.CreateDirectory( Path.GetDirectoryName( cls.TypescriptPath ) );

            using var fs = new FileStream( cls.TypescriptPath, FileMode.Create, FileAccess.Write );
            using var s = new StreamWriter( fs, Encoding.UTF8 );
            s.Write( GenerateTypescript( cls ) );

            s.Close();
            fs.Close();
        }
#pragma warning restore RS1035
    }


    private static StringBuilder GenerateTenantSpecific( COWClassConfig cls ) {
        StringBuilder sb = new();

        if ( cls.GetTenantKeyProp() is Property tenantProp ) {
            if ( tenantProp.TypeIsNullable ) {

                sb.AppendLine( $$"""
                    void IHasOptionalTenantKey<{{tenantProp.TypeWithoutNullable}}{{(tenantProp.TypeIsNullable ? "?" : "")}}>.SetOptionalTenantKeyUnsafe( {{tenantProp.TypeWithoutNullable}}{{(tenantProp.TypeIsNullable ? "?" : "")}} tenantKey ) {
                        this.{{tenantProp.Name}} = tenantKey;
                    }
                """ );
            } else {
                sb.AppendLine( $$"""
                    void IHasTenantKey<{{tenantProp.TypeWithoutNullable}}>.SetTenantKeyUnsafe( {{tenantProp.TypeWithoutNullable}} tenantKey ) {
                        this.{{tenantProp.Name}} = tenantKey;
                    }
                """ );

            }
        }

        return sb;
    }


    private static (StringBuilder, StringBuilder) GenerateForTest( COWClassConfig cls ) {
        var forTestParams = new StringBuilder();
        var forTestAssignments = new StringBuilder();

        foreach ( var prop in cls.RelevantProperties.Where( _ => _.SetterAccessibility != Accessibility.NotApplicable) ) {
            forTestParams.AppendLine( $$"""
                    {{prop.TypeWithoutNullable}}{{(prop.TypeIsNullable ? "?" : "")}} {{prop.Name}} = default{{(prop.TypeIsNullable ? "" : "!")}},
            """ );

            forTestAssignments.AppendLine( $$"""
                    {{prop.Name}} = {{prop.Name}},
            """ );
        }

        return (forTestParams, forTestAssignments);
    }

    static void WritePropHelpers( StringBuilder sb, COWClassConfig cls, Property prop, string description ) {
        sb.AppendLine( $$"""
            // {{description}}
            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_{{prop.Name}}")]
            private extern static void {{prop.Name}}_Setter({{cls.Name}} d, {{prop.TypeWithoutNullable}}{{(prop.TypeIsNullable ? "?" : "")}} v);

            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_{{prop.Name}}")]
            private extern static {{prop.TypeWithoutNullable}}{{(prop.TypeIsNullable ? "?" : "")}} {{prop.Name}}_Getter({{cls.Name}} d);

            public static readonly Expression<Func<{{cls.Name}}, {{prop.TypeWithoutNullable}}{{(prop.TypeIsNullable ? "?" : "")}}>> {{prop.Name}}_Expr = _ => _.{{prop.Name}}!;
            public static readonly Expression<Func<{{cls.Name}}, object?>> {{prop.Name}}_ObjExpr = _ => _.{{prop.Name}};
        """ );

        if ( prop.IsVirtual ) {
            sb.AppendLine( $$"""
                public static readonly Expression<Func<{{cls.Name}}, {{prop.TypeWithoutNullable}}>> {{prop.Name}}_NotNullExpr = _ => _.{{prop.Name}}!;
            """ );

        }

        sb.AppendLine( $$"""

        """ );
    }

    static StringBuilder GeneratePropExtensions( GlobalConfig conf, COWClassConfig cls ) {
        var extensionMethods = new StringBuilder();
        foreach ( var prop in cls.RelevantProperties.Where( _ => !_.IsVirtual && _.SetterAccessibility != Accessibility.NotApplicable ) ) {
            WritePropHelpers( extensionMethods, cls, prop, $"{prop.Name} property" );

            if ( prop.SetterAccessibility != Accessibility.Private && prop.SetterAccessibility != Accessibility.Public ) {
                var accessibility = prop.SetterAccessibility.ToDisplayStringForExt();

                extensionMethods.AppendLine( $$"""
                    {{accessibility}} static TChangeTracker Set{{prop.Name}}<TChangeTracker>(this TChangeTracker changeTracker, {{prop.TypeWithoutNullable}}{{(prop.TypeIsNullable ? "?" : "")}} val) 
                        where TChangeTracker: {{conf.ChangeTrackerType}}<{{cls.Name}}, TChangeTracker> =>
                        changeTracker.{{conf.ChangeTrackerSetPropertyMethod}}({{prop.Name}}_Getter, {{prop.Name}}_Setter, {{prop.Name}}_Expr, val);
                
                """ );
            }
        }

        return extensionMethods;
    }

    static StringBuilder GenerateAssocProps( GlobalConfig conf, COWClassConfig cls ) {
        var assocProps = cls.GetAssocProps();

        var assocWithIdPropPairs = cls.GetAssocWithIdPropPairs( assocProps ).ToDictionary( _ => _.AssocProp.Name );

        StringBuilder builder = new();

        foreach ( var assocProp in assocProps ) {
            if ( assocWithIdPropPairs.TryGetValue( assocProp.Name, out var x ) ) {
                var idProp = x.IdProp;
                WritePropHelpers( builder, cls, assocProp, $"{assocProp.Name} <--> {idProp.Name}" );
                if ( assocProp.SetterAccessibility != Accessibility.Private ) {
                    builder.AppendLine( $$"""
                            {{assocProp.SetterAccessibility.ToDisplayStringForExt()}} static TChangeTracker Set{{assocProp.Name}}<TChangeTracker>(this TChangeTracker changeTracker, {{assocProp.TypeWithoutNullable}}{{(idProp.TypeIsNullable ? "?" : "")}} val)
                                where TChangeTracker: {{conf.ChangeTrackerType}}<{{cls.Name}}, TChangeTracker> =>
                                changeTracker
                                    .{{conf.ChangeTrackerSetPropertyMethod}}({{idProp.Name}}_Getter, {{idProp.Name}}_Setter, {{idProp.Name}}_Expr, val{{(idProp.TypeIsNullable ? "?" : "")}}.Id)
                                    .{{conf.ChangeTrackerSetPropertyMethod}}({{assocProp.Name}}_Getter, {{assocProp.Name}}_Setter, null, val);

                        """ );
                }
            } else {
                WritePropHelpers( builder, cls, assocProp, $"{assocProp.Name}" );

            }
        }

        return builder;
    }


    static (StringBuilder Args, StringBuilder Assignments) GenerateConstructor( COWClassConfig cls ) {
        var assocProps = cls.GetAssocProps();

        List<(Property AssocProp, Property IdProp, bool IsProtected)> otherAssocProps = new();
        var assocWithIdPropPairs = cls.GetAssocWithIdPropPairs( assocProps ).Where( _ => !_.IdProp.IsTenantKey );

        var assocIdPropNames = assocWithIdPropPairs.Select( _ => _.IdProp.Name ).ToImmutableHashSet();

        (StringBuilder args, StringBuilder assignments) = (new(), new());
        foreach ( var pp in assocWithIdPropPairs ) {
            if ( (pp.IsProtected || !pp.IdProp.TypeIsNullable) ) {
                args.AppendLine( $$"""
                        {{pp.AssocProp.TypeWithoutNullable}}{{(pp.IdProp.TypeIsNullable ? "?" : "")}} {{pp.AssocProp.Name}},
                """ );
                assignments.AppendLine( $$"""
                        this.{{pp.IdProp.Name}} = {{pp.AssocProp.Name}}{{(pp.IdProp.TypeIsNullable ? "?" : "")}}.Id;
                        this.{{pp.AssocProp.Name}} = {{pp.AssocProp.Name}};
                """ );
            } else {
                otherAssocProps.Add( pp );
            }
        }

        List<Property> defaultProps = new();
        var privateProps = cls.RelevantProperties.Where( _ => !_.IsGeneratedKey && !_.IsTenantKey && !_.IsVirtual && !assocIdPropNames.Contains( _.Name ) && _.SetterAccessibility != Accessibility.NotApplicable );
        foreach ( var prop in privateProps ) {

            if ( prop.SetterAccessibility == Accessibility.Private || !prop.TypeIsNullable ) {
                args.AppendLine( $$"""
                        {{prop.TypeWithoutNullable}}{{(prop.TypeIsNullable ? "?" : "")}} {{prop.Name}},
                """ );
                assignments.AppendLine( $$"""
                        this.{{prop.Name}} = {{prop.Name}};
                """ );
            } else {
                defaultProps.Add( prop );
            }
        }

        foreach ( var pp in otherAssocProps ) {
            args.AppendLine( $$"""
                    {{pp.AssocProp.TypeWithoutNullable}}{{(pp.IdProp.TypeIsNullable ? "?" : "")}} {{pp.AssocProp.Name}} = default!,
            """ );
            assignments.AppendLine( $$"""
                    this.{{pp.IdProp.Name}} = {{pp.AssocProp.Name}}?.Id;
                    this.{{pp.AssocProp.Name}} = {{pp.AssocProp.Name}};
            """ );
        }


        foreach ( var prop in defaultProps ) {
            args.AppendLine( $$"""
                        {{prop.TypeWithoutNullable}}{{(prop.TypeIsNullable ? "?" : "")}} {{prop.Name}} = default!,
                """ );
            assignments.AppendLine( $$"""
                        this.{{prop.Name}} = {{prop.Name}};
                """ );
        }

        return (args, assignments);
    }


    static (StringBuilder hasPrimary, StringBuilder ext) GenerateFilterExpr( COWClassConfig cls ) {
        (StringBuilder hasPrimary, StringBuilder ext) = (new(), new());
        var keys = cls.GetKeys().ToList();
        if ( keys.Any() ) {

            string keyType;
            if ( keys.Count > 1 ) {
                keyType = "(";
                foreach ( var k in keys ) {
                    keyType += $"{(k.PrimaryKeyOrder == 0 ? "" : ",")} {k.TypeWithoutNullable} {k.Name}";
                }
                keyType += ")";
            } else {
                keyType = keys[0].TypeWithoutNullable;
            }


            ext.AppendLine( $"    public static Expression<Func<{cls.Name}, bool>> GetFilterExpr({keyType} key) =>" );
            ext.Append( "        _ => " );
            if ( keys.Count > 1 ) {
                foreach ( var p in keys ) {
                    ext.Append( $"{(p.PrimaryKeyOrder == 0 ? "" : "&&")} _.{p.Name} == key.{p.Name} " );
                }
            } else {
                var p = keys[0];
                ext.Append( $" _.{p.Name} == key" );
            }
            ext.AppendLine( """
                ;
                
                """ );

            hasPrimary.AppendLine( $"    public static Expression<Func<{cls.Name}, bool>> GetFilterExpr({cls.Name} obj) =>" );
            hasPrimary.Append( "        _ => " );
            foreach ( var p in keys ) {
                hasPrimary.Append( $"{(p.PrimaryKeyOrder == 0 ? "" : "&&")} _.{p.Name} == obj.{p.Name} " );
            }
            hasPrimary.AppendLine( """
                ;
                
                """ );

            if ( cls.Interfaces.Contains( "IHasPrimaryKey" ) ) {
                hasPrimary.AppendLine( $$"""    void IHasPrimaryKey<{{cls.Name}}, {{keyType}}>.SetPrimaryKeyUnsafe({{keyType}} key) {""" );
                if ( keys.Count > 1 ) {
                    foreach ( var p in keys ) {
                        hasPrimary.AppendLine( $"        this.{p.Name} = key.{p.Name};" );
                    }
                } else {
                    var p = keys[0];
                    hasPrimary.AppendLine( $"        this.{p.Name} = key;" );
                }
                hasPrimary.AppendLine( """
                    }
                
                """ );
            }


        }

        return (hasPrimary, ext);
    }


    static StringBuilder GenerateCopyChanges( GlobalConfig conf, COWClassConfig cls ) {
        var assocProps = cls.GetAssocProps();

        var assocWithIdPropPairs = cls.GetAssocWithIdPropPairs( assocProps ).Where( _ => !_.IsProtected );

        var assocIdPropNames = assocWithIdPropPairs.Select( _ => _.IdProp.Name ).ToImmutableHashSet();

        StringBuilder sb = new();
        sb.Append( $$"""
                public static TChangeTracker CopyChangesFrom<TChangeTracker>(this TChangeTracker changeTracker, {{cls.Name}} src
            """ );
        foreach ( var pp in assocWithIdPropPairs ) {
            sb.Append( $$""", {{pp.AssocProp.TypeWithoutNullable}}{{(pp.AssocProp.TypeIsNullable ? "?" : "")}} {{pp.AssocProp.Name}}""" );
        }
        sb.AppendLine( $$""" 
            ) 
                    where TChangeTracker: {{conf.ChangeTrackerType}}<{{cls.Name}}, TChangeTracker> {
                    // Copy mutable properties
            """ );

        foreach ( var prop in cls.RelevantProperties.Where( _ => !_.IsGeneratedKey && !_.IsPrimaryKey && !_.IsVirtual && _.SetterAccessibility == Accessibility.Internal && !assocIdPropNames.Contains( _.Name ) ) ) {
            sb.AppendLine( $$"""
                        changeTracker = changeTracker.Set{{prop.Name}}( src.{{prop.Name}} );
                """ );
        }

        sb.AppendLine( $$""" 

                    // Copy mutable associations
            """ );

        foreach ( var pp in assocWithIdPropPairs ) {
            sb.AppendLine( $$"""
                        if ( src.{{pp.IdProp.Name}} != {{pp.AssocProp.Name}}?.Id ) { throw new Exception( "Id of src.{{pp.IdProp.Name}} != parameter {{pp.AssocProp.Name}}?.Id"); }
                        changeTracker = changeTracker.Set{{pp.AssocProp.Name}}( {{pp.AssocProp.Name}} );
                """ );
        }

        sb.AppendLine( """

                    return changeTracker;
                }

            """ );

        //if ( assocWithIdPropPairs .Any()) { 
        //    sb.Append( $$"""
        //        public static TChangeTracker CopyChangesFrom<TChangeTracker>(this TChangeTracker changeTracker, {{cls.Name}} src
        //    """ );
        //    foreach ( var pp in assocWithIdPropPairs ) {
        //        sb.Append( $$""", Func<{{pp.IdProp.Type}},{{(pp.IdProp.TypeIsNullable ? pp.AssocProp.Type : pp.AssocProp.TypeWithoutNullable)}}> get{{pp.AssocProp.Name}}""" );
        //    }
        //    sb.Append( $$""" 
        //    ) 
        //            where TChangeTracker: {{conf.ChangeTrackerType}}<{{cls.Name}}, TChangeTracker> =>
        //            changeTracker.CopyChangesFrom(src
        //    """ );
        //    foreach ( var pp in assocWithIdPropPairs ) {
        //        sb.Append( $$""", get{{pp.AssocProp.Name}}(src.{{pp.IdProp.Name}})""" );
        //    }

        //    sb.AppendLine( $$""" 
        //    );

        //    """ );
        //}


        sb.AppendLine( $$"""
            #pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
            public static async Task<TChangeTracker> CopyChangesAndResolveAssociations<TChangeTracker>(this TChangeTracker changeTracker, IAssociationLookup associationLookup, {{cls.Name}} src )
        """ );
        sb.AppendLine( $$""" 
                where TChangeTracker: {{conf.ChangeTrackerType}}<{{cls.Name}}, TChangeTracker> =>
                changeTracker.CopyChangesFrom(src
        """ );
        foreach ( var pp in assocWithIdPropPairs ) {
            sb.AppendLine( $$"""
                        , {{(pp.IdProp.TypeIsNullable ? $"src.{pp.IdProp.Name} is null ? null : " : "")}}await associationLookup.GetAssociation({{pp.AssocProp.TypeWithoutNullable}}Ext.GetFilterExpr(src.{{pp.IdProp.Name}}{{(pp.IdProp.TypeIsNullable ? $".Value" : "")}}))
            """ );
        }

        sb.Append( $$""" 
                );
            #pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        
        """ );

        return sb;

    }

    static StringBuilder GenerateIInsertableImpl( COWClassConfig cls ) {
        StringBuilder sb = new();

        var assocProps = cls.GetAssocProps().Where( _ => !_.IsTenantKey );

        var assocWithIdPropPairs = cls.GetAssocWithIdPropPairs( assocProps );

        sb.AppendLine( $$"""
                #pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
                public static async Task ResolveAssociations( IAssociationLookup associationLookup, {{cls.Name}} target ) {
            """ );

        foreach ( var pp in assocWithIdPropPairs ) {
            sb.AppendLine( $$"""
                    target.{{pp.AssocProp.Name}} = {{(pp.IdProp.TypeIsNullable ? $"target.{pp.IdProp.Name} is null ? null : " : "")}}await associationLookup.GetAssociation({{pp.AssocProp.TypeWithoutNullable}}Ext.GetFilterExpr(target.{{pp.IdProp.Name}}{{(pp.IdProp.TypeIsNullable ? $".Value" : "")}}));
            """ );
        }

        sb.AppendLine( $$"""
                }
                #pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
            """ );

        return sb;
    }


    static StringBuilder GenerateIUpdatableImpl( COWClassConfig cls ) {
        StringBuilder sb = new();
        sb.AppendLine( $$"""
                public static Task<TChangeTracker> CopyChangesAndResolveAssociations<TChangeTracker>(TChangeTracker changeTracker, IAssociationLookup associationLookup, {{cls.Name}} src ) 
                    where TChangeTracker : ChangeTracker<{{cls.Name}}, TChangeTracker>
                    =>
                    {{cls.Name}}Ext.CopyChangesAndResolveAssociations( changeTracker, associationLookup, src );
            """ );

        var keys = cls.GetKeys().ToList();
        if ( keys.Any() ) {
            string keyType = keys.Count > 1 ? $"{cls.Name}Ext.Key" : keys[0].TypeWithoutNullable;

            sb.AppendLine( $$"""


                    public static Task<TResult> ExecuteUpdateAsync<TResult>( IUpdateContext<TResult> updateContext ) =>
                        updateContext.ExecuteUpdateAsync<{{cls.Name}}, {{keyType}}>({{cls.Name}}Ext.GetFilterExpr);
                """ );
        }


        return sb;
    }

    enum PropTsCategory { Updatable, Insertable, ReadOnly }

    static PropTsCategory GetPropTsCategory( Property p, IReadOnlyDictionary<string, (Property AssocProp, Property IdProp, bool IsProtected)> assocIdInnerTypes ) =>
        p.SetterAccessibility == Accessibility.Internal || (p.SetterAccessibility == Accessibility.Protected && assocIdInnerTypes.ContainsKey( p.Name )) ? PropTsCategory.Updatable :
        p.SetterAccessibility == Accessibility.Private || p.SetterAccessibility == Accessibility.Protected ? PropTsCategory.Insertable :
        PropTsCategory.ReadOnly
        ;

    static StringBuilder GenerateTypescript( COWClassConfig cls ) {
        StringBuilder sb = new();

        var associationProperties = cls.RelevantProperties.Where( _ => _.IsVirtual && !_.IsTenantKey && !_.IsIgnored );
        var assocWithIdPropPairs = cls.GetAssocWithIdPropPairs( associationProperties );

        var assocIdInnerTypes = assocWithIdPropPairs.ToDictionary( _ => _.IdProp.Name );

        //foreach(var kv in assocIdInnerTypes) {
        //    sb.AppendLine($"// {kv.Key}: {kv.Value}");
        //}

        var dataProperties = cls.RelevantProperties.Where( _ => !_.IsTenantKey && !_.IsGeneratedKey && !_.IsVirtual && !_.IsIgnored )
                .GroupBy( _ => GetPropTsCategory( _, assocIdInnerTypes ) );

        var updatableProperties = dataProperties.FirstOrDefault( g => g.Key == PropTsCategory.Updatable )?.ToList() ?? [];
        var insertableProperties = dataProperties.FirstOrDefault( g => g.Key == PropTsCategory.Insertable )?.ToList() ?? [];
        var readonlyProperties = dataProperties.FirstOrDefault( g => g.Key == PropTsCategory.ReadOnly )?.ToList() ?? [];

        HashSet<string> imports = new() { cls.Name };
        foreach ( var p in associationProperties ) {
            if ( !imports.Contains( p.TypeWithoutNullable ) ) {
                string path = p.IsEnum ? ".." : ".";
                sb.AppendLine( $$"""import { {{p.TypeWithoutNullable}}, {{p.TypeWithoutNullable}}TypeInfo } from '{{path}}/{{p.TypeWithoutNullable}}.ts'""" );
                imports.Add( p.TypeWithoutNullable );
            }
        }

        static string TsType( in string t ) =>
            t.ToUpperInvariant() switch {
                "INT" or "UINT" or "FLOAT" or "DOUBLE" or "LONG" or "ULONG" => "number",
                "INSTANT" or "DATETIME" or "DATETIMEOFFSET" => "Date",
                "BOOL" => "boolean",
                "STRING" or "TIMEONLY" or "TIMESPAN" => "string",
                _ => $"{t}"
            };

        static string TsDefaultValue( Property p ) =>
            p.TypeIsNullable ? "null" :
            p.TypeWithoutNullable.ToUpperInvariant() switch {
                "INT" or "UINT" or "FLOAT" or "DOUBLE" or "LONG" or "ULONG" => "0",
                "INSTANT" or "DATETIME" or "DATETIMEOFFSET" => "new Date()",
                "BOOL" => "false",
                "TIMEONLY" or "TIMESPAN" => "'08:00'",
                _ when p.TypeWithoutNullable.StartsWith( "Id<" ) => $"0 as {p.TypeWithoutNullable}",
                _ => "''"
            };

        sb.AppendLine( $$"""

            export interface {{cls.Name}}UpdatableProperties {
            """ );
        foreach ( var p in updatableProperties ) {
            sb.AppendLine( $"   readonly {p.Name}: {TsType( p.TypeWithoutNullable )}{(p.TypeIsNullable ? " | null" : "")};" );
        }
        sb.AppendLine( $$"""
            }

            export interface {{cls.Name}}Properties extends {{cls.Name}}UpdatableProperties {
            """ );
        foreach ( var p in insertableProperties ) {
            sb.AppendLine( $"   readonly {p.Name}: {TsType( p.TypeWithoutNullable )}{(p.TypeIsNullable ? " | null" : "")};" );
        }
        sb.AppendLine( $$"""
            }

            export interface {{cls.Name}}Associations {
            """ );
        foreach ( var p in associationProperties ) {
            sb.AppendLine( $"   readonly {p.Name}?: {p.TypeWithoutNullable};" );
        }

        sb.AppendLine( $$"""
            }

            export interface {{cls.Name}} extends {{cls.Name}}Properties, {{cls.Name}}Associations {
            """ );

        foreach ( var p in cls.RelevantProperties.Where( _ => _.IsGeneratedKey ) ) {
            sb.AppendLine( $"   readonly {p.Name}: {TsType( p.TypeWithoutNullable )};" );
        }

        foreach ( var p in readonlyProperties ) {
            sb.AppendLine( $"   readonly {p.Name}: {TsType( p.TypeWithoutNullable )}{(p.TypeIsNullable ? " | null" : "")};" );
        }

        sb.AppendLine( $$"""
            }
            """ );



        sb.AppendLine( $$"""

            const defaultUpdatableProperties: {{cls.Name}}UpdatableProperties = {
            {{String.Join( ",\n", updatableProperties.Select( p => $"   {p.Name}: {TsDefaultValue( p )}" ) )}}
            }

            const defaultProperties: {{cls.Name}}Properties = {
                ...defaultUpdatableProperties,
            {{String.Join( ",\n", insertableProperties.Select( p => $"   {p.Name}: {TsDefaultValue( p )}" ) )}}
            }
            """ );

        string GetPropTypeInfo( Property p, bool isInsertable, bool isUpdatable, bool isAssociation ) =>
            $$"""   
                    {{p.Name}}: {
                        Name: "{{p.Name}}", 
                        Type: "{{TsType( p.TypeWithoutNullable )}}",
                        PrimaryKeyOrder: {{p.PrimaryKeyOrder?.ToString() ?? "null"}},
                        IsGeneratedKey: {{(p.IsGeneratedKey ? "true" : "false")}},
                        IsNullable: {{(p.TypeIsNullable ? "true" : "false")}},
                        IsUpdatable: {{(isUpdatable ? "true" : "false")}},
                        IsInsertable: {{(isInsertable ? "true" : "false")}},
                        IsAssociationId: {{(assocIdInnerTypes.ContainsKey( p.Name ) ? "true" : "false")}},
                        IsAssociation: {{(isAssociation ? "true" : "false")}},
                        AssociationType: {{(assocIdInnerTypes.TryGetValue( p.Name, out var assocInfo ) ? $"\"{TsType( assocInfo.AssocProp.TypeWithoutNullable )}\"" : "undefined")}},
                        AssociationProp: {{(assocIdInnerTypes.TryGetValue( p.Name, out var _ ) ? $"\"{assocInfo.AssocProp.Name}\"" : "undefined")}},
                    },

            """;

        sb.AppendLine( $$"""

           export const {{cls.Name}}TypeInfo: IEntityInfo<{{cls.Name}}, {{cls.Name}}Properties, {{cls.Name}}UpdatableProperties, {{cls.Name}}Associations> = {
               Name: "{{cls.Name}}",
               EntityType: {} as {{cls.Name}},
               PropertiesType: {} as {{cls.Name}}Properties,
               UpdatablePropertiesType: {} as {{cls.Name}}UpdatableProperties,
               Default: defaultProperties,
               DefaultUpdatable: defaultUpdatableProperties,
               AssociationTypeInfos: {
           """ );

        foreach ( var assocProp in associationProperties ) {
            sb.AppendLine( $"        {assocProp.Name}: () => {assocProp.TypeWithoutNullable}TypeInfo," );
        }

        sb.AppendLine( $$"""
                },
                Properties: {
            {{String.Join( "", cls.RelevantProperties.Where( _ => _.IsGeneratedKey ).Select( p => GetPropTypeInfo( p, false, false, false ) ) )}}
            {{String.Join( "", readonlyProperties.Select( p => GetPropTypeInfo( p, false, false, false ) ) )}}
            {{String.Join( "", insertableProperties.Select( p => GetPropTypeInfo( p, true, false, false ) ) )}}
            {{String.Join( "", updatableProperties.Select( p => GetPropTypeInfo( p, true, true, false ) ) )}}
            {{String.Join( "", associationProperties.Select( p => GetPropTypeInfo( p, false, false, true ) ) )}}
                }
           }
           """ );

        return sb;
    }
}
