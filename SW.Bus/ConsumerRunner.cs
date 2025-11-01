using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SW.HttpExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus
{
    internal class ConsumerRunner
    {
        private readonly IServiceProvider sp;
        private readonly BusOptions busOptions;
        private readonly ConcurrentDictionary<Type, ILogger> loggerCache;
        private readonly Func<Type, ILogger> loggerFactoryCreateLogger;

        public ConsumerRunner(IServiceProvider sp, BusOptions busOptions, ILoggerFactory loggerFactory)
        {
            this.sp = sp;
            this.busOptions = busOptions;
            this.loggerCache = new ConcurrentDictionary<Type, ILogger>();
            this.loggerFactoryCreateLogger = loggerFactory.CreateLogger;
        }

        private ILogger GetLogger(Type consumerType)
        {
            return loggerCache.GetOrAdd(consumerType, loggerFactoryCreateLogger);
        }

        internal async Task Run(BasicDeliverEventArgs ea, ConsumerDefinition consumerDefinition, IModel model)
        {
            var remainingRetryCount = consumerDefinition.RetryCount;

            if (ea.BasicProperties?.Headers != null &&
                ea.BasicProperties.Headers.ContainsKey("x-death") &&
                ea.BasicProperties?.Headers?["x-death"] is List<object> xDeathList)
            {
                if (xDeathList.Count > 0 && xDeathList.First() is IDictionary<string, object> xDeathDic &&
                    xDeathDic["count"] is long lngTotalDeath && lngTotalDeath < int.MaxValue)
                    remainingRetryCount = consumerDefinition.RetryCount - Convert.ToInt32(lngTotalDeath);
                else
                    remainingRetryCount = 0;
            }

            var message = "";
            var logger = GetLogger(consumerDefinition.ServiceType);
            
            try
            {
                using var scope = sp.CreateScope();
                
                TryBuildBusRequestContext(scope.ServiceProvider, ea.BasicProperties, remainingRetryCount);

                var body = ea.Body;
                message = Encoding.UTF8.GetString(body.ToArray());
                var svc = scope.ServiceProvider.GetRequiredService(consumerDefinition.ServiceType);
                if (consumerDefinition.MessageType == null)
                    await ((IConsume)svc).Process(consumerDefinition.MessageTypeName, message);

                else
                {
                    var messageObject = JsonSerializer.Deserialize(message, consumerDefinition.MessageType);
                    await (Task)consumerDefinition.Method.Invoke(svc, new[] { messageObject });
                }

                model.BasicAck(ea.DeliveryTag, false);
                logger.LogDebug(
                    "Consumer: {ConsumerName} | Queue: {QueueName} | Message Type: {MessageType} | Successfully processed message | Delivery Tag: {DeliveryTag}",
                    consumerDefinition.ServiceType.Name,
                    consumerDefinition.QueueName,
                    consumerDefinition.MessageTypeName,
                    ea.DeliveryTag);
            }
            catch (Exception ex)
            {
                if (remainingRetryCount != 0)
                {
                    // reject the message, will be sent to wait queue
                    model.BasicReject(ea.DeliveryTag, false);
                    logger.LogWarning(ex,
                        "Consumer: {ConsumerName} | Queue: {QueueName} | Message Type: {MessageType} | Failed to process message. Retries remaining: {RemainingRetries}/{TotalRetries} | Delivery Tag: {DeliveryTag} | Message: {Message}",
                        consumerDefinition.ServiceType.Name,
                        consumerDefinition.QueueName,
                        consumerDefinition.MessageTypeName,
                        remainingRetryCount,
                        consumerDefinition.RetryCount,
                        ea.DeliveryTag,
                        message);
                }
                else
                {
                    model.BasicAck(ea.DeliveryTag, false);
                    logger.LogError(ex,
                        "Consumer: {ConsumerName} | Queue: {QueueName} | Message Type: {MessageType} | Failed to process message after all retries exhausted. Total retries: {TotalRetries} | Delivery Tag: {DeliveryTag} | Message: {Message}",
                        consumerDefinition.ServiceType.Name,
                        consumerDefinition.QueueName,
                        consumerDefinition.MessageTypeName,
                        consumerDefinition.RetryCount,
                        ea.DeliveryTag,
                        message);

                    await PublishBad(model, ea.Body, ea.BasicProperties, busOptions.DeadLetterExchange, consumerDefinition.BadRoutingKey, ex);
                }
            }
        }
        internal async Task RunNodeMessage(BasicDeliverEventArgs ea, IModel model,
            ICollection<ListenerDefinition> listeners, Func<Task> refreshConsumers)
        {
            var remainingRetryCount = busOptions.ListenRetryCount;

            if (ea.BasicProperties?.Headers != null &&
                ea.BasicProperties.Headers.ContainsKey("x-death") &&
                ea.BasicProperties?.Headers?["x-death"] is List<object> xDeathList)
            {
                if (xDeathList.Count > 0 && xDeathList.First() is IDictionary<string, object> xDeathDic &&
                    xDeathDic["count"] is long lngTotalDeath && lngTotalDeath < int.MaxValue)
                    remainingRetryCount = busOptions.ListenRetryCount - Convert.ToInt32(lngTotalDeath);
                else
                    remainingRetryCount = 0;
            }

            var message = "";
            MethodInfo processMethod = null;
            MethodInfo failMethod = null;
            ListenerDefinition listenerDefinition = null;
            BroadcastMessage consumerMessage = null;
            object svc = null;
            try
            {
                using var scope = sp.CreateScope();

                var body = ea.Body;
                message = Encoding.UTF8.GetString(body.ToArray());
                if (message == Broadcaster.RefreshConsumersMessageBody)
                {
                    await refreshConsumers();
                }
                else
                {
                    consumerMessage = JsonSerializer.Deserialize<BroadcastMessage>(message);
                    TryBuildBusRequestContext(scope.ServiceProvider, ea.BasicProperties, remainingRetryCount);
                    
                    listenerDefinition = listeners.SingleOrDefault(d =>
                        d.MessageType == Type.GetType(consumerMessage.MessageTypeName));
                    if (listenerDefinition != null)
                    {
                        svc = scope.ServiceProvider.GetRequiredService(listenerDefinition.ServiceType);
                        processMethod = listenerDefinition.Method;
                        failMethod = listenerDefinition.FailMethod;
                        var messageObject =
                            JsonSerializer.Deserialize(consumerMessage.Message, listenerDefinition.MessageType);
                        await (Task)processMethod.Invoke(svc, new[] { messageObject });
                    }
                }

                model.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                // Get logger specific to the listener type or use default if no listener
                var logger = listenerDefinition != null 
                    ? GetLogger(listenerDefinition.ServiceType) 
                    : GetLogger(typeof(ConsumerRunner));
                
                if (remainingRetryCount != 0)
                {
                    // reject the message, will be sent to wait queue
                    model.BasicReject(ea.DeliveryTag, false);
                    await RunOnFail(svc, failMethod, ex, message, logger);
                    logger.LogWarning(ex,
                        "Listener: {ListenerName} | Message Type: {MessageType} | Failed to process message. Retries remaining: {RemainingRetries}/{TotalRetries} | Message: {Message}",
                        listenerDefinition?.ServiceType.Name ?? "reloading",
                        listenerDefinition?.MessageTypeName ?? message,
                        remainingRetryCount,
                        busOptions.ListenRetryCount,
                        message);
                }
                else
                {
                    model.BasicAck(ea.DeliveryTag, false);
                    logger.LogError(ex,
                        "Listener: {ListenerName} | Message Type: {MessageType} | Failed to process message after all retries exhausted. Total retries: {TotalRetries} | Message: {Message}",
                        listenerDefinition?.ServiceType.Name ?? "reloading",
                        listenerDefinition?.MessageTypeName ?? message,
                        busOptions.ListenRetryCount,
                        message);

                    await PublishBad(model, ea.Body, ea.BasicProperties,busOptions.NodeDeadLetterExchange, busOptions.NodeBadRoutingKey, ex);
                    await RunOnFail(svc, failMethod, ex, message, logger);
                }
            }
        }

        private async Task RunOnFail(object svc, MethodInfo failMethod, Exception ex, string message, ILogger logger = null)
        {
            if (svc == null || failMethod == null)
                return;
            try
            {
                await (Task)failMethod.Invoke(svc, new Object[] { ex });
            }
            catch (Exception e)
            {
                if (logger != null)
                {
                    logger.LogError(e, "Failed to run OnFail method. Original exception: {OriginalException} | Message: {Message}", ex, message);
                }
            }
        }


        void TryBuildBusRequestContext(IServiceProvider serviceProvider, IBasicProperties basicProperties,
            int remainingRetries)
        {
            var remainingRetriesValue = new RequestValue("RemainingRetries", remainingRetries.ToString(),
                RequestValueType.ServiceBusValue);
            
            RequestValue sourceNodeIdValue = null;
            if (basicProperties.Headers != null &&
                basicProperties.Headers.TryGetValue(BusOptions.SourceNodeIdHeaderName, out var sourceNodeIdBytes))
            {
                var sourceNodeId = Encoding.UTF8.GetString((byte[])sourceNodeIdBytes);
                sourceNodeIdValue = new("SourceNodeId", sourceNodeId, RequestValueType.ServiceBusValue);
            }
            
            var requestContext = serviceProvider.GetService<RequestContext>();

            if (requestContext == null || !busOptions.Token.IsValid || basicProperties.Headers == null ||
                !basicProperties.Headers.TryGetValue(RequestContext.UserHeaderName, out var userHeaderBytes))
            {
                requestContext?.AddValue(remainingRetriesValue);
                if (sourceNodeIdValue != null) requestContext?.AddValue(sourceNodeIdValue);
                return;
            };

            var userHeader = Encoding.UTF8.GetString((byte[])userHeaderBytes);
            var user = busOptions.Token.ReadJwt(userHeader);

            string correlationHeader = null;
            if (basicProperties.Headers.TryGetValue(RequestContext.CorrelationIdHeaderName,
                    out var correlationIdHeaderBytes))
            {
                correlationHeader = Encoding.UTF8.GetString((byte[])correlationIdHeaderBytes);
            }

            var requestValues = new List<RequestValue> { remainingRetriesValue };
            
            if (sourceNodeIdValue !=null)
                requestValues.Add(sourceNodeIdValue);
            
            requestContext.Set(user, requestValues, correlationHeader);
        }

        private Task PublishBad(IModel model, ReadOnlyMemory<byte> body, IBasicProperties messageProps, 
            string exchange, string routingKey, Exception ex)
        {
            const string exception = "exception";

            var props = model.CreateBasicProperties();
            props.Headers = new Dictionary<string, object>();

            foreach (var (key, value) in messageProps.Headers?.Where(
                         h => h.Key != "x-death") ?? new Dictionary<string, object>())
                props.Headers.Add(key, value);

            // total bad is used in case the message was moved from bad to process (using shovel) and failed again. so we keep history of failures
            var totalBad = props.Headers.Count(c => c.Key.StartsWith(exception)) + 1;

            props.Headers.Add($"{exception}{totalBad}", JsonSerializer.Serialize(ex,ex.GetType()));

            props.DeliveryMode = 2;
            model.BasicPublish(exchange, routingKey, props, body);

            return Task.CompletedTask;
        }
    }
}