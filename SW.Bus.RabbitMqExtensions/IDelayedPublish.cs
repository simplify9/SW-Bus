namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Publishes a message to a single, specific consumer queue after a delay.
/// </summary>
/// <remarks>
/// Unlike <c>IPublish</c>, which routes by message type and therefore fans out to
/// every consumer queue bound to that type, <see cref="IDelayedPublish"/> routes by the
/// target consumer's <em>naked queue name</em> (<c>{ConsumerClass}.{MessageType}</c>), so the
/// message is delivered to exactly one consumer.
/// <para>
/// Delivery uses the RabbitMQ Delayed Message Exchange plugin when it is available on the
/// broker; otherwise it falls back to TTL "delay buckets" (per-duration queues created on the
/// fly that dead-letter the message onto the target once their time-to-live expires).
/// </para>
/// </remarks>
public interface IDelayedPublish
{
    /// <summary>
    /// Publishes <paramref name="message"/> to a single consumer after <paramref name="delay"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type. Its name forms the second half of the target queue name.</typeparam>
    /// <param name="message">The message to deliver.</param>
    /// <param name="consumerName">
    /// The target consumer's class name (e.g. <c>"OrderCreatedConsumer"</c>). Combined with the
    /// message type name to form the naked queue name <c>{consumerName}.{TMessage}</c>.
    /// </param>
    /// <param name="delay">How long to wait before the message becomes visible to the consumer.</param>
    Task PublishDelayed<TMessage>(TMessage message, string consumerName, TimeSpan delay);

    /// <summary>
    /// Raw overload: publishes a pre-serialized body to a specific consumer queue after <paramref name="delay"/>.
    /// </summary>
    /// <param name="messageTypeName">The message type name carried with the message.</param>
    /// <param name="body">The serialized message body.</param>
    /// <param name="nakedQueueName">
    /// The fully-qualified naked queue name of the target consumer (<c>{ConsumerClass}.{MessageType}</c>, case-insensitive).
    /// </param>
    /// <param name="delay">How long to wait before the message becomes visible to the consumer.</param>
    Task PublishDelayed(string messageTypeName, string body, string nakedQueueName, TimeSpan delay);
}
