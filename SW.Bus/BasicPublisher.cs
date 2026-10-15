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

/// <summary>
/// Publishes for one DI scope on a channel of its own, opened on the scope's first publish and
/// closed with the scope. The channel is safe to publish on from several threads of the scope.
/// </summary>
internal class BasicPublisher(
    BusConnection busConnection,
    BusOptions busOptions,
    RequestContext requestContext,
    IOperationalEventPublisher operationalEventPublisher,
    BusMetrics metrics) : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim channelLock = new(1, 1);
    private IChannel channel;

    async public Task Publish<TMessage>(TMessage message, string exchange, byte? priority = null)
    {
        var serializerOptions = new JsonSerializerOptions()
        {
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };
        var body = JsonSerializer.Serialize(message,message.GetType(), serializerOptions);
        
        await Publish(message.GetType().Name, body,exchange, priority);
    }

    public async Task Publish(string messageTypeName, string message, string exchange, byte? priority = null,
        string valuesHeader = null)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(message);
            await Publish(messageTypeName, body, exchange, priority, valuesHeader);
        }
        catch (Exception e)
        {
            throw new Exception($"Publish message to exchange {exchange} failed {messageTypeName} : {message} ", e);
        }

    }

    /// <param name="valuesHeader">Written by <see cref="PublishedValues.ToHeader"/>, which also checks its size.</param>
    public async Task Publish(string messageTypeName, byte[] message, string exchange, byte? priority = null,
        string valuesHeader = null)
    {
        var activity = BusDiagnostics.ActivitySource.StartActivity($"bus.publish {messageTypeName}", ActivityKind.Producer);
        var stopwatch = Stopwatch.StartNew();

        var props = new BasicProperties();
        props.DeliveryMode = DeliveryModes.Persistent;
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
            // CausationId = the span that *caused* this publish (the parent), not the publish span itself.
            // Omit when there is no parent so GetCausationId(props) correctly returns null.
            if (activity.ParentId != null)
                props.Headers[OperationalEventEnvelope.CausationIdHeader] = activity.ParentSpanId.ToString();
        }

        // One header for all of them, under the name RequestContext already reserves for it, so a
        // value cannot collide with a header the bus itself sets.
        if (valuesHeader != null)
            props.Headers[RequestContext.ValuesHeaderName] = valuesHeader;

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
            using (var timeout = busOptions.PublishTimeout > TimeSpan.Zero
                       ? new CancellationTokenSource(busOptions.PublishTimeout)
                       : null)
            {
                var cancellationToken = timeout?.Token ?? CancellationToken.None;
                try
                {
                    var publishChannel = await GetChannelAsync(cancellationToken);
                    // With confirms on, this completes when the broker has the message, and throws when the
                    // broker refuses it or the connection is lost before it answers.
                    await publishChannel.BasicPublishAsync(exchange, messageTypeName.ToLower(), false, props, message,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
                {
                    // The channel may hold a half-sent message or a confirm still owed; the next publish of
                    // this scope opens a fresh one instead.
                    await DisposeChannelAsync();
                    throw new BusException(
                        $"The broker did not take '{messageTypeName}' within {busOptions.PublishTimeout.TotalSeconds:0.#} s; " +
                        "it may be blocking publishers (a memory or disk alarm). The broker may or may not have the message.");
                }
            }

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
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        var current = channel;
        if (current is { IsClosed: false }) return current;

        await channelLock.WaitAsync(cancellationToken);
        try
        {
            // Closed by the broker (a channel error) or never opened: open one. A channel that is
            // recovering with its connection is not closed and is kept.
            if (channel is { IsClosed: false }) return channel;
            if (channel != null) await DisposeChannelAsync();

            var connection = await busConnection.GetPublisherConnectionAsync(cancellationToken);
            channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                publisherConfirmationsEnabled: busOptions.PublisherConfirms,
                publisherConfirmationTrackingEnabled: busOptions.PublisherConfirms), cancellationToken);
            return channel;
        }
        finally
        {
            channelLock.Release();
        }
    }

    private async Task DisposeChannelAsync()
    {
        var closing = channel;
        channel = null;
        if (closing == null) return;
        await CloseBoundedAsync(closing);
    }

    /// <summary>
    /// Closes a channel, waiting at most <see cref="BusConnection.CloseTimeout"/>. A broker that blocks
    /// publishers stops reading the connection, so an unbounded close would hold the scope (an HTTP
    /// request, a consumed message) until the alarm clears; past the bound it finishes in the background.
    /// </summary>
    internal static async Task CloseBoundedAsync(IChannel closing)
    {
        var close = Task.Run(async () =>
        {
            try
            {
                if (closing.IsOpen) await closing.CloseAsync();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Failed to close a publisher channel: {0}", ex.Message);
            }
            finally
            {
                await closing.DisposeAsync();
            }
        });
        await Task.WhenAny(close, Task.Delay(BusConnection.CloseTimeout));
    }

    public async ValueTask DisposeAsync() => await DisposeChannelAsync();

    // Scopes disposed synchronously (a plain "using var scope = CreateScope()") land here.
    public void Dispose() => DisposeChannelAsync().GetAwaiter().GetResult();

    private void FireAndForget(IOperationalEvent evt)
    {
        operationalEventPublisher.Publish(evt, CancellationToken.None)
            .ContinueWith(t => Trace.TraceError(
                    "Unobserved exception publishing operational event {0}: {1}",
                    evt.GetType().Name, t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}