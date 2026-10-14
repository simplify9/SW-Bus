using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// Singleton a test fills before the host starts: the message types <see cref="WorkConsumer"/> consumes,
/// with their options and what processing one does, and a record of how they were processed.
/// Naked queue names resolve to "workconsumer.{type}".
/// </summary>
public class WorkPlan
{
    public ConcurrentDictionary<string, ConsumerOptions> Types { get; } = new();
    public ConcurrentDictionary<string, Func<string, Task>> Handlers { get; } = new();
    public ConcurrentDictionary<string, WorkLog> Logs { get; } = new();

    public WorkLog Log(string type) => Logs.GetOrAdd(type, _ => new WorkLog());
}

public class WorkLog
{
    private int running;
    private int maxRunning;

    public int MaxRunning => Volatile.Read(ref maxRunning);
    public ConcurrentQueue<string> Started { get; } = new();
    public ConcurrentQueue<string> Completed { get; } = new();
    public ConcurrentQueue<string> RemainingRetries { get; } = new();

    internal void Enter()
    {
        var now = Interlocked.Increment(ref running);
        int seen;
        while (now > (seen = Volatile.Read(ref maxRunning)) &&
               Interlocked.CompareExchange(ref maxRunning, now, seen) != seen)
        {
        }
    }

    internal void Exit() => Interlocked.Decrement(ref running);
}

public class WorkConsumer(WorkPlan plan, RequestContext requestContext) : IConsumeExtended
{
    public Task<IEnumerable<string>> GetMessageTypeNames() => Task.FromResult(plan.Types.Keys.AsEnumerable());

    public Task<IDictionary<string, ConsumerOptions>> GetMessageTypeNamesWithOptions() =>
        Task.FromResult<IDictionary<string, ConsumerOptions>>(plan.Types.ToDictionary(t => t.Key, t => t.Value));

    public async Task Process(string messageTypeName, string message)
    {
        var log = plan.Log(messageTypeName);
        log.Started.Enqueue(message);
        log.RemainingRetries.Enqueue(requestContext.Values.FirstOrDefault(v => v.Name == "RemainingRetries")?.Value ?? "");
        log.Enter();
        try
        {
            if (plan.Handlers.TryGetValue(messageTypeName, out var handler))
                await handler(message);
            log.Completed.Enqueue(message);
        }
        finally
        {
            log.Exit();
        }
    }
}
