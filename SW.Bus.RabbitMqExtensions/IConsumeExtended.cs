using SW.PrimitiveTypes;

namespace SW.Bus.RabbitMqExtensions;

public interface IConsumeExtended : IConsume
{
    Task<IDictionary<string,ConsumerOptions>> GetMessageTypeNamesWithOptions();
}