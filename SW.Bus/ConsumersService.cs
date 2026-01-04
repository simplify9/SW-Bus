using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SW.Bus;
internal class ConsumersService : IHostedService
{
    private readonly ILogger<ConsumersService> logger;
    private readonly BusOptions busOptions;
    private readonly ConsumerDiscovery consumerDiscovery;
    private readonly ConnectionFactory connectionFactory;
    
    // Thread-safe dictionary for runtime updates
    private readonly ConcurrentDictionary<string, (IModel model, ConsumerDefinition consumerDefinition)> openModels;
    private readonly ConsumerRunner consumerRunner;

    private IConnection conn;
    private IModel nodeModel;
    
    // Semaphore to prevent overlapping refresh operations
    private readonly SemaphoreSlim _topologyLock = new(1, 1);

    public ConsumersService(ILogger<ConsumersService> logger, BusOptions busOptions,
        ConsumerDiscovery consumerDiscovery, ConnectionFactory connectionFactory, ConsumerRunner consumerRunner)
    {
        this.logger = logger;
        this.busOptions = busOptions;
        this.consumerDiscovery = consumerDiscovery;
        this.connectionFactory = connectionFactory;
        this.consumerRunner = consumerRunner;

        openModels = new ConcurrentDictionary<string, (IModel, ConsumerDefinition)>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Offload to background thread to not block Host startup
        Task.Run(() => StartBusAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task StartBusAsync(CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _topologyLock.WaitAsync(cancellationToken);
                try
                {
                    var consumerDefinitions = await consumerDiscovery.Load();

                    // 1. Establish Connection
                    if (conn is not { IsOpen: true })
                    {
                        conn = connectionFactory.CreateConnection();
                        conn.ConnectionShutdown += ConnectionShutdown;
                    }

                    // 2. Setup Node Listener (Control Channel)
                    DeclareAndBindListener();

                    // 3. Declare Queues (using a temporary channel to keep consumer channels clean)
                    using (var model = conn.CreateModel())
                    {
                        foreach (var c in consumerDefinitions)
                            DeclareAndBind(model, c);
                    }

                    // 4. Attach Consumers
                    foreach (var consumerDefinition in consumerDefinitions)
                    {
                        AttachConsumer(consumerDefinition);
                    }
                    
                    logger.LogInformation("ConsumersService started successfully.");
                    break; // Exit retry loop on success
                }
                finally
                {
                    _topologyLock.Release();
                }
            }
            catch (Exception ex)
            {
                retryCount++;
                var delay = Math.Min(retryCount * 2, 30); // Cap delay at 30s
                logger.LogError(ex, $"Failed to start {nameof(ConsumersService)}. Retrying in {delay}s...");
                
                try 
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                }
                catch (TaskCanceledException) 
                { 
                    break; 
                }
            }
        }
    }

    private void DeclareAndBind(IModel model, ConsumerDefinition c)
    {
        logger.LogInformation($"Declaring and binding: {c.QueueName}.");

        model.QueueDeclare(c.QueueName, true, false, false, c.ProcessArgs);
        model.QueueBind(c.QueueName, busOptions.ProcessExchange, c.RoutingKey, null);
        model.QueueBind(c.QueueName, busOptions.ProcessExchange, c.RetryRoutingKey, null);

        model.QueueDeclare(c.RetryQueueName, true, false, false, c.RetryArgs);
        model.QueueBind(c.RetryQueueName, busOptions.DeadLetterExchange, c.RetryRoutingKey, null);

        model.QueueDeclare(c.BadQueueName, true, false, false, ConsumerDefinition.BadArgs);
        model.QueueBind(c.BadQueueName, busOptions.DeadLetterExchange, c.BadRoutingKey, null);
    }

    private void DeclareAndBindListener()
    {
        // Ensure we don't double-bind if this is a reconnect
        if (nodeModel != null && nodeModel.IsOpen) return;

        logger.LogInformation($"Declaring and binding node queue: {busOptions.NodeQueueName}.");
        var listeners = consumerDiscovery.LoadListeners();

        var repeated = listeners.GroupBy(nc => nc.MessageType).Select(grp => new
        {
            Count = grp.Count(),
            MessageType = grp.Key
        }).Where(mc => mc.Count > 1).ToArray();

        if (repeated.Any())
            throw new BusException(
                $"Duplicate node consumers defined for: {string.Join(',', repeated.Select(r => r.MessageType.FullName))}");

        nodeModel = conn.CreateModel();
        
        nodeModel.QueueDeclare(busOptions.NodeQueueName, true, true, true, busOptions.NodeProcessArgs);
        nodeModel.QueueBind(busOptions.NodeQueueName, busOptions.NodeExchange, busOptions.NodeRoutingKey, null);
        nodeModel.QueueBind(busOptions.NodeQueueName, busOptions.NodeExchange, busOptions.NodeRetryRoutingKey, null);
        
        nodeModel.QueueDeclare(busOptions.NodeRetryQueueName, true, true, true, busOptions.NodeRetryArgs);
        nodeModel.QueueBind(busOptions.NodeRetryQueueName, busOptions.NodeDeadLetterExchange, busOptions.NodeRetryRoutingKey, null);
        
        nodeModel.QueueDeclare(busOptions.NodeBadQueueName, true, false, false, ConsumerDefinition.BadArgs);
        nodeModel.QueueBind(busOptions.NodeBadQueueName, busOptions.NodeDeadLetterExchange, busOptions.NodeBadRoutingKey, null);

        var consumer = new AsyncEventingBasicConsumer(nodeModel);
        consumer.Received += async (ch, ea) =>
        {
            // Pass the RefreshConsumers method as the callback
            await consumerRunner.RunNodeMessage(ea, nodeModel, listeners, RefreshConsumers);
        };

        nodeModel.BasicQos(0, 1, false);
        nodeModel.BasicConsume(busOptions.NodeQueueName, false, consumer);
    }

    private void AttachConsumer(ConsumerDefinition consumerDefinition)
    {
        if (openModels.ContainsKey(consumerDefinition.QueueName)) return;

        var model = conn.CreateModel();
        
        var consumer = new AsyncEventingBasicConsumer(model);
        consumerDefinition.ConsumerObject = consumer;
        
        consumer.Shutdown += (ch, args) =>
        {
            logger.LogWarning($"Consumer {consumerDefinition.QueueName} shutdown. {args}");
            return Task.CompletedTask;
        };
        
        consumer.Received += async (ch, ea) => 
        { 
            await consumerRunner.Run(ea, consumerDefinition, model); 
        };

        model.BasicQos(0, consumerDefinition.QueuePrefetch, false);

        consumerDefinition.ConsumerTag = model.BasicConsume(consumerDefinition.QueueName, false, "",
            consumerDefinition.ConsumerArgs, consumer);

        openModels.TryAdd(consumerDefinition.QueueName, (model, consumerDefinition));
    }

    private async Task RefreshConsumers()
    {
        // Lock to ensure we don't process multiple refresh requests simultaneously
        await _topologyLock.WaitAsync();
        try
        {
            logger.LogInformation("Refreshing consumers...");
            var newDefinitions = await consumerDiscovery.Load(true);
            
            // 1. Declare any NEW queues
            using (var model = conn.CreateModel())
            {
                foreach (var c in newDefinitions)
                {
                    if (openModels.ContainsKey(c.QueueName)) continue;
                    DeclareAndBind(model, c);
                }
            }

            // 2. Update existing or Attach new
            foreach (var def in newDefinitions)
            {
                // CASE A: Existing Consumer
                if (openModels.TryGetValue(def.QueueName, out var existing))
                {
                    var existingModel = existing.model;
                    var existingDef = existing.consumerDefinition;

                    var argsChanged = !DictionariesEqual(existingDef.ConsumerArgs, def.ConsumerArgs);
                    var priorityChanged = existingDef.ConsumerPriority != def.ConsumerPriority;

                    if (priorityChanged || argsChanged)
                    {
                        logger.LogInformation($"Configuration changed for {def.QueueName}. Restarting consumer.");
                        try
                        {
                            existingModel.BasicCancel(existingDef.ConsumerTag);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, $"Failed to cancel consumer {def.QueueName}");
                        }

                        // Update properties (including Args) BEFORE re-consuming
                        existingDef.UpdateConsumerProps(def);

                        existingDef.ConsumerTag = existingModel.BasicConsume(def.QueueName,
                            false, "", existingDef.ConsumerArgs, existingDef.ConsumerObject);
                    }
                    else 
                    {
                        if (existingDef.QueuePrefetch != def.QueuePrefetch)
                        {
                            logger.LogInformation($"Prefetch changed for {def.QueueName} to {def.QueuePrefetch}.");
                            existingModel.BasicQos(0, def.QueuePrefetch, false);
                        }
                        existingDef.UpdateConsumerProps(def);
                    }
                }
                // CASE B: New Consumer
                else
                {
                    logger.LogInformation($"Attaching new consumer: {def.QueueName}");
                    AttachConsumer(def);
                }
            }
            
            // Optional: Logic to remove consumers that are no longer in newDefinitions could go here
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error refreshing consumers.");
        }
        finally
        {
            _topologyLock.Release();
        }
    }

    private bool DictionariesEqual(IDictionary<string, object> a, IDictionary<string, object> b)
    {
        if (a == b) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;

        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var value)) return false;
            if (!Equals(kvp.Value, value)) return false;
        }
        return true;
    }

    private void ConnectionShutdown(object connection, ShutdownEventArgs args)
    {
        logger.LogWarning($"Consumer RabbitMq connection shutdown. {args.Cause}");
        // Note: RabbitMQ Client usually handles auto-recovery for connection, 
        // but we might need to re-declare topology if AutomaticRecoveryEnabled is false.
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _topologyLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var (model, _) in openModels.Values)
            {
                try
                {
                    if (model.IsOpen) model.Close();
                    model.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to stop model.");
                }
            }
            openModels.Clear();

            nodeModel?.Dispose();
            if (conn != null)
            {
                if (conn.IsOpen) conn.Close();
                conn.Dispose();
            }
        }
        finally
        {
            _topologyLock.Release();
        }
    }
}