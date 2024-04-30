using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SW.PrimitiveTypes;

namespace SW.Bus;
internal class Broadcaster : IBroadcast
{
    internal const string RefreshConsumersMessageBody = "refresh_consumers";

    private readonly BasicPublisher basicPublisher;
    private readonly string exchange;
    private readonly string nodeRoutingKey;

    public Broadcaster(BasicPublisher basicPublisher, string exchange, string nodeRoutingKey)
    {
        this.basicPublisher = basicPublisher;
        this.exchange = exchange;
        this.nodeRoutingKey = nodeRoutingKey;
    }

    public Task Broadcast<TMessage>(TMessage message)
    {
        var serializerOptions = new JsonSerializerOptions()
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };
        var publishMessage = JsonSerializer.Serialize(message,serializerOptions);

        return basicPublisher.Publish(nodeRoutingKey, publishMessage,exchange);
    }

    public Task RefreshConsumers() => basicPublisher.Publish(nodeRoutingKey, RefreshConsumersMessageBody,exchange);
    
    
}