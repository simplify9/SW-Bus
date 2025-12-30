namespace SW.Bus.RabbitMqExtensions;

public class QueueOptions:ConsumerOptions
{
    public int? RetryCount { get; set; }
    public uint? RetryAfterSeconds { get; set; }
    public IDictionary<string, object>? ConsumerArgs => Priority is null or 0 ? null : new Dictionary<string, object>
    {
        { "x-priority", Priority},
    };
}