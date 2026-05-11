using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SW.Bus.RabbitMqExtensions;
using SW.HttpExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus
{
    internal class ConsumerRunner
    {
        private readonly IServiceProvider sp;
        private readonly BusOptions busOptions;
        private readonly IOperationalEventPublisher operationalEventPublisher;
        private readonly BusMetrics metrics;

        private readonly ILogger<ConsumerRunner> logger;

        public ConsumerRunner(
            IServiceProvider sp,
            BusOptions busOptions,
            IOperationalEventPublisher operationalEventPublisher,
            BusMetrics metrics,
            ILogger<ConsumerRunner> logger)
        {
            this.sp = sp;
            this.busOptions = busOptions;
            this.operationalEventPublisher = operationalEventPublisher;
            this.metrics = metrics;
            this.logger = logger;
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
            var payloadSizeBytes = (long)ea.Body.Length;
            var currentRetryCount = Math.Max(consumerDefinition.RetryCount - remainingRetryCount, 0);
            var parentContext = OperationalEventEnvelope.ExtractParentContext(ea.BasicProperties);
            using var activity = BusDiagnostics.ActivitySource.StartActivity(
                $"bus.consume {consumerDefinition.MessageTypeName}",
                ActivityKind.Consumer,
                parentContext);
            var stopwatch = Stopwatch.StartNew();

            var commonMessageId = OperationalEventEnvelope.GetMessageId(ea.BasicProperties);
            var commonCorrelationId = OperationalEventEnvelope.GetCorrelationId(ea.BasicProperties);
            var commonCausationId = OperationalEventEnvelope.GetCausationId(ea.BasicProperties);
            var traceId = activity?.TraceId.ToString() ?? OperationalEventEnvelope.GetTraceId(ea.BasicProperties);
            var spanId = activity?.SpanId.ToString() ?? OperationalEventEnvelope.GetSpanId(ea.BasicProperties);

            metrics.ProcessingStarted.Add(1);
            FireAndForget(new MessageProcessingStarted(
                DateTime.UtcNow,
                busOptions.OperationalEventsSchemaVersion,
                Environment.MachineName,
                busOptions.EnvironmentName,
                busOptions.ApplicationName ?? string.Empty,
                busOptions.ProcessExchange,
                consumerDefinition.QueueName,
                consumerDefinition.ServiceType?.Name ?? string.Empty,
                consumerDefinition.MessageTypeName,
                commonMessageId,
                commonCorrelationId,
                commonCausationId,
                traceId,
                spanId,
                ea.DeliveryTag,
                currentRetryCount,
                payloadSizeBytes));

            try
            {
                using var scope = sp.CreateScope();
                TryBuildBusRequestContext(scope.ServiceProvider,consumerDefinition.MessageTypeName, ea.BasicProperties, remainingRetryCount);

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
                stopwatch.Stop();
                metrics.ProcessingCompleted.Add(1);
                metrics.ProcessingLatencyMs.Record(stopwatch.Elapsed.TotalMilliseconds);
                FireAndForget(new MessageProcessingCompleted(
                    DateTime.UtcNow,
                    busOptions.OperationalEventsSchemaVersion,
                    Environment.MachineName,
                    busOptions.EnvironmentName,
                    busOptions.ApplicationName ?? string.Empty,
                    busOptions.ProcessExchange,
                    consumerDefinition.QueueName,
                    consumerDefinition.ServiceType?.Name ?? string.Empty,
                    consumerDefinition.MessageTypeName,
                    commonMessageId,
                    commonCorrelationId,
                    commonCausationId,
                    traceId,
                    spanId,
                    ea.DeliveryTag,
                    stopwatch.Elapsed.TotalMilliseconds,
                    currentRetryCount,
                    payloadSizeBytes));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                metrics.ProcessingFailed.Add(1);
                FireAndForget(new MessageProcessingFailed(
                    DateTime.UtcNow,
                    busOptions.OperationalEventsSchemaVersion,
                    Environment.MachineName,
                    busOptions.EnvironmentName,
                    busOptions.ApplicationName ?? string.Empty,
                    busOptions.ProcessExchange,
                    consumerDefinition.QueueName,
                    consumerDefinition.ServiceType?.Name ?? string.Empty,
                    consumerDefinition.MessageTypeName,
                    commonMessageId,
                    commonCorrelationId,
                    commonCausationId,
                    traceId,
                    spanId,
                    ea.DeliveryTag,
                    stopwatch.Elapsed.TotalMilliseconds,
                    currentRetryCount,
                    ex.GetType().FullName ?? ex.GetType().Name,
                    ex.Message,
                    ex.StackTrace,
                    payloadSizeBytes));

                if (remainingRetryCount != 0)
                {
                    // reject the message, will be sent to wait queue
                    model.BasicReject(ea.DeliveryTag, false);
                    metrics.RetryScheduled.Add(1);
                    FireAndForget(new MessageRetryScheduled(
                        DateTime.UtcNow,
                        busOptions.OperationalEventsSchemaVersion,
                        Environment.MachineName,
                        busOptions.EnvironmentName,
                        busOptions.ApplicationName ?? string.Empty,
                        busOptions.ProcessExchange,
                        consumerDefinition.QueueName,
                        consumerDefinition.ServiceType?.Name ?? string.Empty,
                        consumerDefinition.MessageTypeName,
                        commonMessageId,
                        commonCorrelationId,
                        commonCausationId,
                        traceId,
                        spanId,
                        ea.DeliveryTag,
                        currentRetryCount,
                        remainingRetryCount,
                        consumerDefinition.RetryAfter));
                    logger.LogWarning(ex,
                        @$"Failed to process message '{consumerDefinition.MessageTypeName}', in '{consumerDefinition.ServiceType.Name}'. Number of retries remaining {remainingRetryCount}.
                            Total retries configured {consumerDefinition.RetryCount}.
                            Message {message}");
                }
                else
                {
                    model.BasicAck(ea.DeliveryTag, false);
                    logger.LogError(ex,
                        @$"Failed to process message '{consumerDefinition.MessageTypeName}', in '{consumerDefinition.ServiceType.Name}'. Message {message}, Total retries {consumerDefinition.RetryCount}");

                    await PublishBad(model, ea.Body, ea.BasicProperties, busOptions.DeadLetterExchange, consumerDefinition.BadRoutingKey, ex);
                    metrics.DeadLetterMoved.Add(1);
                    FireAndForget(new MessageMovedToDeadLetter(
                        DateTime.UtcNow,
                        busOptions.OperationalEventsSchemaVersion,
                        Environment.MachineName,
                        busOptions.EnvironmentName,
                        busOptions.ApplicationName ?? string.Empty,
                        busOptions.ProcessExchange,
                        consumerDefinition.QueueName,
                        consumerDefinition.ServiceType?.Name ?? string.Empty,
                        consumerDefinition.MessageTypeName,
                        commonMessageId,
                        commonCorrelationId,
                        commonCausationId,
                        traceId,
                        spanId,
                        ea.DeliveryTag,
                        busOptions.DeadLetterExchange,
                        consumerDefinition.BadRoutingKey));
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
            var payloadSizeBytes = (long)ea.Body.Length;
            var currentRetryCount = Math.Max(busOptions.ListenRetryCount - remainingRetryCount, 0);
            var parentContext = OperationalEventEnvelope.ExtractParentContext(ea.BasicProperties);
            using var activity = BusDiagnostics.ActivitySource.StartActivity("bus.listen node", ActivityKind.Consumer, parentContext);
            var stopwatch = Stopwatch.StartNew();
            var commonMessageId = OperationalEventEnvelope.GetMessageId(ea.BasicProperties);
            var commonCorrelationId = OperationalEventEnvelope.GetCorrelationId(ea.BasicProperties);
            var commonCausationId = OperationalEventEnvelope.GetCausationId(ea.BasicProperties);
            var traceId = activity?.TraceId.ToString() ?? OperationalEventEnvelope.GetTraceId(ea.BasicProperties);
            var spanId = activity?.SpanId.ToString() ?? OperationalEventEnvelope.GetSpanId(ea.BasicProperties);

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
                    listenerDefinition = listeners.SingleOrDefault(d =>
                        d.MessageType == Type.GetType(consumerMessage.MessageTypeName));
                    
                    if (listenerDefinition != null)
                    {
                        TryBuildBusRequestContext(scope.ServiceProvider, listenerDefinition.MessageTypeName, ea.BasicProperties, remainingRetryCount);
                        svc = scope.ServiceProvider.GetRequiredService(listenerDefinition.ServiceType);
                        processMethod = listenerDefinition.Method;
                        failMethod = listenerDefinition.FailMethod;
                        var messageObject =
                            JsonSerializer.Deserialize(consumerMessage.Message, listenerDefinition.MessageType);
                        await (Task)processMethod.Invoke(svc, new[] { messageObject });
                    }
                }

                model.BasicAck(ea.DeliveryTag, false);
                stopwatch.Stop();
                metrics.ProcessingCompleted.Add(1);
                metrics.ProcessingLatencyMs.Record(stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                metrics.ProcessingFailed.Add(1);
                if (remainingRetryCount != 0)
                {
                    // reject the message, will be sent to wait queue
                    model.BasicReject(ea.DeliveryTag, false);
                    metrics.RetryScheduled.Add(1);
                    await RunOnFail(svc, failMethod, ex, message);
                    logger.LogWarning(ex,
                        @$"Failed to process message '{listenerDefinition?.MessageTypeName ?? message}', 
                        in '{listenerDefinition?.ServiceType.Name ?? "reloading"}'. 
                        Number of retries remaining {remainingRetryCount}.
                        Total retries configured {busOptions.ListenRetryCount}.
                        Message {message}");
                }
                else
                {
                    model.BasicAck(ea.DeliveryTag, false);
                    logger.LogError(ex,
                        @$"Failed to process message '{listenerDefinition?.MessageTypeName} ?? ?? message', in 
                        '{listenerDefinition?.ServiceType.Name ?? "reloading"}'. 
                           Message {message}, Total retries {busOptions.ListenRetryCount}");

                    await PublishBad(model, ea.Body, ea.BasicProperties,busOptions.NodeDeadLetterExchange, busOptions.NodeBadRoutingKey, ex);
                    metrics.DeadLetterMoved.Add(1);
                    await RunOnFail(svc, failMethod, ex, message);
                }

                FireAndForget(new MessageProcessingFailed(
                    DateTime.UtcNow,
                    busOptions.OperationalEventsSchemaVersion,
                    Environment.MachineName,
                    busOptions.EnvironmentName,
                    busOptions.ApplicationName ?? string.Empty,
                    busOptions.NodeExchange,
                    busOptions.NodeQueueName,
                    listenerDefinition?.ServiceType?.Name ?? "node-listener",
                    listenerDefinition?.MessageTypeName ?? "node-control",
                    commonMessageId,
                    commonCorrelationId,
                    commonCausationId,
                    traceId,
                    spanId,
                    ea.DeliveryTag,
                    stopwatch.Elapsed.TotalMilliseconds,
                    currentRetryCount,
                    ex.GetType().FullName ?? ex.GetType().Name,
                    ex.Message,
                    ex.StackTrace,
                    payloadSizeBytes));
            }
        }

        private async Task RunOnFail(object svc, MethodInfo failMethod, Exception ex, string message)
        {
            if (svc == null || failMethod == null)
                return;
            try
            {
                await (Task)failMethod.Invoke(svc, new Object[] { ex });
            }
            catch (Exception)
            {
                logger.LogError(ex, $"Failed to run OnFail message, Message {message}");
            }
        }
        
        void TryBuildBusRequestContext(IServiceProvider serviceProvider,string messageName, IBasicProperties basicProperties,
            int remainingRetries)
        {
            var remainingRetriesValue = new RequestValue("RemainingRetries", remainingRetries.ToString(),
                RequestValueType.ServiceBusValue);
            var messageTypeValue = new RequestValue("MessageTypeName", messageName,
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
                requestContext?.AddValue(messageTypeValue);
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

        private void FireAndForget(IOperationalEvent evt)
        {
            operationalEventPublisher.Publish(evt)
                .ContinueWith(t => logger.LogWarning(t.Exception,
                        "Unobserved exception publishing operational event {EventType}.",
                        evt.GetType().Name),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}