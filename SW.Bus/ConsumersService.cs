using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
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

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.Run(()=>StartBusAsync(cancellationToken), cancellationToken);
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



        public async Task StopAsync(CancellationToken cancellationToken)
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

            nodeModel?.Dispose();
            conn?.Close();
            conn?.Dispose();
        }
    }
}

