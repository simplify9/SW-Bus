using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using SW.Bus.RabbitMqExtensions;
using SW.HttpExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus;

internal class BasicPublisher
{
    private readonly IModel model;
    private readonly BusOptions busOptions;
    private readonly RequestContext requestContext;
    private readonly IOperationalEventPublisher operationalEventPublisher;
    private readonly BusMetrics metrics;

    public BasicPublisher(
        IModel model,
        BusOptions busOptions,
        RequestContext requestContext,
        IOperationalEventPublisher operationalEventPublisher,
        BusMetrics metrics)
    {
        this.model = model;
        this.busOptions = busOptions;
        this.requestContext = requestContext;
        this.operationalEventPublisher = operationalEventPublisher;
        this.metrics = metrics;
    }

    async public Task Publish<TMessage>(TMessage message, string exchange, byte? priority = null)
    {
        var serializerOptions = new JsonSerializerOptions()
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };
        var body = JsonSerializer.Serialize(message,message.GetType(), serializerOptions);
        
        await Publish(message.GetType().Name, body,exchange, priority);
    }

    public async Task Publish(string messageTypeName, string message,string exchange, byte? priority = null)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(message);
            await Publish(messageTypeName, body,exchange, priority);
        }
        catch (Exception e)
        {
            throw new Exception($"Publish message to exchange {exchange} failed {messageTypeName} : {message} ", e);
        }

    }

    public Task Publish(string messageTypeName, byte[] message,string exchange, byte? priority = null)
    {
        var activity = BusDiagnostics.ActivitySource.StartActivity($"bus.publish {messageTypeName}", ActivityKind.Producer);
        var stopwatch = Stopwatch.StartNew();

        var props = model.CreateBasicProperties();
        props.DeliveryMode = 2;
        props.Headers = new Dictionary<string, object>();
        props.MessageId = Guid.NewGuid().ToString("N");

        if (requestContext.CorrelationId != null)
            props.CorrelationId = requestContext.CorrelationId;

        if (priority.HasValue)
        {
            props.Priority = priority.Value;
        }

        if (requestContext.IsValid && busOptions.Token.IsValid)
        {
            var jwt = busOptions.Token.WriteJwt((ClaimsIdentity)requestContext.User.Identity);
            props.Headers.Add(RequestContext.UserHeaderName, jwt);
        }

        if (requestContext.CorrelationId != null)
            props.Headers.Add(RequestContext.CorrelationIdHeaderName, requestContext.CorrelationId);

        if (activity != null)
        {
            props.Headers[OperationalEventEnvelope.TraceParentHeader] = activity.Id;
            if (!string.IsNullOrWhiteSpace(activity.TraceStateString))
                props.Headers[OperationalEventEnvelope.TraceStateHeader] = activity.TraceStateString;
            props.Headers[OperationalEventEnvelope.TraceIdHeader] = activity.TraceId.ToString();
            props.Headers[OperationalEventEnvelope.SpanIdHeader] = activity.SpanId.ToString();
            props.Headers[OperationalEventEnvelope.CausationIdHeader] = activity.SpanId.ToString();
        }

        props.Headers.Add(BusOptions.SourceNodeIdHeaderName,busOptions.NodeId);
        props.Headers.Add("Id", props.MessageId);

        metrics.PublishStarted.Add(1);
        FireAndForget(new PublishStarted(
            DateTime.UtcNow,
            busOptions.OperationalEventsSchemaVersion,
            Environment.MachineName,
            busOptions.EnvironmentName,
            busOptions.ApplicationName ?? string.Empty,
            exchange,
            string.Empty,
            string.Empty,
            messageTypeName,
            props.MessageId,
            props.CorrelationId ?? string.Empty,
            OperationalEventEnvelope.GetCausationId(props),
            activity?.TraceId.ToString() ?? string.Empty,
            activity?.SpanId.ToString() ?? string.Empty,
            null,
            message?.LongLength ?? 0));

        try
        {
            model.BasicPublish(exchange, messageTypeName.ToLower(), props, message);
            stopwatch.Stop();
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", exchange);
            activity?.SetTag("messaging.operation", "publish");
            activity?.SetTag("messaging.message.id", props.MessageId);

            metrics.PublishCompleted.Add(1);
            metrics.PublishLatencyMs.Record(stopwatch.Elapsed.TotalMilliseconds);
            FireAndForget(new PublishCompleted(
                DateTime.UtcNow,
                busOptions.OperationalEventsSchemaVersion,
                Environment.MachineName,
                busOptions.EnvironmentName,
                busOptions.ApplicationName ?? string.Empty,
                exchange,
                string.Empty,
                string.Empty,
                messageTypeName,
                props.MessageId,
                props.CorrelationId ?? string.Empty,
                OperationalEventEnvelope.GetCausationId(props),
                activity?.TraceId.ToString() ?? string.Empty,
                activity?.SpanId.ToString() ?? string.Empty,
                null,
                stopwatch.Elapsed.TotalMilliseconds,
                message?.LongLength ?? 0));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.PublishFailed.Add(1);
            FireAndForget(new PublishFailed(
                DateTime.UtcNow,
                busOptions.OperationalEventsSchemaVersion,
                Environment.MachineName,
                busOptions.EnvironmentName,
                busOptions.ApplicationName ?? string.Empty,
                exchange,
                string.Empty,
                string.Empty,
                messageTypeName,
                props.MessageId,
                props.CorrelationId ?? string.Empty,
                OperationalEventEnvelope.GetCausationId(props),
                activity?.TraceId.ToString() ?? string.Empty,
                activity?.SpanId.ToString() ?? string.Empty,
                null,
                stopwatch.Elapsed.TotalMilliseconds,
                ex.GetType().FullName ?? ex.GetType().Name,
                ex.Message,
                ex.StackTrace,
                message?.LongLength ?? 0));
            throw;
        }
        finally
        {
            activity?.Dispose();
        }

        return Task.CompletedTask;
    }

    private void FireAndForget(IOperationalEvent evt)
    {
        _ = operationalEventPublisher.Publish(evt, CancellationToken.None);
    }
}