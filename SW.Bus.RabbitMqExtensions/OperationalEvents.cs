namespace SW.Bus.RabbitMqExtensions;

/// <summary>
/// Represents a strongly typed operational event emitted by the bus runtime.
/// </summary>
public interface IOperationalEvent
{
    /// <summary>
    /// Gets the UTC timestamp when the operational event is produced.
    /// </summary>
    DateTime TimestampUtc { get; }
}

/// <summary>
/// Publishes operational events asynchronously.
/// Implementations must tolerate telemetry failures and should avoid blocking message processing paths.
/// </summary>
public interface IOperationalEventPublisher
{
    /// <summary>
    /// Publishes an operational event to the configured telemetry pipeline.
    /// </summary>
    /// <param name="evt">The operational event to publish.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task Publish(IOperationalEvent evt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Batch sink contract for writing operational events to external systems.
/// </summary>
public interface IOperationalEventBatchSink
{
    /// <summary>
    /// Writes a batch of operational events.
    /// </summary>
    /// <param name="events">The batch to write.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task PublishBatch(IReadOnlyList<IOperationalEvent> events, CancellationToken cancellationToken = default);
}

/// <summary>
/// Base contract for event models with shared lifecycle envelope metadata.
/// </summary>
public abstract record OperationalEventBase(
    DateTime TimestampUtc,
    string SchemaVersion,
    string EventName,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag) : IOperationalEvent;

/// <summary>
/// Base type for message processing events.
/// </summary>
public abstract record MessageProcessingEventBase(
    DateTime TimestampUtc,
    string SchemaVersion,
    string EventName,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    double? ProcessingDurationMs,
    int RetryCount,
    string? ExceptionType,
    string? ExceptionMessage,
    string? StackTrace,
    long PayloadSizeBytes)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        EventName,
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);

public sealed record MessageProcessingStarted(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    int RetryCount,
    long PayloadSizeBytes)
    : MessageProcessingEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(MessageProcessingStarted),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag,
        null,
        RetryCount,
        null,
        null,
        null,
        PayloadSizeBytes);

public sealed record MessageProcessingCompleted(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    double? ProcessingDurationMs,
    int RetryCount,
    long PayloadSizeBytes)
    : MessageProcessingEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(MessageProcessingCompleted),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag,
        ProcessingDurationMs,
        RetryCount,
        null,
        null,
        null,
        PayloadSizeBytes);

public sealed record MessageProcessingFailed(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    double? ProcessingDurationMs,
    int RetryCount,
    string? ExceptionType,
    string? ExceptionMessage,
    string? StackTrace,
    long PayloadSizeBytes)
    : MessageProcessingEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(MessageProcessingFailed),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag,
        ProcessingDurationMs,
        RetryCount,
        ExceptionType,
        ExceptionMessage,
        StackTrace,
        PayloadSizeBytes);

public sealed record MessageRetryScheduled(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    int RetryCount,
    int RemainingRetryCount,
    uint RetryDelaySeconds)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(MessageRetryScheduled),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);

public sealed record MessageMovedToDeadLetter(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    string DeadLetterExchange,
    string DeadLetterRoutingKey)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(MessageMovedToDeadLetter),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);

public sealed record ConsumerConnected(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    string ConsumerTag)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(ConsumerConnected),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);

public sealed record ConsumerDisconnected(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    string Reason)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(ConsumerDisconnected),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);

public sealed record QueueBackpressureDetected(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    long QueueDepth,
    ushort QueuePrefetch,
    long Threshold)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(QueueBackpressureDetected),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);

public sealed record PublishStarted(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    long PayloadSizeBytes)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(PublishStarted),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);

public sealed record PublishCompleted(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    double PublishDurationMs,
    long PayloadSizeBytes)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(PublishCompleted),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);

public sealed record PublishFailed(
    DateTime TimestampUtc,
    string SchemaVersion,
    string MachineName,
    string Environment,
    string ApplicationName,
    string Exchange,
    string QueueName,
    string ConsumerName,
    string MessageType,
    string MessageId,
    string CorrelationId,
    string CausationId,
    string TraceId,
    string SpanId,
    ulong? DeliveryTag,
    double? PublishDurationMs,
    string? ExceptionType,
    string? ExceptionMessage,
    string? StackTrace,
    long PayloadSizeBytes)
    : OperationalEventBase(
        TimestampUtc,
        SchemaVersion,
        nameof(PublishFailed),
        MachineName,
        Environment,
        ApplicationName,
        Exchange,
        QueueName,
        ConsumerName,
        MessageType,
        MessageId,
        CorrelationId,
        CausationId,
        TraceId,
        SpanId,
        DeliveryTag);


