using System.Threading.Tasks;
using SW.PrimitiveTypes;

namespace SW.Bus.IntegrationTests;

/// <summary>
/// Target consumer for delayed-publish tests. Naked queue name resolves to "delayedconsumer.delaydto".
/// </summary>
public class DelayedConsumer : IConsume<DelayDto>
{
    private readonly MessageSink sink;

    public DelayedConsumer(MessageSink sink) => this.sink = sink;

    public Task Process(DelayDto message)
    {
        sink.Record(message.Id);
        return Task.CompletedTask;
    }
}
