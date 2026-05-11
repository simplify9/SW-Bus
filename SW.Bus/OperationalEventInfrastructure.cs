using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus;

internal sealed class OperationalEventBuffer
{
    public OperationalEventBuffer(BusOptions busOptions)
    {
        var fullMode = busOptions.OperationalEventsDropOldest
            ? BoundedChannelFullMode.DropOldest
            : BoundedChannelFullMode.DropWrite;

        Buffer = Channel.CreateBounded<IOperationalEvent>(new BoundedChannelOptions(busOptions.OperationalEventsBufferCapacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public Channel<IOperationalEvent> Buffer { get; }
}

internal sealed class OperationalEventChannelPublisher : IOperationalEventPublisher
{
    private readonly BusOptions busOptions;
    private readonly OperationalEventBuffer buffer;
    private readonly BusMetrics metrics;

    public OperationalEventChannelPublisher(BusOptions busOptions, OperationalEventBuffer buffer, BusMetrics metrics)
    {
        this.busOptions = busOptions;
        this.buffer = buffer;
        this.metrics = metrics;
    }

    public Task Publish(IOperationalEvent evt, CancellationToken cancellationToken = default)
    {
        if (!busOptions.OperationalEventsEnabled || evt == null)
            return Task.CompletedTask;

        if (!buffer.Buffer.Writer.TryWrite(evt))
            metrics.OperationalEventDropped.Add(1);

        return Task.CompletedTask;
    }
}

internal sealed class OperationalEventDispatcher : BackgroundService
{
    private readonly ILogger<OperationalEventDispatcher> logger;
    private readonly BusOptions busOptions;
    private readonly OperationalEventBuffer buffer;
    private readonly IEnumerable<IOperationalEventBatchSink> sinks;

    public OperationalEventDispatcher(
        ILogger<OperationalEventDispatcher> logger,
        BusOptions busOptions,
        OperationalEventBuffer buffer,
        IEnumerable<IOperationalEventBatchSink> sinks)
    {
        this.logger = logger;
        this.busOptions = busOptions;
        this.buffer = buffer;
        this.sinks = sinks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!busOptions.OperationalEventsEnabled)
            return;

        var reader        = buffer.Buffer.Reader;
        var flushInterval = TimeSpan.FromMilliseconds(busOptions.OperationalEventsFlushIntervalMs);
        var batch         = new List<IOperationalEvent>(busOptions.OperationalEventsBatchSize);

        // NOTE: PeriodicTimer.WaitForNextTickAsync only supports one outstanding
        // call at a time and throws InvalidOperationException if called concurrently.
        // The old Task.WhenAny pattern left the previous timer awaitable dangling on
        // each loop iteration, crashing the dispatcher. We replace it with a linked
        // CancellationTokenSource that times out after flushInterval — one token, zero
        // concurrent-call issues, and the same flush-on-idle semantics.

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 1. Drain into the batch (non-blocking fast path)
                while (batch.Count < busOptions.OperationalEventsBatchSize && reader.TryRead(out var evt))
                    batch.Add(evt);

                // 2. If the batch is full, flush immediately and loop
                if (batch.Count >= busOptions.OperationalEventsBatchSize)
                {
                    await FlushBatch(batch, stoppingToken);
                    continue;
                }

                // 3. Wait for either more data OR the flush interval to elapse
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(flushInterval);

                try
                {
                    // Blocks until data is available or the timeout fires
                    await reader.WaitToReadAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Flush interval elapsed — fall through to drain + flush below
                }

                // 4. One final drain after either condition
                while (batch.Count < busOptions.OperationalEventsBatchSize && reader.TryRead(out var evt2))
                    batch.Add(evt2);

                if (batch.Count > 0)
                    await FlushBatch(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            // Drain any remaining events on shutdown
            while (reader.TryRead(out var pending))
                batch.Add(pending);
            if (batch.Count > 0)
                await FlushBatch(batch, CancellationToken.None);
        }
    }

    private async Task FlushBatch(List<IOperationalEvent> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        try
        {
            foreach (var sink in sinks)
                await sink.PublishBatch(batch, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Operational event sink failed. Telemetry failure is ignored.");
        }
        finally
        {
            batch.Clear();
        }
    }
}

internal sealed class NullOperationalEventBatchSink : IOperationalEventBatchSink
{
    public Task PublishBatch(IReadOnlyList<IOperationalEvent> events, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal static class BusDiagnostics
{
    public const string ActivitySourceName = "SimplyWorks.Bus";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}

internal sealed class BusMetrics
{
    private readonly Meter meter;

    public BusMetrics(IServiceProvider serviceProvider)
    {
        var meterFactory = serviceProvider.GetService<IMeterFactory>();
        meter = meterFactory?.Create("SimplyWorks.Bus") ?? new Meter("SimplyWorks.Bus");

        PublishStarted = meter.CreateCounter<long>("sw_bus_publish_started_total");
        PublishCompleted = meter.CreateCounter<long>("sw_bus_publish_completed_total");
        PublishFailed = meter.CreateCounter<long>("sw_bus_publish_failed_total");
        ProcessingStarted = meter.CreateCounter<long>("sw_bus_processing_started_total");
        ProcessingCompleted = meter.CreateCounter<long>("sw_bus_processing_completed_total");
        ProcessingFailed = meter.CreateCounter<long>("sw_bus_processing_failed_total");
        RetryScheduled = meter.CreateCounter<long>("sw_bus_retry_scheduled_total");
        DeadLetterMoved = meter.CreateCounter<long>("sw_bus_dead_letter_total");
        OperationalEventDropped = meter.CreateCounter<long>("sw_bus_operational_event_dropped_total");
        ProcessingLatencyMs = meter.CreateHistogram<double>("sw_bus_processing_latency_ms");
        PublishLatencyMs = meter.CreateHistogram<double>("sw_bus_publish_latency_ms");
    }

    public Counter<long> PublishStarted { get; }
    public Counter<long> PublishCompleted { get; }
    public Counter<long> PublishFailed { get; }
    public Counter<long> ProcessingStarted { get; }
    public Counter<long> ProcessingCompleted { get; }
    public Counter<long> ProcessingFailed { get; }
    public Counter<long> RetryScheduled { get; }
    public Counter<long> DeadLetterMoved { get; }
    public Counter<long> OperationalEventDropped { get; }
    public Histogram<double> ProcessingLatencyMs { get; }
    public Histogram<double> PublishLatencyMs { get; }
}

internal static class OperationalEventEnvelope
{
    internal const string TraceParentHeader = "traceparent";
    internal const string TraceStateHeader = "tracestate";
    internal const string TraceIdHeader = "trace-id";
    internal const string SpanIdHeader = "span-id";
    internal const string CausationIdHeader = "causation-id";

    internal static string HeaderAsString(IDictionary<string, object> headers, string key)
    {
        if (headers == null || !headers.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is byte[] bytes)
            return System.Text.Encoding.UTF8.GetString(bytes);

        if (value is ReadOnlyMemory<byte> memory)
            return System.Text.Encoding.UTF8.GetString(memory.Span);

        return value.ToString();
    }

    internal static string GetMessageId(RabbitMQ.Client.IBasicProperties props)
        => props?.MessageId
           ?? HeaderAsString(props?.Headers, "Id")
           ?? string.Empty;

    internal static string GetCorrelationId(RabbitMQ.Client.IBasicProperties props)
        => props?.CorrelationId
           ?? HeaderAsString(props?.Headers, "X-Correlation-Id")
           ?? string.Empty;

    internal static string GetCausationId(RabbitMQ.Client.IBasicProperties props)
        => HeaderAsString(props?.Headers, CausationIdHeader) ?? string.Empty;

    internal static string GetTraceId(RabbitMQ.Client.IBasicProperties props)
        => HeaderAsString(props?.Headers, TraceIdHeader) ?? string.Empty;

    internal static string GetSpanId(RabbitMQ.Client.IBasicProperties props)
        => HeaderAsString(props?.Headers, SpanIdHeader) ?? string.Empty;

    internal static ActivityContext ExtractParentContext(RabbitMQ.Client.IBasicProperties props)
    {
        var traceParent = HeaderAsString(props?.Headers, TraceParentHeader);
        if (string.IsNullOrWhiteSpace(traceParent))
            return default;

        var traceState = HeaderAsString(props?.Headers, TraceStateHeader);
        return ActivityContext.TryParse(traceParent, traceState, out var parentContext)
            ? parentContext
            : default;
    }
}

