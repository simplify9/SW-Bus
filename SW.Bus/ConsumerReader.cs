using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EasyNetQ.Management.Client;
using EasyNetQ.Management.Client.Model;
using Microsoft.Extensions.Caching.Memory;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus;

/// <summary>
/// Provides access to RabbitMQ consumer statistics and queue metrics using the RabbitMQ Management API.
/// Implements caching to prevent excessive API calls and reduce load on RabbitMQ.
/// Cache duration is configurable via <see cref="BusOptions.MonitoringCacheSeconds"/> (default: 5 seconds, range: 3-60 seconds).
/// </summary>
public class ConsumerReader : IConsumerReader
{
    private readonly BusOptions busOptions;
    private readonly ConsumerDiscovery consumerDiscovery;
    private readonly ManagementClient managementClient;
    private readonly Vhost vhost;
    private readonly IMemoryCache memoryCache;
    
    /// <summary>
    /// Tracks the timestamp of the last successful fetch from RabbitMQ management API.
    /// Used to monitor cache freshness and API availability.
    /// </summary>
    private DateTime lastUpdatedUtc = DateTime.MinValue;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerReader"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client used for RabbitMQ management API calls (injected and managed by IHttpClientFactory).</param>
    /// <param name="busOptions">The bus configuration options including management API credentials, virtual host, and cache duration (<see cref="BusOptions.MonitoringCacheSeconds"/>).</param>
    /// <param name="consumerDiscovery">Service for discovering registered consumers and their definitions.</param>
    /// <param name="memoryCache">Memory cache for storing queue statistics with configurable expiration to prevent API abuse.</param>
    public ConsumerReader(
        HttpClient httpClient, 
        BusOptions busOptions, 
        ConsumerDiscovery consumerDiscovery, 
        IMemoryCache memoryCache)
    {
        this.busOptions = busOptions;
        this.consumerDiscovery = consumerDiscovery;
        this.memoryCache = memoryCache;

        // ManagementClient uses the managed HttpClient
        managementClient = new ManagementClient(
            httpClient, 
            busOptions.ManagementUsername, 
            busOptions.ManagementPassword
        );
        
        vhost = new Vhost(busOptions.VirtualHost);
    }

    /// <inheritdoc />
    public Task<DateTime> LastUpdated => Task.FromResult(lastUpdatedUtc);
    
    /// <inheritdoc />
    public async Task<ConsumerCount> GetConsumerCount<TConsumer>(string messageName) where TConsumer : IConsume
    {
        var definitions = await consumerDiscovery.Load(true);
        var definition = definitions.FirstOrDefault(d => d.ServiceType == typeof(TConsumer) && d.MessageTypeName == messageName);

        if (definition == null)
        {
            var queueName = GetQueueName<TConsumer>(messageName);
            return await GetConsumerCount(queueName, typeof(TConsumer).Name, messageName, 0, 0);
        }

        return await GetConsumerCount(definition.QueueName, definition.ServiceType.Name, definition.MessageTypeName, definition.ConsumerPriority ?? 0, definition.QueuePrefetch);
    }

    /// <inheritdoc />
    public async Task<ConsumerCount[]> GetConsumerCount<TConsumer>() where TConsumer : IConsume
    {
        var definitions = await consumerDiscovery.Load(true);
        var filteredDefinitions = definitions.Where(d => d.ServiceType == typeof(TConsumer));
        return await GetConsumerCounts(filteredDefinitions);
    }

    /// <inheritdoc />
    public async Task<ConsumerCount> GetConsumerCount<TTypedConsumer, TMessage>()
        where TTypedConsumer : IConsume<TMessage> where TMessage : class
    {
        var messageName = typeof(TMessage).Name;
        var definitions = await consumerDiscovery.Load(true);
        var definition = definitions.FirstOrDefault(d => d.ServiceType == typeof(TTypedConsumer) && d.MessageTypeName == messageName);
        
        if (definition == null)
        {
            var queueName = GetQueueName<TTypedConsumer>(messageName);
            return await GetConsumerCount(queueName, typeof(TTypedConsumer).Name, messageName, 0, 0);
        }

        return await GetConsumerCount(definition.QueueName, definition.ServiceType.Name, definition.MessageTypeName, definition.ConsumerPriority ?? 0, definition.QueuePrefetch);
    }

    /// <inheritdoc />
    public async Task<ConsumerCount[]> GetAllConsumersCount()
    {
        var definitions = await consumerDiscovery.Load(true);
        return await GetConsumerCounts(definitions);
    }

    /// <summary>
    /// Retrieves consumer counts and statistics for a collection of consumer definitions.
    /// Fetches all queue information in bulk from RabbitMQ management API and maps them to consumer definitions.
    /// Results are cached based on <see cref="BusOptions.MonitoringCacheSeconds"/> to prevent API abuse.
    /// </summary>
    /// <param name="definitions">The collection of consumer definitions to retrieve statistics for.</param>
    /// <returns>An array of <see cref="ConsumerCount"/> objects containing statistics for each consumer.</returns>
    private async Task<ConsumerCount[]> GetConsumerCounts(IEnumerable<ConsumerDefinition> definitions)
    {
        var queues = await memoryCache.GetOrCreateAsync("queues", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(busOptions.MonitoringCacheSeconds);
            var result = await managementClient.GetQueuesAsync(vhost.Name);
            
            // Update the timestamp on a successful fetch
            lastUpdatedUtc = DateTime.UtcNow;
            
            return result;
        });
        
