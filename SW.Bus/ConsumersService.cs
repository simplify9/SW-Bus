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

namespace SW.Bus
{
    internal class ConsumersService : IHostedService
    {

        private readonly ILogger<ConsumersService> logger;
        private readonly BusOptions busOptions;
        private readonly ConsumerDiscovery consumerDiscovery;
        private readonly ConnectionFactory connectionFactory;
        private readonly IDictionary<string,IModel> openModels;
        private readonly ConcurrentDictionary<string, IModel> drainingModels = new ConcurrentDictionary<string, IModel>();
        private readonly ConsumerRunner consumerRunner;

        private IConnection conn;
        private IModel nodeModel;
        private ICollection<ConsumerDefinition> consumerDefinitions;
        public ConsumersService(ILogger<ConsumersService> logger, BusOptions busOptions,
            ConsumerDiscovery consumerDiscovery, ConnectionFactory connectionFactory, ConsumerRunner consumerRunner)
        {
            this.logger = logger;
            this.busOptions = busOptions;
            this.consumerDiscovery = consumerDiscovery;
            this.connectionFactory = connectionFactory;
            this.consumerRunner = consumerRunner;

            openModels = new Dictionary<string, IModel>();
        }

        public  Task StartAsync(CancellationToken cancellationToken)
        {
            Task.Run(() => StartBusAsync(cancellationToken), cancellationToken);
            Task.Run(() => DrainingLoop(cancellationToken), cancellationToken);
            return Task.CompletedTask;

        }

        private async Task StartBusAsync(CancellationToken cancellationToken)
        {
            
            try
            {
                consumerDefinitions = await consumerDiscovery.Load();

                conn = connectionFactory.CreateConnection();
                conn.ConnectionShutdown += ConnectionShutdown;
                DeclareAndBindListener();
                
                using (var model = conn.CreateModel())
                {
                    foreach (var c in consumerDefinitions)
                        DeclareAndBind(model,c);
                    
                }

                foreach (var consumerDefinition in consumerDefinitions)
                {
                    AttachConsumer(consumerDefinition);
                }


            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Starting {nameof(ConsumersService)}");
            }
            
        }
        
        private void DeclareAndBind(IModel model, ConsumerDefinition c)
        {
            logger.LogInformation($"Declaring and binding: {c.QueueName}.");

            CheckAndMigrateLegacyQueue(c);

            // process queue 
            model.QueueDeclare(c.QueueName, true, false, false, c.ProcessArgs);
            model.QueueBind(c.QueueName, busOptions.ProcessExchange, c.RoutingKey, null);
            model.QueueBind(c.QueueName, busOptions.ProcessExchange, c.RetryRoutingKey, null);
            //model.QueueUnbind();
            // wait queue
            
            model.QueueDeclare(c.RetryQueueName, true, false, false, c.RetryArgs);
            model.QueueBind(c.RetryQueueName, busOptions.DeadLetterExchange, c.RetryRoutingKey, null);
            // bad queue
            model.QueueDeclare(c.BadQueueName, true, false, false, ConsumerDefinition.BadArgs);
            model.QueueBind(c.BadQueueName, busOptions.DeadLetterExchange, c.BadRoutingKey, null);
        }

