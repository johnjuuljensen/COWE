using System;
using System.Linq.Expressions;

namespace COWEntities;

public abstract class ChangeTracker<TObject, TChangeTracker> 
    where TObject: class
    where TChangeTracker: ChangeTracker<TObject, TChangeTracker> 
{
    public delegate TProperty DGetter<TProperty>(TObject obj);
    public delegate void DSetter<TProperty>(TObject obj, TProperty val );

    public abstract TChangeTracker SetProperty<TProperty>( DGetter<TProperty> getter, DSetter<TProperty> setter, Expression<Func<TObject, TProperty>>? propExpr, TProperty val );
}

public abstract class CloningChangeTracker<TObject, TChangeTracker>: ChangeTracker<TObject, TChangeTracker>
    where TObject : class, IUpdatable<TObject>
    where TChangeTracker : ChangeTracker<TObject, TChangeTracker>
    {

    public CloningChangeTracker( TObject original ) {
        Original = original;
    }

    public TObject Original { get; }

    TObject? m_Clone = null;
    protected TObject Clone => m_Clone ??= Original.CloneForUpdate();

    protected bool UpdateClone<TProperty>( DGetter<TProperty> getter, DSetter<TProperty> setter, TProperty val ) {
        if ( !object.Equals( getter( Clone ), val ) ) {
            setter( Clone, val );
            return true;
        }

        return false;
    }
}

public class ObjectChangeTracker<TObject>( TObject original ): CloningChangeTracker<TObject, ObjectChangeTracker<TObject>>( original ) where TObject : class, IUpdatable<TObject> {
    public override ObjectChangeTracker<TObject> SetProperty<TProperty>( DGetter<TProperty> getter, DSetter<TProperty> setter, Expression<Func<TObject, TProperty>>? propExpr, TProperty val ) {
        UpdateClone( getter, setter, val );
        return this;
    }
}

public class ModifyingChangeTracker<TObject>: ChangeTracker<TObject, ModifyingChangeTracker<TObject>>
    where TObject : class {

    public ModifyingChangeTracker( TObject original ) {
        Original = original;
    }

    public TObject Original { get; }

    public override ModifyingChangeTracker<TObject> SetProperty<TProperty>( DGetter<TProperty> getter, DSetter<TProperty> setter, Expression<Func<TObject, TProperty>>? propExpr, TProperty val ) {
        setter( Original, val );
        return this;
    }
}
