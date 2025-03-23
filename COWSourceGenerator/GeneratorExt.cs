using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SourceGeneratorCommon;

public static partial class GeneratorExt {
    public static string? GetStringOrDefault( this AnalyzerConfigOptions options, string prefix, string key, string? defaultValue = null ) =>
        options.TryGetValue( $"{prefix}_{key}", out var x ) && !string.IsNullOrWhiteSpace( x ) ? x : defaultValue;

    public static bool GetBoolOrDefault( this AnalyzerConfigOptions options, string prefix, string key, bool defaultValue = false ) =>
        string.Equals( options.GetStringOrDefault( prefix, key, "false" ), "true", StringComparison.OrdinalIgnoreCase );

    static public string ToDisplayString( this Accessibility accessibility ) =>
        accessibility switch {
            Accessibility.NotApplicable => "NOT_APPLICABLE",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.Public => "public",
            _ => "UNKNOWN"
        };

    static public string ToDisplayStringForExt( this Accessibility accessibility ) =>
        accessibility switch {
            Accessibility.NotApplicable => "NOT_APPLICABLE",
            Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal => "internal",
            Accessibility.Internal or Accessibility.ProtectedOrInternal => "public",
            Accessibility.Public => "public",
            _ => "UNKNOWN"
        };

    static public bool IsInternalOrPublic( this IPropertySymbol prop ) =>
        prop.DeclaredAccessibility == Accessibility.Public || prop.DeclaredAccessibility == Accessibility.Internal;

}


/*


            context.ReportDiagnostic( Diagnostic.Create( new DiagnosticDescriptor(
                id: "COW_Conf002",
                title: "Config.UpdatableGenericMarkerInterface Not Set",
                messageFormat: "Config.UpdatableGenericMarkerInterface Not Set, using default.",
                category: "COWSourceGenerator",
                DiagnosticSeverity.Warning,
                isEnabledByDefault: true ), Location.None ) );

// Input class
public partial class ExampleClassWithAssociations {
    public Id<ExampleClassWithAssociations> Id { get; private set; }
    public Id<Tenant> TenantId { get; private set; }
    public Id<User> UserId { get; private set; }
    public Id<ClassType> ClassTypeId { get; private set; }
    public DateTime StartTime { get; private set; }

    public bool BoolProp { get; internal set; }
    public int? NullableIntProp { get; internal set; }

    public virtual User User { get; private set; }
    public virtual ClassType ClassType { get; private set; }
}

// Generated class 
public partial class ExampleClassWithAssociations {
    public static ExampleClassWithAssociations ForTest(
        Id<ExampleClassWithAssociations> Id = default, 
        Id<Tenant> TenantId = default,
        Id<User> UserId = default,
        Id<ClassType> ClassTypeId = default,
        DateTime StartTime = default,
        bool BoolProp = default,
        int? NullableIntProp = default,
        User User = default,
        ClassType ClassType = default
        ) =>
        new() {
            Id = Id,
            TenantId = TenantId,
            UserId = UserId,
            ClassTypeId = ClassTypeId,
            StartTime = StartTime,
            BoolProp = BoolProp,
            NullableIntProp = NullableIntProp,
            User = User,
            ClassType = ClassType,
        };

    ExampleClassWithAssociations IUpdatable<ExampleClassWithAssociations>.CloneForUpdate() => (ExampleClassWithAssociations)this.MemberwiseClone();
}

// Generated class
public static class ExampleClassWithAssociationsExt {
    [UnsafeAccessor( UnsafeAccessorKind.Method, Name = "set_BoolProp" )] 
    public extern static void BoolPropl_Setter( ExampleClassWithAssociations d, string? v );
    
    [UnsafeAccessor( UnsafeAccessorKind.Method, Name = "get_AVal" )] 
    public extern static string? AVal_Getter( ExampleClassWithAssociations d);
    
    public static readonly Expression<Func<ExampleClassWithAssociations, string?>> BoolProp_Expr = _ => _.BoolProp;

    public static ChangeTracker<ExampleClassWithAssociations> SetBoolProp(this ChangeTracker<ExampleClassWithAssociations> changeTracker, string? val) =>
        changeTracker.SetProperty( BoolProp_Getter, BoolProp_Setter, BoolProp_Expr, val );
}



*/
