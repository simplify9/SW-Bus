using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SW.PrimitiveTypes;

namespace SW.Bus.IntegrationTests;

/// <summary>Singleton holding the message type names <see cref="DynamicConsumer"/> currently consumes.</summary>
public class DynamicMessageTypes
{
    public ConcurrentDictionary<string, bool> Names { get; } = new(new Dictionary<string, bool>
    {
        ["kept"] = true,
        ["removed"] = true
    });
}

/// <summary>
/// A multi-message consumer whose message types can change at runtime, the way an application's
/// database-driven consumers do. Naked queue names resolve to "dynamicconsumer.{name}".
/// </summary>
public class DynamicConsumer : IConsume
{
    private readonly DynamicMessageTypes types;

    public DynamicConsumer(DynamicMessageTypes types) => this.types = types;

    public Task<IEnumerable<string>> GetMessageTypeNames() => Task.FromResult(types.Names.Keys.AsEnumerable());

    public Task Process(string messageTypeName, string message) => Task.CompletedTask;
}