        private void DeclareAndBindListener()
        {
            logger.LogInformation($"Declaring and binding node queue: {busOptions.NodeQueueName}.");
            var listeners = consumerDiscovery.LoadListeners();
            
            var repeated = listeners.GroupBy(nc => nc.MessageType).Select(grp => new
            {
                Count = grp.Count(),
                MessageType = grp.Key
            }).Where(mc=> mc.Count > 1).ToArray();

            if (repeated.Any())
                throw new BusException("One node consumer is allowed for each message type. the following message(s) has more than one node consumer defined" + 
                                       $" {string.Join(',', repeated.Select(r=> r.MessageType.FullName))}");
            
            nodeModel = conn.CreateModel();
            // process queue 
            nodeModel.QueueDeclare(busOptions.NodeQueueName, true, true, true, busOptions.NodeProcessArgs );
            nodeModel.QueueBind(busOptions.NodeQueueName, busOptions.NodeExchange, busOptions.NodeRoutingKey, null);
            nodeModel.QueueBind(busOptions.NodeQueueName, busOptions.NodeExchange, busOptions.NodeRetryRoutingKey, null);
            // wait queue
            nodeModel.QueueDeclare(busOptions.NodeRetryQueueName, true, true, true, busOptions.NodeRetryArgs);
            nodeModel.QueueBind(busOptions.NodeRetryQueueName, busOptions.NodeDeadLetterExchange, busOptions.NodeRetryRoutingKey, null);
            // bad queue
            nodeModel.QueueDeclare(busOptions.NodeBadQueueName, true, false, false, ConsumerDefinition.BadArgs);
            nodeModel.QueueBind(busOptions.NodeBadQueueName, busOptions.NodeDeadLetterExchange, busOptions.NodeBadRoutingKey, null);

            var consumer = new AsyncEventingBasicConsumer(nodeModel);
            consumer.Shutdown +=  (ch, args) =>
            {
                try
                {
                    logger.LogWarning($"Node Consumer RabbitMq connection shutdown. {args}");
                }
                catch (Exception)
                {
                    // ignored
                }
                return Task.CompletedTask;
            };
            consumer.Received += async (ch, ea) =>
            {
                await consumerRunner.RunNodeMessage(ea, nodeModel,listeners,RefreshConsumers );
            };

            nodeModel.BasicQos(0, 1, false);

            nodeModel.BasicConsume(busOptions.NodeQueueName, false, consumer);

        }
        private void AttachConsumer(ConsumerDefinition consumerDefinition)
        {
            var model = conn.CreateModel();
            openModels.Add(consumerDefinition.QueueName, model);
                    
            var consumer = new AsyncEventingBasicConsumer(model);
            consumer.Shutdown +=  (ch, args) =>
            {
                try
                {
                    logger.LogWarning($"Consumer RabbitMq connection shutdown. {args}");
                }
                catch (Exception)
                {
                    // ignored
                }
                return Task.CompletedTask;
            };
            consumer.Received += async (ch, ea) =>
            {
                await consumerRunner.Run(ea, consumerDefinition, model);
            };

            model.BasicQos(0, consumerDefinition.QueuePrefetch, false);

            model.BasicConsume(consumerDefinition.QueueName,  false, "", consumerDefinition.ConsumerArgs,consumer );
            
        }
        
