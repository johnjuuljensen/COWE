using System;
using System.Linq.Expressions;
using System.Threading.Tasks;

using COWEntities;

public interface IUpdateContext<TResult> {
    public delegate Expression<Func<TEntity, bool>> DGetFilter<TEntity, TKey>( TKey key );

    Task<TResult> ExecuteUpdateAsync<TEntity, TKey>( DGetFilter<TEntity,TKey> getFilter )
        where TEntity : class, IUpdatable<TEntity>;
}