        var queuesMap = queues.ToDictionary(q => q.Name, StringComparer.OrdinalIgnoreCase);

        return definitions.Select(d =>
        {
            queuesMap.TryGetValue(d.QueueName, out var queue);
            queuesMap.TryGetValue(d.RetryQueueName, out var retryQueue);
            queuesMap.TryGetValue(d.BadQueueName, out var badQueue);

            return new ConsumerCount(
                d.ServiceType.Name,
                d.MessageTypeName,
                queue?.Consumers ?? 0,
                queue?.MessagesUnacknowledged ?? 0,
                queue?.MessagesReady ?? 0,
                retryQueue?.Messages ?? 0,
                badQueue?.Messages ?? 0,
                d.ConsumerPriority ?? 0,
                d.QueuePrefetch,
                queue?.MessageStats?.PublishDetails?.Rate ?? 0,
                queue?.MessageStats?.DeliverGetDetails?.Rate ?? 0,
                queue?.MessageStats?.AckDetails?.Rate ?? 0
            );
        }).ToArray();
    }

    /// <summary>
    /// Constructs the queue name for a consumer based on the bus options and consumer/message types.
    /// The queue name follows the pattern: {ProcessExchange}.{ApplicationName}.{ConsumerName}.{MessageName}
    /// </summary>
    /// <typeparam name="TConsumer">The type of the consumer.</typeparam>
    /// <param name="messageName">The name of the message type.</param>
    /// <returns>The fully qualified queue name in lowercase.</returns>
    private string GetQueueName<TConsumer>(string messageName)
    {
        var queueNamePrefix = $"{busOptions.ProcessExchange}{(string.IsNullOrWhiteSpace(busOptions.ApplicationName) ? "" : $".{busOptions.ApplicationName}")}";
        var nakedQueueName = $"{typeof(TConsumer).Name}.{messageName}".ToLower();
        return $"{queueNamePrefix}.{nakedQueueName}".ToLower();
    }

    /// <summary>
    /// Retrieves consumer statistics for a specific queue by name.
    /// Fetches statistics for the main queue, retry queue, and bad/failed queue in parallel.
    /// </summary>
    /// <param name="queueName">The name of the main queue.</param>
    /// <param name="consumerName">The name of the consumer service.</param>
    /// <param name="messageName">The name of the message type.</param>
    /// <param name="priority">The priority level of the consumer.</param>
    /// <param name="prefetch">The prefetch count (QoS) for the consumer.</param>
    /// <returns>A <see cref="ConsumerCount"/> object containing the aggregated statistics.</returns>
    private async Task<ConsumerCount> GetConsumerCount(string queueName, string consumerName, string messageName, int priority, ushort prefetch)
    {
        var queueTask = GetQueueInfo(queueName);
        var retryQueueTask = GetQueueInfo($"{queueName}.retry");
        var badQueueTask = GetQueueInfo($"{queueName}.bad");

        await Task.WhenAll(queueTask, retryQueueTask, badQueueTask);

        var queue = queueTask.Result;
        var retryQueue = retryQueueTask.Result;
        var badQueue = badQueueTask.Result;

        return new ConsumerCount(
            consumerName,
            messageName,
            queue?.Consumers ?? 0,
            queue?.MessagesUnacknowledged ?? 0,
            queue?.MessagesReady ?? 0,
            retryQueue?.Messages ?? 0,
            badQueue?.Messages ?? 0,
            priority,
            prefetch,
            queue?.MessageStats?.PublishDetails?.Rate ?? 0,
            queue?.MessageStats?.DeliverGetDetails?.Rate ?? 0,
            queue?.MessageStats?.AckDetails?.Rate ?? 0
        );
    }

    /// <summary>
    /// Retrieves queue information from RabbitMQ management API with caching.
    /// Individual queue lookups are cached based on <see cref="BusOptions.MonitoringCacheSeconds"/> to minimize API calls.
    /// Returns null if the queue does not exist or an error occurs.
    /// </summary>
    /// <param name="queueName">The name of the queue to retrieve information for.</param>
    /// <returns>A <see cref="Queue"/> object if found, otherwise null.</returns>
    private async Task<Queue> GetQueueInfo(string queueName)
    {
        return await memoryCache.GetOrCreateAsync($"queue_{queueName}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(busOptions.MonitoringCacheSeconds);
            try
            {
                var queue = await managementClient.GetQueueAsync(queueName, vhost.Name);
                
                // Only update if we actually got a queue back
                if (queue != null) lastUpdatedUtc = DateTime.UtcNow;
                
                return queue;
            }
            catch
            {
                return null;
            }
        });
    }
}