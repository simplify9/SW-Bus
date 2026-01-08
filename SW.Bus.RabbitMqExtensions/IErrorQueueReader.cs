using SW.PrimitiveTypes;

namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Represents a message that has been moved to an error queue (retry or bad/failed queue).
/// Contains the original message payload, routing information, and exception history.
/// </summary>
/// <param name="RawBody">The raw JSON or text body of the message as it was originally published.</param>
/// <param name="Exchange">The name of the exchange where the message was originally sent.</param>
/// <param name="RoutingKey">The routing key used when the message was originally published.</param>
/// <param name="Properties">All AMQP message properties including headers, correlation ID, timestamps, etc.</param>
public record ErrorMessage(
    string RawBody,
    string Exchange,
    string RoutingKey,
    IReadOnlyDictionary<string, object?> Properties
)
{
    /// <summary>
    /// Gets the message headers extracted from the AMQP properties.
    /// Headers typically contain retry counts, exception information, and other metadata added during message processing.
    /// </summary>
    public IReadOnlyDictionary<string, object> Headers => 
        Properties.TryGetValue("headers", out var h) && h is IReadOnlyDictionary<string, object> dict 
            ? dict 
            : new Dictionary<string, object>();
    
    /// <summary>
    /// Gets the correlation ID of the message, used for tracing and linking related messages.
    /// Returns null if no correlation ID was set on the message.
    /// </summary>
    public string? CorrelationId => Properties.TryGetValue("correlation_id", out var id) ? id?.ToString() : null;

    /// <summary>
    /// Gets the complete exception history for this message.
    /// Each retry attempt adds a new exception entry to the headers (exception0, exception1, etc.).
    /// Returns exceptions in chronological order from first to last failure.
    /// </summary>
    /// <example>
    /// If a message failed 3 times, this will return an enumerable with 3 exception strings.
    /// </example>
    public IEnumerable<string?> ExceptionHistory => Headers
        .Where(h => h.Key.StartsWith("exception", StringComparison.OrdinalIgnoreCase))
        .OrderBy(h => GetExceptionIndex(h.Key))
        .Select(h => h.Value?.ToString());

    /// <summary>
    /// Gets the most recent exception that caused this message to be moved to the error queue.
    /// Returns null if no exception information is available.
    /// </summary>
    public string? LastException => ExceptionHistory.LastOrDefault();

    private static int GetExceptionIndex(string key) =>
        int.TryParse(key.Replace("exception", "", StringComparison.OrdinalIgnoreCase), out var index) ? index : 0;
}

/// <summary>
/// Specifies the type of error queue to peek messages from.
/// </summary>
public enum ErrorQueueType
{
    /// <summary>
    /// Messages that failed processing but will be retried automatically.
    /// These messages are temporarily in the retry queue and will be reprocessed after a delay.
    /// </summary>
    Retry,
    
    /// <summary>
    /// Messages that have permanently failed after all retry attempts.
    /// These messages require manual intervention or investigation.
    /// Also known as the "dead letter queue" or "poison message queue".
    /// </summary>
    Bad
}

/// <summary>
/// Provides read-only access to error queues (retry and bad/failed queues) in RabbitMQ.
/// Allows peeking at messages that have failed processing without removing them from the queue.
/// </summary>
public interface IErrorQueueReader
{
    /// <summary>
    /// Peeks messages from an error queue for a strongly-typed consumer.
    /// Messages are not removed from the queue and will remain for retry or manual intervention.
    /// </summary>
    /// <typeparam name="TConsumer">The consumer type that processes messages (must implement <see cref="IConsume{TMessage}"/>).</typeparam>
    /// <typeparam name="TMessage">The message type being consumed.</typeparam>
    /// <param name="queueType">The type of error queue to peek (Retry or Bad).</param>
    /// <param name="count">Maximum number of messages to retrieve. Default is 10.</param>
    /// <returns>A collection of <see cref="ErrorMessage"/> objects from the error queue.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no consumer definition is found for the specified types.</exception>
    /// <example>
    /// <code>
    /// // Peek at failed order messages in the retry queue
    /// var messages = await errorReader.Peek&lt;OrderConsumer, OrderCreatedEvent&gt;(ErrorQueueType.Retry, 20);
    /// </code>
    /// </example>
    Task<IEnumerable<ErrorMessage>> Peek<TConsumer, TMessage>(ErrorQueueType queueType, int count = 10) 
        where TConsumer : IConsume<TMessage> 
        where TMessage : class;

    /// <summary>
    /// Peeks messages from an error queue for a multi-message consumer.
    /// Used when the consumer implements <see cref="IConsume"/> and handles multiple message types.
    /// Messages are not removed from the queue and will remain for retry or manual intervention.
    /// </summary>
    /// <typeparam name="TConsumer">The consumer type that processes messages (must implement <see cref="IConsume"/>).</typeparam>
    /// <param name="messageName">The name of the message type (e.g., "OrderCreatedEvent").</param>
    /// <param name="queueType">The type of error queue to peek (Retry or Bad).</param>
    /// <param name="count">Maximum number of messages to retrieve. Default is 10.</param>
    /// <returns>A collection of <see cref="ErrorMessage"/> objects from the error queue.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no consumer definition is found for the specified consumer and message name.</exception>
    /// <example>
    /// <code>
    /// // Peek at failed messages in the bad queue for a multi-message consumer
    /// var messages = await errorReader.Peek&lt;GenericOrderConsumer&gt;("OrderCreatedEvent", ErrorQueueType.Bad, 10);
    /// </code>
    /// </example>
    Task<IEnumerable<ErrorMessage>> Peek<TConsumer>(string messageName, ErrorQueueType queueType, int count = 10) 
        where TConsumer : IConsume;

    /// <summary>
    /// Peeks messages directly from a queue by its name.
    /// Provides low-level access when the queue name is known but not discovered via consumer types.
    /// Messages are not removed from the queue and will remain for retry or manual intervention.
    /// </summary>
    /// <param name="queueName">The full name of the queue to peek (e.g., "process.myapp.orderconsumer.ordercreatedevent.retry").</param>
    /// <param name="count">Maximum number of messages to retrieve. Default is 10.</param>
    /// <returns>A collection of <see cref="ErrorMessage"/> objects from the queue.</returns>
    /// <example>
    /// <code>
    /// // Peek at messages from a specific retry queue
    /// var messages = await errorReader.PeekByQueueName("process.myapp.orderconsumer.ordercreatedevent.retry", 5);
    /// </code>
    /// </example>
    Task<IEnumerable<ErrorMessage>> PeekByQueueName(string queueName, int count = 10);
}