namespace SW.Bus.RabbitMqExtensions;

// ─────────────────────────────────────────────────────────────────────────────
// Enumerations
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Severity level used for dashboard alerts and consumer health status.
/// </summary>
public enum AlertSeverity
{
    /// <summary>Consumer is healthy; no action required.</summary>
    Info,
    /// <summary>Elevated retry count, backpressure, or publish/ack imbalance detected.</summary>
    Warning,
    /// <summary>Consumer disconnected, large dead-letter backlog, or queue saturation.</summary>
    Critical
}

// ─────────────────────────────────────────────────────────────────────────────
// Filter / Query models
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Query parameters for filtering the in-memory operational event store.
/// All string filters are case-insensitive substring matches.
/// </summary>
/// <param name="ApplicationName">Filter by emitting application name.</param>
/// <param name="ConsumerName">Filter by consumer service class name.</param>
/// <param name="MessageType">Filter by message type name (e.g. "OrderCreated").</param>
/// <param name="CorrelationId">Filter by correlation ID for request tracing.</param>
/// <param name="TraceId">Filter by distributed trace ID (W3C TraceParent).</param>
/// <param name="QueueName">Filter by full queue name.</param>
/// <param name="EventName">Exact match on EventName (e.g. "MessageProcessingFailed").</param>
/// <param name="From">Inclusive lower bound on <see cref="IOperationalEvent.TimestampUtc"/>.</param>
/// <param name="To">Inclusive upper bound on <see cref="IOperationalEvent.TimestampUtc"/>.</param>
/// <param name="Limit">Maximum number of events to return (most recent first). Default: 200.</param>
public record OperationalEventFilter(
    string? ApplicationName = null,
    string? ConsumerName = null,
    string? MessageType = null,
    string? CorrelationId = null,
    string? TraceId = null,
    string? QueueName = null,
    string? EventName = null,
    DateTime? From = null,
    DateTime? To = null,
    int Limit = 200);

// ─────────────────────────────────────────────────────────────────────────────
// View-model records (usable by custom dashboards without SW.Bus.RabbitMqViewer)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Top-level summary shown on the global overview dashboard card row.
/// </summary>
/// <param name="TotalConsumers">Total number of registered consumers across all queues.</param>
/// <param name="UnhealthyConsumers">Number of consumers with <see cref="AlertSeverity.Warning"/> or <see cref="AlertSeverity.Critical"/> status.</param>
/// <param name="DisconnectedConsumers">Number of consumers with zero active nodes (TotalNodes == 0).</param>
/// <param name="TotalQueueDepth">Sum of all main queue message counts.</param>
/// <param name="TotalRetryBacklog">Sum of all retry-queue message counts.</param>
/// <param name="TotalDeadLetterBacklog">Sum of all dead-letter-queue message counts.</param>
/// <param name="TotalIncomingRate">Aggregate publish rate across all queues (msg/s).</param>
/// <param name="TotalAckRate">Aggregate acknowledge rate across all queues (msg/s).</param>
/// <param name="ActiveAlerts">Total number of active dashboard alerts.</param>
/// <param name="LastUpdatedUtc">UTC timestamp of the most recent data refresh from the management API.</param>
public record DashboardSummary(
    int TotalConsumers,
    int UnhealthyConsumers,
    int DisconnectedConsumers,
    long TotalQueueDepth,
    long TotalRetryBacklog,
    long TotalDeadLetterBacklog,
    double TotalIncomingRate,
    double TotalAckRate,
    int ActiveAlerts,
    DateTime LastUpdatedUtc);

