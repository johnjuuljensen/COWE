using System;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace COWEntities;

[AttributeUsage( AttributeTargets.Class )]
public sealed class COWEntityAttribute: Attribute { }

[AttributeUsage( AttributeTargets.Property )]
public sealed class PrimaryKeyAttribute: Attribute {
    public PrimaryKeyAttribute() { }
    public PrimaryKeyAttribute( int order ) {
        Order = order;
    }
    public int? Order { get; }
}

[AttributeUsage( AttributeTargets.Property )]
public sealed class GeneratedKeyAttribute: Attribute { }

[AttributeUsage( AttributeTargets.Property )]
public sealed class TenantKeyAttribute: Attribute { }

public interface IHasTenantKey<TTenantKey> {
    [Obsolete]
    void SetTenantKeyUnsafe( TTenantKey tenantKey );
}

public interface IHasOptionalTenantKey<TTenantKey> {
    [Obsolete]
    void SetOptionalTenantKeyUnsafe( TTenantKey tenantKey );
}

public interface IHasPrimaryKeyFilter<T> {
    static abstract Expression<Func<T, bool>> GetFilterExpr( T obj );
}

/// <summary>
/// An entity whose updates apply only to the version they were read at, and move it on by one. Implemented by the
/// COW generator for a class that declares it; the change tracker uses these members to guard the update.
/// </summary>
public interface IVersioned<T> where T : IVersioned<T> {
    int Version { get; }

    static abstract Expression<Func<T, int>> VersionExpr { get; }

    /// <summary>Sets the version on an object being updated, to the value the update writes.</summary>
    [Obsolete( "Not obsolete, but only for the change tracker" )]
    static abstract void SetVersionUnsafe( T obj, int version );
}


public interface IHasPrimaryKey<T, TKey>: IHasPrimaryKeyFilter<T> {
    [Obsolete]
    void SetPrimaryKeyUnsafe( TKey key );
}

public interface IUpdatable<T> where T : class {
    // Note: Not public
    T CloneForUpdate();

    static abstract Task<TChangeTracker> CopyChangesAndResolveAssociations<TChangeTracker>( TChangeTracker changeTracker, IAssociationLookup associationLookup, T src )
        where TChangeTracker : ChangeTracker<T, TChangeTracker>;

    static abstract Task<TResult> ExecuteUpdateAsync<TResult>( IUpdateContext<TResult> updateContext );
}


public interface IInsertable<T> {
    static abstract Task ResolveAssociations( IAssociationLookup associationLookup, T target );
}

public interface INotInsertable { }


public class AssociationLookupException( string associationTypeName ): Exception() {
    public string AssociationTypeName { get; } = associationTypeName;
}

public interface IAssociationLookup {
    public Task<T> GetAssociation<T>( Expression<Func<T, bool>> lookUpExpr ) where T : class;
}

