using SW.PrimitiveTypes;

namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Represents the statistics and configuration of a consumer.
/// </summary>
/// <param name="Name">The name of the consumer service (e.g., "OrderService").</param>
/// <param name="MessageName">The name of the message type being consumed (e.g., "OrderCreated").</param>
/// <param name="TotalNodes">The total number of active consumer instances (nodes) listening to the queue.</param>
/// <param name="ProcessingCount">The number of messages currently being processed (unacknowledged) by consumers.</param>
/// <param name="QueueCount">The number of messages waiting in the main queue to be processed.</param>
/// <param name="RetryCount">The number of messages currently in the retry queue waiting for redelivery.</param>
/// <param name="FailedCount">The number of messages in the bad/failed queue that could not be processed.</param>
/// <param name="Priority">The priority level of the consumer (higher values indicate higher priority).</param>
/// <param name="Prefetch">The number of messages pre-fetched by the consumer (QoS).</param>
/// <param name="IncomingRate">The rate at which messages are entering the queue (messages per second). Example: 5.2 msg/s.</param>
/// <param name="ProcessingRate">The rate at which messages are being delivered to consumers (messages per second). Example: 4.8 msg/s.</param>
/// <param name="AckRate">The rate at which messages are being successfully acknowledged by consumers (messages per second). Example: 4.8 msg/s.</param>
public record ConsumerCount(
    string Name,
    string MessageName,
    long TotalNodes,
    long ProcessingCount,
    long QueueCount,
    long RetryCount,
    long FailedCount,
    int Priority,
    ushort Prefetch,
    double IncomingRate,
    double ProcessingRate,
    double AckRate
);

/// <summary>
/// Interface for reading consumer statistics and configuration.
/// </summary>
public interface IConsumerReader
{
    /// <summary>
    /// Gets the consumer count and statistics for a specific consumer and message name.
    /// </summary>
    /// <typeparam name="TConsumer">The type of the consumer.</typeparam>
    /// <param name="messageName">The name of the message.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the consumer count statistics.</returns>
    public Task<ConsumerCount> GetConsumerCount<TConsumer>(string messageName) where TConsumer : IConsume;

    /// <summary>
    /// Gets the consumer counts and statistics for all messages handled by a specific consumer type.
    /// </summary>
    /// <typeparam name="TConsumer">The type of the consumer.</typeparam>
    /// <returns>A task that represents the asynchronous operation. The task result contains an array of consumer count statistics.</returns>
    public Task<ConsumerCount[]> GetConsumerCount<TConsumer>() where TConsumer : IConsume;

    /// <summary>
    /// Gets the consumer count and statistics for a specific typed consumer and message type.
    /// </summary>
    /// <typeparam name="TTypedConsumer">The type of the typed consumer.</typeparam>
    /// <typeparam name="TMessage">The type of the message.</typeparam>
    /// <returns>A task that represents the asynchronous operation. The task result contains the consumer count statistics.</returns>
    public Task<ConsumerCount> GetConsumerCount<TTypedConsumer, TMessage>()
        where TTypedConsumer : IConsume<TMessage> where TMessage : class;

    /// <summary>
    /// Gets the consumer counts and statistics for all registered consumers.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains an array of all consumer count statistics.</returns>
    public Task<ConsumerCount[]> GetAllConsumersCount();
}