/// <summary>
/// Per-consumer health row, extending raw <see cref="ConsumerCount"/> with
/// derived health status and backpressure flag for use in operational dashboards.
/// </summary>
/// <param name="Name">Consumer service class name (e.g. "OrderConsumer").</param>
/// <param name="MessageName">Message type being consumed (e.g. "OrderCreated").</param>
/// <param name="QueueName">Fully qualified RabbitMQ queue name.</param>
/// <param name="TotalNodes">Number of active consumer instances (nodes).</param>
/// <param name="ProcessingCount">Messages currently unacknowledged (in-flight).</param>
/// <param name="QueueCount">Messages waiting in the main queue.</param>
/// <param name="RetryCount">Messages in the retry queue.</param>
/// <param name="FailedCount">Messages in the dead-letter (bad) queue.</param>
/// <param name="Priority">Consumer priority level configured via <see cref="ConsumerOptions"/>.</param>
/// <param name="Prefetch">QoS prefetch count.</param>
/// <param name="IncomingRate">Message publish rate into the queue (msg/s).</param>
/// <param name="ProcessingRate">Message deliver rate to consumers (msg/s).</param>
/// <param name="AckRate">Message acknowledge rate from consumers (msg/s).</param>
/// <param name="IsBackpressured">True when queue depth exceeds <see cref="BusOptions.QueueBackpressureThreshold"/>.</param>
/// <param name="HealthStatus">Derived health severity for color-coding in dashboards.</param>
public record ConsumerHealthView(
    string Name,
    string MessageName,
    string QueueName,
    long TotalNodes,
    long ProcessingCount,
    long QueueCount,
    long RetryCount,
    long FailedCount,
    int Priority,
    ushort Prefetch,
    double IncomingRate,
    double ProcessingRate,
    double AckRate,
    bool IsBackpressured,
    AlertSeverity HealthStatus);

/// <summary>
/// Queue-centric view combining main, retry, and dead-letter queue statistics.
/// </summary>
/// <param name="QueueName">Full name of the main processing queue.</param>
/// <param name="RetryQueueName">Full name of the retry queue.</param>
/// <param name="DeadLetterQueueName">Full name of the dead-letter (bad) queue.</param>
/// <param name="Messages">Messages waiting in the main queue.</param>
/// <param name="Consumers">Number of active consumer connections.</param>
/// <param name="Unacknowledged">Messages in-flight (unacknowledged by consumers).</param>
/// <param name="RetryMessages">Messages currently waiting in the retry queue.</param>
/// <param name="DeadLetterMessages">Messages accumulated in the dead-letter queue.</param>
/// <param name="IncomingRate">Message publish rate (msg/s).</param>
/// <param name="ProcessingRate">Message deliver rate to consumers (msg/s).</param>
/// <param name="AckRate">Message acknowledge rate (msg/s).</param>
public record QueueDetailView(
    string QueueName,
    string RetryQueueName,
    string DeadLetterQueueName,
    long Messages,
    long Consumers,
    long Unacknowledged,
    long RetryMessages,
    long DeadLetterMessages,
    double IncomingRate,
    double ProcessingRate,
    double AckRate);

/// <summary>
/// Retry analysis view for consumers with non-zero retry backlogs.
/// Ordered by backlog size descending.
/// </summary>
/// <param name="ConsumerName">Consumer service class name.</param>
/// <param name="MessageName">Message type being consumed.</param>
/// <param name="QueueName">Fully qualified main queue name.</param>
/// <param name="RetryBacklog">Current number of messages in the retry queue.</param>
/// <param name="IncomingRate">Publish rate (msg/s).</param>
/// <param name="AckRate">Acknowledge rate (msg/s). Low ack rate with high retry indicates processing issues.</param>
/// <param name="Severity">Alert severity based on retry backlog size.</param>
public record RetryAnalysisView(
    string ConsumerName,
    string MessageName,
    string QueueName,
    long RetryBacklog,
    double IncomingRate,
    double AckRate,
    AlertSeverity Severity);

/// <summary>
/// Dead-letter summary view for consumers with non-zero dead-letter backlogs.
/// Ordered by dead-letter count descending.
/// </summary>
/// <param name="ConsumerName">Consumer service class name.</param>
/// <param name="MessageName">Message type being consumed.</param>
/// <param name="DeadLetterQueueName">Full name of the dead-letter queue.</param>
/// <param name="DeadLetterCount">Total messages accumulated in the dead-letter queue.</param>
/// <param name="LastExceptionType">Exception type from the most recently sampled failed message.</param>
/// <param name="LastExceptionMessage">Exception message from the most recently sampled failed message.</param>
/// <param name="LastFailedAt">Timestamp of the most recently observed failure from the operational event store.</param>
/// <param name="Severity">Alert severity based on dead-letter count.</param>
public record DeadLetterSummaryView(
    string ConsumerName,
    string MessageName,
    string DeadLetterQueueName,
    long DeadLetterCount,
    string? LastExceptionType,
    string? LastExceptionMessage,
    DateTime? LastFailedAt,
    AlertSeverity Severity);

