using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus;

/// <summary>
/// Aggregate data service that combines <see cref="IConsumerReader"/>,
/// <see cref="IOperationalEventStore"/>, and <see cref="IAlertEvaluator"/> into a single
/// façade used by both the built-in viewer (<c>SW.Bus.RabbitMqViewer</c>) and any
/// custom dashboard implementation consuming <see cref="IBusDashboardDataService"/>.
/// </summary>
internal sealed class BusDashboardDataService : IBusDashboardDataService
{
    private readonly IConsumerReader consumerReader;
    private readonly IOperationalEventStore eventStore;
    private readonly IAlertEvaluator alertEvaluator;
    private readonly BusOptions busOptions;

    public BusDashboardDataService(
        IConsumerReader consumerReader,
        IOperationalEventStore eventStore,
        IAlertEvaluator alertEvaluator,
        BusOptions busOptions)
    {
        this.consumerReader  = consumerReader;
        this.eventStore      = eventStore;
        this.alertEvaluator  = alertEvaluator;
        this.busOptions      = busOptions;
    }

    /// <inheritdoc />
    public async Task<ConsumerHealthView[]> GetConsumerHealthAsync(CancellationToken cancellationToken = default)
    {
        var counts = await consumerReader.GetAllConsumersCount();
        return counts.Select(ToHealthView).ToArray();
    }

    /// <inheritdoc />
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetConsumerHealthAsync(cancellationToken);
        var alerts = alertEvaluator.Evaluate(health);
        var lastUpdated = await consumerReader.LastUpdated;

        return new DashboardSummary(
            health.Length,
            health.Count(c => c.HealthStatus != AlertSeverity.Info),
            health.Count(c => c.TotalNodes == 0),
            health.Sum(c => c.QueueCount),
            health.Sum(c => c.RetryCount),
            health.Sum(c => c.FailedCount),
            health.Sum(c => c.IncomingRate),
            health.Sum(c => c.AckRate),
            alerts.Count,
            lastUpdated);
    }

    /// <inheritdoc />
    public async Task<QueueDetailView[]> GetQueueDetailsAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetConsumerHealthAsync(cancellationToken);
        return health.Select(c => new QueueDetailView(
            c.QueueName,
            $"{c.QueueName}.retry",
            $"{c.QueueName}.bad",
            c.QueueCount,
            c.TotalNodes,
            c.ProcessingCount,
            c.RetryCount,
            c.FailedCount,
            c.IncomingRate,
            c.ProcessingRate,
            c.AckRate)).ToArray();
    }

    /// <inheritdoc />
    public async Task<RetryAnalysisView[]> GetRetryAnalysisAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetConsumerHealthAsync(cancellationToken);
        return health
            .Where(c => c.RetryCount > 0)
            .OrderByDescending(c => c.RetryCount)
            .Select(c => new RetryAnalysisView(
                c.Name,
                c.MessageName,
                c.QueueName,
                c.RetryCount,
                c.IncomingRate,
                c.AckRate,
                c.RetryCount >= busOptions.AlertRetryCriticalThreshold
                    ? AlertSeverity.Critical
                    : AlertSeverity.Warning))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<DeadLetterSummaryView[]> GetDeadLetterSummaryAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetConsumerHealthAsync(cancellationToken);

        // Enrich with last seen failure from the in-memory event store
        var recentFailures = eventStore.GetRecent(new OperationalEventFilter(
            EventName: nameof(MessageMovedToDeadLetter),
            Limit: 1000));

        var lastFailureByQueue = recentFailures
            .OfType<MessageMovedToDeadLetter>()
            .GroupBy(e => e.QueueName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.TimestampUtc).First(),
                StringComparer.OrdinalIgnoreCase);

        var recentErrors = eventStore.GetRecent(new OperationalEventFilter(
            EventName: nameof(MessageProcessingFailed),
            Limit: 1000));

        var lastErrorByQueue = recentErrors
            .OfType<MessageProcessingFailed>()
            .GroupBy(e => e.QueueName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.TimestampUtc).First(),
                StringComparer.OrdinalIgnoreCase);

        return health
            .Where(c => c.FailedCount > 0)
            .OrderByDescending(c => c.FailedCount)
            .Select(c =>
            {
                lastErrorByQueue.TryGetValue(c.QueueName, out var lastErr);
                lastFailureByQueue.TryGetValue(c.QueueName, out var lastDl);

                return new DeadLetterSummaryView(
                    c.Name,
                    c.MessageName,
                    $"{c.QueueName}.bad",
                    c.FailedCount,
                    lastErr?.ExceptionType,
                    lastErr?.ExceptionMessage,
                    lastDl?.TimestampUtc,
                    c.FailedCount >= busOptions.AlertDeadLetterCriticalThreshold
                        ? AlertSeverity.Critical
                        : AlertSeverity.Warning);
            })
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardAlert>> GetAlertsAsync(
        ConsumerHealthView[]? consumers = null,
        CancellationToken cancellationToken = default)
    {
        consumers ??= await GetConsumerHealthAsync(cancellationToken);
        return alertEvaluator.Evaluate(consumers);
    }

    /// <inheritdoc />
    public IReadOnlyList<IOperationalEvent> GetRecentEvents(OperationalEventFilter? filter = null)
        => eventStore.GetRecent(filter);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ConsumerHealthView ToHealthView(ConsumerCount c)
    {
        var prefix    = busOptions.ProcessExchange +
                        (string.IsNullOrWhiteSpace(busOptions.ApplicationName) ? "" : $".{busOptions.ApplicationName}");
        var queueName = $"{prefix}.{c.Name.ToLower()}.{c.MessageName.ToLower()}";

        var isBackpressured = busOptions.QueueBackpressureThreshold > 0 &&
                              c.QueueCount >= busOptions.QueueBackpressureThreshold;

        AlertSeverity health;
        if (c.TotalNodes == 0 || c.FailedCount >= busOptions.AlertDeadLetterCriticalThreshold)
            health = AlertSeverity.Critical;
        else if (c.FailedCount > 0 || c.RetryCount > busOptions.AlertRetryWarningThreshold || isBackpressured)
            health = AlertSeverity.Warning;
        else
            health = AlertSeverity.Info;

        return new ConsumerHealthView(
            c.Name,
            c.MessageName,
            queueName,
            c.TotalNodes,
            c.ProcessingCount,
            c.QueueCount,
            c.RetryCount,
            c.FailedCount,
            c.Priority,
            c.Prefetch,
            c.IncomingRate,
            c.ProcessingRate,
            c.AckRate,
            isBackpressured,
            health);
    }
}

