namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Represents configuration options for a queue, including retry policies.
/// </summary>
public class QueueOptions:ConsumerOptions
{
    /// <summary>
    /// Gets or sets the number of times to retry processing a message before moving it to the dead letter queue.
    /// </summary>
    public int? RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the delay in seconds before retrying a failed message.
    /// </summary>
    public uint? RetryAfterSeconds { get; set; }

    /// <summary>
    /// Gets the arguments for the consumer, such as priority.
    /// </summary>
    public IDictionary<string, object>? ConsumerArgs => Priority is null or 0 ? null : new Dictionary<string, object>
    {
        { "x-priority", Priority},
    };
}