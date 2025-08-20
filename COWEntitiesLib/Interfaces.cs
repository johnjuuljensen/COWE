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


public interface IHasPrimaryKey<T, TKey>: IHasPrimaryKeyFilter<T> {
    [Obsolete]
    void SetPrimaryKeyUnsafe( TKey key );
}

public interface IUpdatable<T> where T : class {
    // Note: Not public
    T CloneForUpdate();

    static abstract TChangeTracker CopyChangesAndResolveAssociations<TChangeTracker>( TChangeTracker changeTracker, IAssociationLookup associationLookup, T src )
        where TChangeTracker : ChangeTracker<T, TChangeTracker>;

    static abstract Task<TResult> ExecuteUpdateAsync<TResult>( IUpdateContext<TResult> updateContext );
}


public interface IInsertable<T> { }

public interface INotInsertable { }

public interface IAssociationLookup {
    public T GetAssociation<T>( Expression<Func<T, bool>> lookUpExpr ) where T : class;
}

