using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EasyNetQ.Management.Client;
using EasyNetQ.Management.Client.Model;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus;

/// <summary>
/// Provides read-only access to error queues (retry and bad/failed queues) in RabbitMQ using the Management API.
/// Allows inspection of failed messages without removing them from the queue, which is useful for debugging and monitoring.
/// </summary>
public class ErrorQueueReader : IErrorQueueReader
{
    private readonly ConsumerDiscovery consumerDiscovery;
    private readonly ManagementClient managementClient;
    private readonly Vhost vhost;

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorQueueReader"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client used for RabbitMQ management API calls (injected and managed by IHttpClientFactory).</param>
    /// <param name="busOptions">The bus configuration options including management API credentials and virtual host.</param>
    /// <param name="consumerDiscovery">Service for discovering registered consumers and their queue definitions.</param>
    public ErrorQueueReader(HttpClient httpClient, BusOptions busOptions, ConsumerDiscovery consumerDiscovery)
    {
        this.consumerDiscovery = consumerDiscovery;
        managementClient = new ManagementClient(httpClient, busOptions.ManagementUsername, busOptions.ManagementPassword);
        vhost = new Vhost(busOptions.VirtualHost);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ErrorMessage>> Peek<TConsumer, TMessage>(ErrorQueueType queueType, int count = 10) 
        where TConsumer : IConsume<TMessage> where TMessage : class
    {
        var messageName = typeof(TMessage).Name;
        return await PeekInternal(typeof(TConsumer), messageName, queueType, count);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ErrorMessage>> Peek<TConsumer>(string messageName, ErrorQueueType queueType, int count = 10) 
        where TConsumer : IConsume
    {
        return await PeekInternal(typeof(TConsumer), messageName, queueType, count);
    }

    /// <summary>
    /// Internal helper method that resolves the error queue name from consumer discovery and peeks messages.
    /// Throws an exception if the consumer definition cannot be found.
    /// </summary>
    /// <param name="consumerType">The type of the consumer.</param>
    /// <param name="messageName">The name of the message type.</param>
    /// <param name="queueType">The type of error queue to peek (Retry or Bad).</param>
    /// <param name="count">Maximum number of messages to retrieve.</param>
    /// <returns>A collection of <see cref="ErrorMessage"/> objects from the error queue.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no consumer definition is found.</exception>
    private async Task<IEnumerable<ErrorMessage>> PeekInternal(Type consumerType, string messageName, ErrorQueueType queueType, int count)
    {
        if (consumerType == null)
            throw new ArgumentNullException(nameof(consumerType));
        
        if (string.IsNullOrWhiteSpace(messageName))
            throw new ArgumentException("Message name cannot be null or empty.", nameof(messageName));

        var definitions = await consumerDiscovery.Load(true);
        var definition = definitions.FirstOrDefault(d => d.ServiceType == consumerType && d.MessageTypeName == messageName);

        if (definition == null) 
            throw new InvalidOperationException(
                $"No consumer definition found for {consumerType.Name} handling message type '{messageName}'. " +
                $"Ensure the consumer is registered via AddBusConsume().");

        string targetQueue = queueType == ErrorQueueType.Retry 
            ? definition.RetryQueueName 
            : definition.BadQueueName;

        if (string.IsNullOrEmpty(targetQueue))
            throw new InvalidOperationException(
                $"The {queueType} queue name is not configured for consumer {consumerType.Name} handling message '{messageName}'.");

        return await PeekByQueueName(targetQueue, count);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ErrorMessage>> PeekByQueueName(string queueName, int count = 10)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            throw new ArgumentException("Queue name cannot be null or empty.", nameof(queueName));
        
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be greater than zero.");
        
        if (count > 100)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot exceed 100 messages to prevent excessive load on RabbitMQ.");

        try
        {
            var getMessagesRequest = new GetMessagesFromQueueInfo(count, AckMode.AckRequeueTrue);
            var messages = await managementClient.GetMessagesFromQueueAsync(vhost, queueName, getMessagesRequest);

            return messages.Select(m => new ErrorMessage(
                RawBody: m.Payload,
                Exchange: m.Exchange,
                RoutingKey: m.RoutingKey,
                Properties: m.Properties
            ));
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Failed to peek messages from queue '{queueName}'. The queue may not exist, or the RabbitMQ Management API is unavailable.", 
                ex);
        }
    }
}