/// <summary>
/// A single operational alert surfaced by <see cref="IAlertEvaluator"/>.
/// </summary>
/// <param name="Severity">Alert severity level.</param>
/// <param name="Title">Short alert title suitable for dashboard card headers.</param>
/// <param name="Detail">Human-readable explanation of the alert condition.</param>
/// <param name="QueueName">The queue where the condition was detected.</param>
/// <param name="ConsumerName">The consumer where the condition was detected.</param>
/// <param name="TimestampUtc">UTC time when the alert was generated.</param>
public record DashboardAlert(
    AlertSeverity Severity,
    string Title,
    string Detail,
    string QueueName,
    string ConsumerName,
    DateTime TimestampUtc);

// ─────────────────────────────────────────────────────────────────────────────
// Public interfaces
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Provides read access to the in-memory ring buffer of operational events emitted by the bus.
/// Register a custom implementation (e.g. backed by Elasticsearch or ClickHouse) to replace the
/// built-in ring buffer without changing any dashboard or consuming code.
/// </summary>
public interface IOperationalEventStore
{
    /// <summary>
    /// Returns the most recent events matching the optional filter, ordered newest-first.
    /// </summary>
    /// <param name="filter">Optional filter parameters. Null returns up to 200 most recent events.</param>
    IReadOnlyList<IOperationalEvent> GetRecent(OperationalEventFilter? filter = null);

    /// <summary>
    /// Total number of events received since startup (may exceed ring-buffer capacity).
    /// </summary>
    long TotalReceived { get; }
}

/// <summary>
/// Evaluates <see cref="ConsumerHealthView"/> data and produces a list of active <see cref="DashboardAlert"/> objects.
/// Implement this interface to apply custom threshold logic or severity classification rules.
/// </summary>
public interface IAlertEvaluator
{
    /// <summary>
    /// Produces a list of alerts from the current consumer health snapshot.
    /// </summary>
    /// <param name="consumers">Current consumer health views from <see cref="IBusDashboardDataService.GetConsumerHealthAsync"/>.</param>
    IReadOnlyList<DashboardAlert> Evaluate(ConsumerHealthView[] consumers);
}

/// <summary>
/// Unified data service for all bus operations dashboard views.
/// Aggregate layer over <see cref="IConsumerReader"/>, <see cref="IErrorQueueReader"/>,
/// <see cref="IOperationalEventStore"/>, and <see cref="IAlertEvaluator"/>.
/// <para>
/// This interface is intentionally public in <c>SW.Bus.RabbitMqExtensions</c> so that
/// users can build their own custom dashboards, REST APIs, or monitoring tools without
/// taking a dependency on <c>SW.Bus.RabbitMqViewer</c>.
/// </para>
/// </summary>
public interface IBusDashboardDataService
{
    /// <summary>
    /// Gets the top-level dashboard summary (counts, rates, alert totals).
    /// </summary>
    Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets health details for all registered consumers, including derived health status.
    /// </summary>
    Task<ConsumerHealthView[]> GetConsumerHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets queue-centric statistics including main, retry, and dead-letter queue depths.
    /// </summary>
    Task<QueueDetailView[]> GetQueueDetailsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets retry analysis for consumers with non-zero retry backlogs ordered by severity.
    /// </summary>
    Task<RetryAnalysisView[]> GetRetryAnalysisAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets dead-letter summary for consumers with non-zero dead-letter backlogs ordered by count descending.
    /// </summary>
    Task<DeadLetterSummaryView[]> GetDeadLetterSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates and returns active dashboard alerts using the registered <see cref="IAlertEvaluator"/>.
    /// Pass cached consumers to avoid a redundant API call.
    /// </summary>
    /// <param name="consumers">Pre-fetched consumer health views, or null to fetch fresh data.</param>
    Task<IReadOnlyList<DashboardAlert>> GetAlertsAsync(ConsumerHealthView[]? consumers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the operational event store using the specified filter.
    /// </summary>
    /// <param name="filter">Filter parameters (service, message type, correlation ID, time range, etc.).</param>
    IReadOnlyList<IOperationalEvent> GetRecentEvents(OperationalEventFilter? filter = null);
}

