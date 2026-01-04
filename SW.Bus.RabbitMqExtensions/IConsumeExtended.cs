using SW.PrimitiveTypes;

namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Interface for consumers that require extended configuration options for multiple message types.
/// </summary>
public interface IConsumeExtended : IConsume
{
    /// <summary>
    /// Gets the message type names and their corresponding consumer options.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a dictionary of message type names and their consumer options.</returns>
    Task<IDictionary<string,ConsumerOptions>> GetMessageTypeNamesWithOptions();
}

/// <summary>
/// Interface for typed consumers that require extended configuration options.
/// </summary>
/// <typeparam name="TMessage">The type of the message being consumed.</typeparam>
public interface IConsumeExtended<TMessage> : IConsume<TMessage> where TMessage : class
{
    /// <summary>
    /// Gets the consumer options for the specific message type.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains the consumer options.</returns>
    Task<ConsumerOptions> GetConsumerOptions();
}