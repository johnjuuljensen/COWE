using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace COWEntities;

public class HandlerStore<TExecutor> {
    protected static readonly Dictionary<string, TExecutor> ms_handlers = new( StringComparer.InvariantCultureIgnoreCase );

    protected static bool RegisterHandler<T>( TExecutor executor, string? nameOverload = null ) where T : class {
        return ms_handlers.TryAdd( nameOverload ?? typeof( T ).Name, executor );
    }

    public delegate TResult DMapNoHandler<TResult>();
}

public class UpdateHandlerStore: HandlerStore<UpdateHandlerStore.IUpdateExecutor> {
    public interface IUpdateExecutor {
        Task<TResult> RunAsync<TResult>( IUpdateContext<TResult> updateContext );
        Type EntityType { get; }
    }

    class UpdateExecutor<T>: IUpdateExecutor
        where T : class, IUpdatable<T> {
        public Task<TResult> RunAsync<TResult>( IUpdateContext<TResult> updateContext ) =>
            T.ExecuteUpdateAsync<TResult>( updateContext );

        public Type EntityType => typeof( T );
    }

    public static bool RegisterUpdatable<T>( string? nameOverload = null ) where T : class, IUpdatable<T> =>
        RegisterHandler<T>( new UpdateExecutor<T>(), nameOverload );

    public static Task<TResult> InvokeUpdateHandlerAsync<TResult>( string entityName, IUpdateContext<TResult> updateContext, DMapNoHandler<TResult> mapNoHandler ) =>
        ms_handlers.TryGetValue( entityName, out var handler )
        ? handler.RunAsync( updateContext )
        : Task.FromResult( mapNoHandler() );

    public static IEnumerable<(string EntityName, TResult Result)> Select<TResult>( IUpdateContext<TResult> context ) =>
        ms_handlers.OrderBy( kv => kv.Key ).Select( kv => (kv.Key, kv.Value.RunAsync( context ).Result) );

    public static IEnumerable<KeyValuePair<string, IUpdateExecutor>> Handlers => ms_handlers;

}
