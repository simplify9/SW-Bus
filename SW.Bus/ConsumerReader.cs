using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EasyNetQ.Management.Client;
using EasyNetQ.Management.Client.Model;
using Microsoft.Extensions.Caching.Memory;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus;

public class ConsumerReader : IConsumerReader
{
    private readonly BusOptions busOptions;
    private readonly ConsumerDiscovery consumerDiscovery;
    private readonly ManagementClient managementClient;
    private readonly Vhost vhost;
    private readonly IMemoryCache memoryCache;

    public ConsumerReader(BusOptions busOptions, ConsumerDiscovery consumerDiscovery, IMemoryCache memoryCache)
    {
        this.busOptions = busOptions;
        this.consumerDiscovery = consumerDiscovery;
        this.memoryCache = memoryCache;
        var uri = new Uri(busOptions.ManagementUrl);
        var httpClient = new System.Net.Http.HttpClient { BaseAddress = uri };
        managementClient = new ManagementClient(httpClient, busOptions.ManagementUsername, busOptions.ManagementPassword);
        vhost = new Vhost(busOptions.VirtualHost);
    }

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

    public async Task<ConsumerCount[]> GetConsumerCount<TConsumer>() where TConsumer : IConsume
    {
        var definitions = await consumerDiscovery.Load(true);
        var filteredDefinitions = definitions.Where(d => d.ServiceType == typeof(TConsumer));
        return await GetConsumerCounts(filteredDefinitions);
    }

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

    public async Task<ConsumerCount[]> GetAllConsumersCount()
    {
        var definitions = await consumerDiscovery.Load(true);
        return await GetConsumerCounts(definitions);
    }

    private async Task<ConsumerCount[]> GetConsumerCounts(IEnumerable<ConsumerDefinition> definitions)
    {
        var queues = await memoryCache.GetOrCreateAsync("queues", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
            return await managementClient.GetQueuesAsync(vhost.Name);
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

    private string GetQueueName<TConsumer>(string messageName)
    {
        var queueNamePrefix = $"{busOptions.ProcessExchange}{(string.IsNullOrWhiteSpace(busOptions.ApplicationName) ? "" : $".{busOptions.ApplicationName}")}";
        var nakedQueueName = $"{typeof(TConsumer).Name}.{messageName}".ToLower();
        return $"{queueNamePrefix}.{nakedQueueName}".ToLower();
    }

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

    private async Task<Queue> GetQueueInfo(string queueName)
    {
        return await memoryCache.GetOrCreateAsync($"queue_{queueName}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
            try
            {
                return await managementClient.GetQueueAsync(queueName, vhost.Name);
            }
            catch
            {
                return null;
            }
        });
    }
}