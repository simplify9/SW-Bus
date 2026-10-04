using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SW.PrimitiveTypes;

namespace SW.Bus.IntegrationTests;

/// <summary>Singleton recording the bus values each "carried" message arrived with.</summary>
public class ReceivedValues
{
    public ConcurrentQueue<IReadOnlyDictionary<string, string>> Messages { get; } = new();
}

/// <summary>Records the <see cref="RequestValueType.ServiceBusValue"/> values its request context holds.</summary>
public class ValuesConsumer(RequestContext requestContext, ReceivedValues received) : IConsume
{
    public Task<IEnumerable<string>> GetMessageTypeNames() => Task.FromResult<IEnumerable<string>>(["carried"]);

    public Task Process(string messageTypeName, string message)
    {
        received.Messages.Enqueue(requestContext.Values
            .Where(v => v.Type == RequestValueType.ServiceBusValue)
            .ToDictionary(v => v.Name, v => v.Value));
        return Task.CompletedTask;
    }
}
