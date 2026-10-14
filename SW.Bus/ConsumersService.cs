using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SW.Bus.RabbitMqExtensions;
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
    private readonly BusConnection busConnection;
    
    // Thread-safe dictionary for runtime updates
    private readonly ConcurrentDictionary<string, AttachedConsumer> openModels;
    // Consumers taken out of service (removed, or replaced to change their concurrency) whose channel
    // closes once the messages they were processing are acknowledged.
    private readonly ConcurrentDictionary<AttachedConsumer, Task> retiring = new();
    private readonly ConsumerRunner consumerRunner;
    private readonly IOperationalEventPublisher operationalEventPublisher;
    private readonly CancellationTokenSource stopping = new();

    private IConnection conn;
    private AttachedConsumer nodeConsumer;
    private Task startTask = Task.CompletedTask;
    private bool stopped;
    
    // Semaphore to prevent overlapping refresh operations
    private readonly SemaphoreSlim _topologyLock = new(1, 1);

    public ConsumersService(ILogger<ConsumersService> logger, BusOptions busOptions,
        ConsumerDiscovery consumerDiscovery, BusConnection busConnection, ConsumerRunner consumerRunner,
        IOperationalEventPublisher operationalEventPublisher)
    {
        this.logger = logger;
        this.busOptions = busOptions;
        this.consumerDiscovery = consumerDiscovery;
        this.busConnection = busConnection;
        this.consumerRunner = consumerRunner;
        this.operationalEventPublisher = operationalEventPublisher;

        openModels = new ConcurrentDictionary<string, AttachedConsumer>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Offload to background thread to not block Host startup
        startTask = Task.Run(() => StartBusAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task StartBusAsync(CancellationToken cancellationToken)
    {
        try
        {
            // BusConnection retries until the broker answers; queues bind to these exchanges.
            await busConnection.WaitForExchangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var retryCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _topologyLock.WaitAsync(cancellationToken);
                try
                {
                    var consumerDefinitions = await consumerDiscovery.Load();

                    // 1. Establish Connection. Opened once; the client recovers it, with its channels,
                    // queues, bindings and consumers, after an outage, so nothing here re-attaches.
                    if (conn == null)
                    {
                        conn = await busConnection.GetConsumerConnectionAsync(cancellationToken);
                        conn.ConsumerTagChangeAfterRecoveryAsync += ConsumerTagChangedAfterRecovery;
                    }

                    // 2. Setup Node Listener (Control Channel)
                    await DeclareAndBindListener(cancellationToken);

                    // 3. Declare Queues (using a temporary channel to keep consumer channels clean)
                    await using (var model = await conn.CreateChannelAsync(cancellationToken: cancellationToken))
                    {
                        foreach (var c in consumerDefinitions)
                            await DeclareAndBind(model, c, cancellationToken);
                        await model.CloseAsync(cancellationToken);
                    }

                    // 4. Attach Consumers
                    foreach (var consumerDefinition in consumerDefinitions)
                    {
                        await AttachConsumer(consumerDefinition, cancellationToken);
                    }
                    
                    logger.LogInformation("ConsumersService started successfully.");
                    break; // Exit retry loop on success
                }
                finally
                {
                    _topologyLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
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

    // Names, arguments and bindings must stay exactly as they are: the broker refuses to redeclare an
    // existing queue with different arguments.
    private async Task DeclareAndBind(IChannel model, ConsumerDefinition c, CancellationToken cancellationToken)
    {
        logger.LogInformation($"Declaring and binding: {c.QueueName}.");

        await model.QueueDeclareAsync(c.QueueName, true, false, false, c.ProcessArgs, cancellationToken: cancellationToken);
        await model.QueueBindAsync(c.QueueName, busOptions.ProcessExchange, c.RoutingKey, null, cancellationToken: cancellationToken);
        await model.QueueBindAsync(c.QueueName, busOptions.ProcessExchange, c.RetryRoutingKey, null, cancellationToken: cancellationToken);

        await model.QueueDeclareAsync(c.RetryQueueName, true, false, false, c.RetryArgs, cancellationToken: cancellationToken);
        await model.QueueBindAsync(c.RetryQueueName, busOptions.DeadLetterExchange, c.RetryRoutingKey, null, cancellationToken: cancellationToken);

        await model.QueueDeclareAsync(c.BadQueueName, true, false, false, ConsumerDefinition.BadArgs, cancellationToken: cancellationToken);
        await model.QueueBindAsync(c.BadQueueName, busOptions.DeadLetterExchange, c.BadRoutingKey, null, cancellationToken: cancellationToken);
    }

    private async Task DeclareAndBindListener(CancellationToken cancellationToken)
    {
        // Ensure we don't double-bind if this is a retried start; set only once fully attached.
        if (nodeConsumer != null) return;

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

        var nodeModel = await conn.CreateChannelAsync(new CreateChannelOptions(false, false,
            consumerDispatchConcurrency: 1), cancellationToken);
        try
        {
            await nodeModel.QueueDeclareAsync(busOptions.NodeQueueName, true, true, true, busOptions.NodeProcessArgs, cancellationToken: cancellationToken);
            await nodeModel.QueueBindAsync(busOptions.NodeQueueName, busOptions.NodeExchange, busOptions.NodeRoutingKey, null, cancellationToken: cancellationToken);
            await nodeModel.QueueBindAsync(busOptions.NodeQueueName, busOptions.NodeExchange, busOptions.NodeRetryRoutingKey, null, cancellationToken: cancellationToken);

            await nodeModel.QueueDeclareAsync(busOptions.NodeRetryQueueName, true, true, true, busOptions.NodeRetryArgs, cancellationToken: cancellationToken);
            await nodeModel.QueueBindAsync(busOptions.NodeRetryQueueName, busOptions.NodeDeadLetterExchange, busOptions.NodeRetryRoutingKey, null, cancellationToken: cancellationToken);

            await nodeModel.QueueDeclareAsync(busOptions.NodeBadQueueName, true, false, false, ConsumerDefinition.BadArgs, cancellationToken: cancellationToken);
            await nodeModel.QueueBindAsync(busOptions.NodeBadQueueName, busOptions.NodeDeadLetterExchange, busOptions.NodeBadRoutingKey, null, cancellationToken: cancellationToken);

            var attached = new AttachedConsumer(nodeModel, null);
            var consumer = new AsyncEventingBasicConsumer(nodeModel);
            consumer.ReceivedAsync += async (ch, ea) =>
            {
                if (!attached.InFlight.TryEnter()) return;
                try
                {
                    // Pass the RefreshConsumers method as the callback
                    await consumerRunner.RunNodeMessage(ea, nodeModel, listeners, RefreshConsumers);
                }
                finally
                {
                    attached.InFlight.Exit();
                }
            };

            await nodeModel.BasicQosAsync(0, 1, false, cancellationToken);
            attached.ConsumerTag = await nodeModel.BasicConsumeAsync(busOptions.NodeQueueName, false, consumer, cancellationToken);
            nodeConsumer = attached;
        }
        catch
        {
            await nodeModel.DisposeAsync();
            throw;
        }
    }

    private async Task AttachConsumer(ConsumerDefinition consumerDefinition, CancellationToken cancellationToken = default)
    {
        if (openModels.ContainsKey(consumerDefinition.QueueName)) return;

        // The channel's dispatch concurrency is how many messages of this queue run at once on this node.
        var model = await conn.CreateChannelAsync(new CreateChannelOptions(false, false,
            consumerDispatchConcurrency: consumerDefinition.Concurrency), cancellationToken);
        var attached = new AttachedConsumer(model, consumerDefinition);
        try
        {
            var consumer = new AsyncEventingBasicConsumer(model);
            consumerDefinition.ConsumerObject = consumer;
            
            consumer.ShutdownAsync += (ch, args) =>
            {
                logger.LogWarning($"Consumer {consumerDefinition.QueueName} shutdown. {args}");
                FireAndForget(new ConsumerDisconnected(
                    DateTime.UtcNow,
                    busOptions.OperationalEventsSchemaVersion,
                    Environment.MachineName,
                    busOptions.EnvironmentName,
                    busOptions.ApplicationName ?? string.Empty,
                    busOptions.ProcessExchange,
                    consumerDefinition.QueueName,
                    consumerDefinition.ServiceType?.Name ?? string.Empty,
                    consumerDefinition.MessageTypeName,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    args?.ReplyText ?? "shutdown"));
                return Task.CompletedTask;
            };
            
            consumer.ReceivedAsync += async (ch, ea) => 
            { 
                // Once the consumer is stopping, a message it has not started is left unacknowledged
                // and returns to the queue when the channel closes.
                if (!attached.InFlight.TryEnter()) return;
                try
                {
                    await consumerRunner.Run(ea, consumerDefinition, model);
                }
                finally
                {
                    attached.InFlight.Exit();
                }
            };

            await model.BasicQosAsync(0, consumerDefinition.QueuePrefetch, false, cancellationToken);

            consumerDefinition.ConsumerTag = await model.BasicConsumeAsync(consumerDefinition.QueueName, false, "",
                false, false, consumerDefinition.ConsumerArgs, consumer, cancellationToken);
            attached.ConsumerTag = consumerDefinition.ConsumerTag;
        }
        catch
        {
            await model.DisposeAsync();
            throw;
        }

        openModels.TryAdd(consumerDefinition.QueueName, attached);
        FireAndForget(new ConsumerConnected(
            DateTime.UtcNow,
            busOptions.OperationalEventsSchemaVersion,
            Environment.MachineName,
            busOptions.EnvironmentName,
            busOptions.ApplicationName ?? string.Empty,
            busOptions.ProcessExchange,
            consumerDefinition.QueueName,
            consumerDefinition.ServiceType?.Name ?? string.Empty,
            consumerDefinition.MessageTypeName,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            consumerDefinition.ConsumerTag));

        await TryEmitQueueBackpressure(model, consumerDefinition);
    }

    private async Task DetachConsumer(string queueName)
    {
        if (!openModels.TryRemove(queueName, out var open)) return;

        logger.LogInformation($"Detaching removed consumer: {queueName}");
        await Retire(open);
    }

    /// <summary>
    /// Takes a consumer out of service: it starts no new message, its consumer is cancelled at the
    /// broker, and its channel closes once the messages it is processing are acknowledged. Messages
    /// prefetched but not started return to the queue when the channel closes.
    /// </summary>
    private async Task Retire(AttachedConsumer open)
    {
        open.InFlight.Close();
        try
        {
            // Cancel before closing: it also drops the consumer from the client's recovery records,
            // so a reconnect doesn't re-attach it.
            if (open.Channel.IsOpen) await open.Channel.BasicCancelAsync(open.ConsumerTag);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to cancel consumer {open.Definition?.QueueName}");
        }

        // Closed whatever the cancel did: the entry is already gone, so no later refresh would
        // retry, and closing the channel stops the consumer on its own. Registered before it can
        // finish, so a stop always sees it until its channel is closed.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        retiring[open] = closed.Task;
        _ = CloseWhenIdle(open, closed);
    }

    private async Task CloseWhenIdle(AttachedConsumer open, TaskCompletionSource closed)
    {
        try
        {
            await open.InFlight.WhenIdle();
            await CloseChannel(open);
        }
        finally
        {
            retiring.TryRemove(open, out _);
            closed.TrySetResult();
        }
    }

    /// <summary>
    /// Closes a consumer's channel. The client's close, abort and dispose all wait for a running
    /// handler to return, so the close is bounded: the broker closes the channel and requeues its
    /// unacknowledged messages at once, and a channel still processing is not disposed here.
    /// </summary>
    private async Task CloseChannel(AttachedConsumer open)
    {
        var name = open.Definition?.QueueName ?? busOptions.NodeQueueName;
        using var bounded = new CancellationTokenSource(BusConnection.CloseTimeout);
        try
        {
            if (open.Channel.IsOpen) await open.Channel.CloseAsync(bounded.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning($"The channel of consumer {name} closed with a message still being processed; it returns to the queue.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to close the channel of consumer {name}");
        }

        // Disposing a channel whose handler is still running blocks the closes of other channels
        // on the connection until it returns, so such a channel is left to the connection's close.
        if (open.InFlight.Count == 0)
            await open.Channel.DisposeAsync();
    }

    private async Task RefreshConsumers()
    {
        // Lock to ensure we don't process multiple refresh requests simultaneously
        await _topologyLock.WaitAsync();
        try
        {
            if (stopped || conn == null) return;

            logger.LogInformation("Refreshing consumers...");
            var newDefinitions = await consumerDiscovery.Load();
            
            // 1. Declare any NEW queues
            await using (var model = await conn.CreateChannelAsync())
            {
                foreach (var c in newDefinitions)
                {
                    if (openModels.ContainsKey(c.QueueName)) continue;
                    await DeclareAndBind(model, c, CancellationToken.None);
                }
                await model.CloseAsync();
            }

            // 2. Update existing or Attach new
            foreach (var def in newDefinitions)
            {
                // CASE A: Existing Consumer
                if (openModels.TryGetValue(def.QueueName, out var existing))
                {
                    var existingModel = existing.Channel;
                    var existingDef = existing.Definition;

                    var prefetchChanged = existingDef.QueuePrefetch != def.QueuePrefetch;
                    var priorityChanged = existingDef.ConsumerPriority != def.ConsumerPriority;
                    var concurrencyChanged = existingDef.Concurrency != def.Concurrency;

                    if (concurrencyChanged)
                    {
                        // A channel's dispatch concurrency is fixed when it is created, so the consumer
                        // moves to a new channel; the old one finishes what it is processing first.
                        logger.LogInformation($"Concurrency changed for {def.QueueName}. Moving the consumer to a new channel.");
                        openModels.TryRemove(def.QueueName, out _);
                        await Retire(existing);
                        existingDef.UpdateConsumerProps(def);
                        await AttachConsumer(existingDef);
                    }
                    else if (priorityChanged || prefetchChanged)
                    {
                        logger.LogInformation($"Configuration changed for {def.QueueName}. Restarting consumer.");
                        try
                        {
                            await existingModel.BasicCancelAsync(existingDef.ConsumerTag);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, $"Failed to cancel consumer {def.QueueName}");
                        }

                        // Update properties (including Args) BEFORE re-consuming
                        existingDef.UpdateConsumerProps(def);

                        await existingModel.BasicQosAsync(0, def.QueuePrefetch, false);
                        
                        existingDef.ConsumerTag = await existingModel.BasicConsumeAsync(def.QueueName,
                            false, "", false, false, existingDef.ConsumerArgs, existingDef.ConsumerObject);
                        existing.ConsumerTag = existingDef.ConsumerTag;
                        
                    }
                    else 
                    {
                        existingDef.UpdateConsumerProps(def);
                    }
                }
                // CASE B: New Consumer
                else
                {
                    logger.LogInformation($"Attaching new consumer: {def.QueueName}");
                    await AttachConsumer(def);
                }
            }

            // 3. Detach consumers that are no longer defined (a dynamic consumer removed or renamed).
            // Only the consumer stops; its queues, and whatever they still hold, are left for the
            // application to decide about.
            var defined = newDefinitions.Select(d => d.QueueName).ToHashSet();
            foreach (var queueName in openModels.Keys.Where(q => !defined.Contains(q)).ToList())
                await DetachConsumer(queueName);
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

    // A consumer whose tag the broker generated gets a new one when the client recovers it.
    private Task ConsumerTagChangedAfterRecovery(object sender, ConsumerTagChangedAfterRecoveryEventArgs args)
    {
        foreach (var open in openModels.Values.Where(o => o.ConsumerTag == args.TagBefore))
        {
            open.ConsumerTag = args.TagAfter;
            open.Definition.ConsumerTag = args.TagAfter;
        }
        if (nodeConsumer?.ConsumerTag == args.TagBefore) nodeConsumer.ConsumerTag = args.TagAfter;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops receiving, waits up to <see cref="BusOptions.DrainTimeout"/> (and the host's shutdown
    /// token) for the messages being processed to finish and be acknowledged, then closes the
    /// channels. Messages prefetched but not started, and any still running at the deadline, return
    /// to their queue when the channel closes.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        try
        {
            await startTask;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Consumer start ended with an error during stop.");
        }

        List<AttachedConsumer> consumers;
        await _topologyLock.WaitAsync(cancellationToken);
        try
        {
            // The lock is not held while draining: a node message being drained may itself be a refresh.
            stopped = true;
            consumers = openModels.Values.ToList();
            openModels.Clear();
            if (nodeConsumer != null) consumers.Add(nodeConsumer);
        }
        finally
        {
            _topologyLock.Release();
        }

        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(busOptions.DrainTimeout);

        foreach (var open in consumers)
        {
            open.InFlight.Close();
            try
            {
                if (open.Channel.IsOpen) await open.Channel.BasicCancelAsync(open.ConsumerTag, false, drain.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"Failed to cancel consumer {open.Definition?.QueueName ?? busOptions.NodeQueueName}.");
            }
        }

        var running = consumers.Sum(c => c.InFlight.Count);
        if (running > 0)
            logger.LogInformation("Waiting for {Count} message(s) being processed to finish.", running);
        try
        {
            await Task.WhenAll(consumers.Select(c => c.InFlight.WhenIdle()).Concat(retiring.Values))
                .WaitAsync(drain.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Stopped waiting for {Count} message(s) still being processed; they return to their queue.",
                consumers.Sum(c => c.InFlight.Count) + retiring.Keys.Sum(c => c.InFlight.Count));
        }

        var all = consumers.Concat(retiring.Keys).ToList();
        await Task.WhenAll(all.Select(CloseChannel));
        retiring.Clear();
        if (all.Any(c => c.InFlight.Count > 0)) busConnection.ConsumerHandlersStillRunning = true;
    }

    private async Task TryEmitQueueBackpressure(IChannel model, ConsumerDefinition consumerDefinition)
    {
        if (busOptions.QueueBackpressureThreshold <= 0)
            return;

        try
        {
            var depth = await model.MessageCountAsync(consumerDefinition.QueueName);
            if (depth < busOptions.QueueBackpressureThreshold)
                return;

            FireAndForget(new QueueBackpressureDetected(
                DateTime.UtcNow,
                busOptions.OperationalEventsSchemaVersion,
                Environment.MachineName,
                busOptions.EnvironmentName,
                busOptions.ApplicationName ?? string.Empty,
                busOptions.ProcessExchange,
                consumerDefinition.QueueName,
                consumerDefinition.ServiceType?.Name ?? string.Empty,
                consumerDefinition.MessageTypeName,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                depth,
                consumerDefinition.QueuePrefetch,
                busOptions.QueueBackpressureThreshold));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Queue backpressure check failed for {QueueName}", consumerDefinition.QueueName);
        }
    }

    private void FireAndForget(IOperationalEvent evt)
    {
        operationalEventPublisher.Publish(evt)
            .ContinueWith(t => logger.LogWarning(t.Exception,
                    "Unobserved exception publishing operational event {EventType}.",
                    evt.GetType().Name),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private sealed class AttachedConsumer(IChannel channel, ConsumerDefinition definition)
    {
        public IChannel Channel { get; } = channel;
        public ConsumerDefinition Definition { get; } = definition;
        public string ConsumerTag { get; set; }
        public InFlightMessages InFlight { get; } = new();
    }
}

/// <summary>
/// Counts the messages a consumer is processing, and once closed lets no new one start, so a stop or
/// a channel replacement can wait for the running ones to be acknowledged.
/// </summary>
internal sealed class InFlightMessages
{
    private readonly object gate = new();
    private int count;
    private bool closed;
    private TaskCompletionSource idle = NewIdle(completed: true);

    public int Count
    {
        get { lock (gate) return count; }
    }

    public bool TryEnter()
    {
        lock (gate)
        {
            if (closed) return false;
            if (count++ == 0) idle = NewIdle(completed: false);
            return true;
        }
    }

    public void Exit()
    {
        lock (gate)
        {
            if (--count == 0) idle.TrySetResult();
        }
    }

    public void Close()
    {
        lock (gate) closed = true;
    }

    public Task WhenIdle()
    {
        lock (gate) return idle.Task;
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) source.SetResult();
        return source;
    }
}
