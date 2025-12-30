using SW.PrimitiveTypes;

namespace SW.Bus.RabbitMqExtensions;

public interface IRabbitMqPublish : IPublish
{
    Task Publish<TMessage>(TMessage message, byte priority);
    Task Publish(string messageTypeName, string message, byte priority);
    Task Publish(string messageTypeName, byte[] message, byte priority);
}