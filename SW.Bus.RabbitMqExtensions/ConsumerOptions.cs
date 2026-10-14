namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Represents configuration options for a consumer.
/// </summary>
public class ConsumerOptions
{
    /// <summary>
    /// Gets or sets the number of messages to prefetch.
    /// </summary>
    public ushort? Prefetch { get; set; }

    /// <summary>
    /// Gets or sets the priority of the consumer. Higher values indicate higher priority.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Gets or sets how many messages of this queue are processed at the same time on one node.
    /// 1 processes them one at a time, in the order they arrive. Above 1 they run in parallel and
    /// their order is not kept. Prefetch still caps it: a node never holds more than prefetch
    /// unacknowledged messages. When null, <c>BusOptions.DefaultConsumerConcurrency</c> applies.
    /// </summary>
    public ushort? Concurrency { get; set; }
}