        private async Task RefreshConsumers()
        {
            try
            {
                consumerDefinitions = await consumerDiscovery.Load(true);
                using (var model = conn.CreateModel())
                {
                    foreach (var c in consumerDefinitions)
                    {
                        if(openModels.ContainsKey(c.QueueName))
                            continue;
                        DeclareAndBind(model,c);
                    }
                }
                foreach (var consumerDefinition in consumerDefinitions)
                {
                    if(openModels.ContainsKey(consumerDefinition.QueueName))
                        continue;
                    AttachConsumer(consumerDefinition);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Starting {nameof(ConsumersService)}");
            }
        }
        private void ConnectionShutdown(object connection, ShutdownEventArgs args)
        {
            try
            {
                logger.LogWarning($"Consumer RabbitMq connection shutdown. {args.Cause}");
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void CheckAndMigrateLegacyQueue(ConsumerDefinition c)
        {
            var potentialLegacyQueues = new List<string>();
            potentialLegacyQueues.Add(c.LegacyQueueName);
            for (int i = 1; i <= 10; i++)
            {
                potentialLegacyQueues.Add($"{c.LegacyQueueName}.p{i}");
            }
            if (busOptions.DefaultMaxPriority > 10)
            {
                potentialLegacyQueues.Add($"{c.LegacyQueueName}.p{busOptions.DefaultMaxPriority}");
            }

            foreach (var legacyQueueName in potentialLegacyQueues)
            {
                if (legacyQueueName == c.QueueName) continue;
                if (drainingModels.ContainsKey(legacyQueueName)) continue;

                try 
                {
                    using (var tempModel = conn.CreateModel())
                    {
                        tempModel.QueueDeclarePassive(legacyQueueName);
                    }
                    
                    logger.LogInformation($"Legacy queue found: {legacyQueueName}. Starting migration.");
                    
                    using (var tempModel = conn.CreateModel())
                    {
                        tempModel.QueueUnbind(legacyQueueName, busOptions.ProcessExchange, c.RoutingKey, null);
                        tempModel.QueueUnbind(legacyQueueName, busOptions.ProcessExchange, c.RetryRoutingKey, null);
                    }

                    if (openModels.ContainsKey(legacyQueueName))
                    {
                        var model = openModels[legacyQueueName];
                        openModels.Remove(legacyQueueName);
                        drainingModels.TryAdd(legacyQueueName, model);
                        logger.LogInformation($"Moved active consumer on {legacyQueueName} to draining mode.");
                    }
                    else
                    {
                        StartDraining(legacyQueueName, c);
                    }
                }
                catch (RabbitMQ.Client.Exceptions.OperationInterruptedException ex)
                {
                    if (ex.ShutdownReason.ReplyCode != 404)
                    {
                        logger.LogError(ex, $"Error checking legacy queue {legacyQueueName}");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error checking legacy queue {legacyQueueName}");
                }
            }
        }

        private void StartDraining(string queueName, ConsumerDefinition c)
        {
            var model = conn.CreateModel();
            if (drainingModels.TryAdd(queueName, model))
            {
                var consumer = new AsyncEventingBasicConsumer(model);
                consumer.Received += async (ch, ea) =>
                {
                    await consumerRunner.Run(ea, c, model);
                };
                
                var args = new Dictionary<string, object> { { "x-priority", 1 } };
                model.BasicConsume(queueName, false, "", args, consumer);
                
                logger.LogInformation($"Started draining legacy queue: {queueName}");
            }
            else
            {
                model.Dispose();
            }
        }

        private async Task DrainingLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    
                    if (drainingModels.IsEmpty || conn == null || !conn.IsOpen) continue;

                    using (var model = conn.CreateModel())
                    {
                        var queuesToRemove = new List<string>();
                        foreach (var queueName in drainingModels.Keys)
                        {
                            try 
                            {
                                var result = model.QueueDeclarePassive(queueName);
                                if (result.MessageCount == 0)
                                {
                                    model.QueueDelete(queueName);
                                    queuesToRemove.Add(queueName);
                                    logger.LogInformation($"Legacy queue {queueName} is empty and has been deleted.");
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, $"Error checking draining queue {queueName}");
                                if (ex is RabbitMQ.Client.Exceptions.OperationInterruptedException oex && oex.ShutdownReason.ReplyCode == 404)
                                {
                                    queuesToRemove.Add(queueName);
                                }
                            }
                        }
                        
                        foreach(var q in queuesToRemove)
                        {
                            if (drainingModels.TryRemove(q, out var drainingModel))
                            {
                                try
                                {
                                    drainingModel.Close();
                                    drainingModel.Dispose();
                                }
                                catch
                                {
                                    // ignored
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                     logger.LogError(ex, "Error in DrainingLoop");
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            
            foreach (var model in openModels.Values)

                try
                {
                    //model.Close();
                    model.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"Failed to stop model.");
                }

            foreach (var model in drainingModels.Values)
            {
                try
                {
                    model.Dispose();
                }
                catch
                {
                    // ignored
                }
            }

            nodeModel?.Dispose();
            conn?.Close();
            conn?.Dispose();
            return Task.CompletedTask;
        }
    }
}

