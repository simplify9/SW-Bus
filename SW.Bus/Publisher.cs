using RabbitMQ.Client;
using SW.PrimitiveTypes;
using System.Threading.Tasks;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus
{
    internal class Publisher : IPublish
    {
        private readonly BasicPublisher basicPublisher;
        private readonly string exchange;
        public Publisher(BasicPublisher basicPublisher, string exchange)
        {
            this.basicPublisher = basicPublisher;
            this.exchange = exchange;
        }
        public Task Publish<TMessage>(TMessage message) => 
            basicPublisher.Publish(message,exchange);
        public Task Publish(string messageTypeName, string message) =>
            basicPublisher.Publish(messageTypeName, message, exchange);
        public Task Publish(string messageTypeName, byte[] message) =>
            basicPublisher.Publish(messageTypeName, message, exchange);
    }
}