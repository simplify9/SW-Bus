using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RabbitMQ.Client;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus;

/// <summary>
/// Delivers a message to a single, specific consumer queue after a delay.
/// Uses the Delayed Message Exchange plugin when available; otherwise falls back to TTL delay buckets.
/// </summary>
internal class DelayedPublisher : IDelayedPublish
{
    // x-delay header understood by the rabbitmq_delayed_message_exchange plugin (milliseconds).
    private const string DelayHeader = "x-delay";

    // Buckets declared so far on this connection. Declares are idempotent; this just avoids re-declaring.
    private static readonly ConcurrentDictionary<string, byte> DeclaredBuckets = new();

    private readonly BasicPublisher basicPublisher;
    private readonly BusOptions busOptions;
    private readonly IModel model;

    public DelayedPublisher(BasicPublisher basicPublisher, BusOptions busOptions, IModel model)
    {
        this.basicPublisher = basicPublisher;
        this.busOptions = busOptions;
        this.model = model;
    }

    public Task PublishDelayed<TMessage>(TMessage message, string consumerName, TimeSpan delay)
    {
        var serializerOptions = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles };
        var messageTypeName = message.GetType().Name;
        var body = JsonSerializer.Serialize(message, message.GetType(), serializerOptions);
        var nakedQueueName = $"{consumerName}.{messageTypeName}";
        return PublishDelayed(messageTypeName, body, nakedQueueName, delay);
    }

    public Task PublishDelayed(string messageTypeName, string body, string nakedQueueName, TimeSpan delay)
    {
        var routingKey = nakedQueueName.ToLower();

        // No delay: deliver straight to the targeted consumer via the direct router.
        if (delay <= TimeSpan.Zero)
            return basicPublisher.Publish(messageTypeName, body, busOptions.DelayExchange, routingKeyOverride: routingKey);

        if (busOptions.DelayedPluginAvailable)
        {
            var delayMs = (int)Math.Min(delay.TotalMilliseconds, int.MaxValue);
            var headers = new Dictionary<string, object> { { DelayHeader, delayMs } };
            return basicPublisher.Publish(messageTypeName, body, busOptions.DelayPluginExchange,
                routingKeyOverride: routingKey, extraHeaders: headers);
        }

        // TTL-bucket fallback: round the delay up to the nearest bucket, publish to that bucket's fanout
        // entry exchange (which preserves the original routing key), wait for TTL expiry, then
        // dead-letter onto DelayExchange where the key routes to the one target consumer queue.
        var bucketSeconds = busOptions.ResolveDelayBucketSeconds(delay);
        DeclareBucket(bucketSeconds);
        return basicPublisher.Publish(messageTypeName, body,
            busOptions.DelayBucketEntryExchange(bucketSeconds), routingKeyOverride: routingKey);
    }

    private void DeclareBucket(int bucketSeconds)
    {
        var entryExchange = busOptions.DelayBucketEntryExchange(bucketSeconds);
        if (!DeclaredBuckets.TryAdd(entryExchange, 0))
            return;

        var queue = busOptions.DelayBucketQueue(bucketSeconds);

        // Fanout so the message lands in the TTL queue regardless of routing key, but the routing key
        // is preserved on the message envelope. On TTL expiry RabbitMQ dead-letters using that same
        // key onto DelayExchange, which routes it to the correct consumer queue.
        model.ExchangeDeclare(entryExchange, ExchangeType.Fanout, durable: true, autoDelete: false);
        model.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false, new Dictionary<string, object>
        {
            { "x-message-ttl", bucketSeconds * 1000 },
            { "x-dead-letter-exchange", busOptions.DelayExchange }
            // No x-dead-letter-routing-key: RabbitMQ keeps the original routing key on dead-letter.
        });
        model.QueueBind(queue, entryExchange, string.Empty);
    }
}
