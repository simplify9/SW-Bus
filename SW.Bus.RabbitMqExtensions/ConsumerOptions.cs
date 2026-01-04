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
}