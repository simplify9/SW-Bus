using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SW.Bus.RabbitMqExtensions;

namespace SW.Bus;

/// <summary>
/// Lock-free ring-buffer implementation of <see cref="IOperationalEventStore"/> and
/// <see cref="IOperationalEventBatchSink"/>. Events are written without blocking the
/// consumer thread. When the buffer is full, oldest events are overwritten automatically.
/// </summary>
internal sealed class InMemoryOperationalEventStore : IOperationalEventBatchSink, IOperationalEventStore
{
    private readonly IOperationalEvent[] _buffer;
    private readonly int _capacity;
    private long _writeIndex;    // monotonically increasing, never wraps
    private long _totalReceived; // count of all events ever received

    public InMemoryOperationalEventStore(BusOptions busOptions)
    {
        _capacity = Math.Max(1000, busOptions.OperationalEventsStoreCapacity);
        _buffer = new IOperationalEvent[_capacity];
    }

    /// <inheritdoc />
    public long TotalReceived => Interlocked.Read(ref _totalReceived);

    /// <inheritdoc />
    public Task PublishBatch(IReadOnlyList<IOperationalEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (var evt in events)
        {
            if (evt == null) continue;
            var slot = Interlocked.Increment(ref _writeIndex) - 1;
            Volatile.Write(ref _buffer[slot % _capacity], evt);
            Interlocked.Increment(ref _totalReceived);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<IOperationalEvent> GetRecent(OperationalEventFilter? filter = null)
    {
        var writeIdx = Interlocked.Read(ref _writeIndex);
        var limit    = filter?.Limit ?? 200;
        var count    = (int)Math.Min(writeIdx, _capacity);
        var results  = new List<IOperationalEvent>(Math.Min(limit, count));

        // Iterate newest → oldest
        for (var i = writeIdx - 1; i >= Math.Max(0, writeIdx - count); i--)
        {
            var evt = Volatile.Read(ref _buffer[i % _capacity]);
            if (evt == null) continue;
            if (Matches(evt, filter))
                results.Add(evt);
            if (results.Count >= limit)
                break;
        }

        return results;
    }

    private static bool Matches(IOperationalEvent evt, OperationalEventFilter? filter)
    {
        if (filter == null) return true;
        if (evt is not OperationalEventBase b) return true;

        if (filter.ApplicationName != null && !ContainsCI(b.ApplicationName, filter.ApplicationName)) return false;
        if (filter.ConsumerName    != null && !ContainsCI(b.ConsumerName,    filter.ConsumerName))    return false;
        if (filter.MessageType     != null && !ContainsCI(b.MessageType,     filter.MessageType))     return false;
        if (filter.CorrelationId   != null && !ContainsCI(b.CorrelationId,   filter.CorrelationId))   return false;
        if (filter.TraceId         != null && !ContainsCI(b.TraceId,         filter.TraceId))         return false;
        if (filter.QueueName       != null && !ContainsCI(b.QueueName,       filter.QueueName))       return false;
        if (filter.EventName       != null && !EqualsCI(b.EventName,         filter.EventName))       return false;

        if (filter.From.HasValue && b.TimestampUtc < filter.From.Value) return false;
        if (filter.To.HasValue   && b.TimestampUtc > filter.To.Value)   return false;

        return true;
    }

    /// <summary>Returns false when <paramref name="value"/> is null; otherwise case-insensitive substring match.</summary>
    private static bool ContainsCI(string? value, string token)
        => value != null && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns false when <paramref name="value"/> is null; otherwise case-insensitive equality.</summary>
    private static bool EqualsCI(string? value, string token)
        => value != null && value.Equals(token, StringComparison.OrdinalIgnoreCase);
}

