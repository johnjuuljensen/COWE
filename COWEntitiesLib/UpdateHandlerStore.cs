﻿using System.Collections.Generic;
using System.Threading.Tasks;

namespace COWEntities;

public class HandlerStore<TExecutor> {
    protected static readonly Dictionary<string, TExecutor> ms_handlers = new();

    protected static bool RegisterHandler<T>( TExecutor executor, string? nameOverload = null ) where T : class {
        return ms_handlers.TryAdd( nameOverload ?? typeof( T ).Name.ToUpperInvariant(), executor );
    }

    public delegate TResult DMapNoHandler<TResult>();
}

public class UpdateHandlerStore: HandlerStore<UpdateHandlerStore.IUpdateExecutor> {
    public interface IUpdateExecutor {
        Task<TResult> RunAsync<TResult>( IUpdateContext<TResult> updateContext );
    }

    class UpdateExecutor<T>: IUpdateExecutor
        where T : class, IUpdatable<T> {
        public Task<TResult> RunAsync<TResult>( IUpdateContext<TResult> updateContext ) =>
            T.ExecuteUpdateAsync<TResult>( updateContext );
    }

    public static bool RegisterUpdatable<T>( string? nameOverload = null ) where T : class, IUpdatable<T> =>
        RegisterHandler<T>( new UpdateExecutor<T>(), nameOverload );

    public static Task<TResult> InvokeUpdateHandlerAsync<TResult>( string entityName, IUpdateContext<TResult> updateContext, DMapNoHandler<TResult> mapNoHandler ) =>
        ms_handlers.TryGetValue( entityName.ToUpperInvariant(), out var handler )
        ? handler.RunAsync( updateContext )
        : Task.FromResult( mapNoHandler() );

    //public static IEnumerable<(string EntityName, TResult Result)> Select<TResult>( IQueryContext<TResult> queryContext ) =>
    //    ms_UpdateHandlers.OrderBy( kv => kv.Key ).Select( kv => (kv.Key, kv.Value.Run( queryContext )) );
}
