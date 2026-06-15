using System;
using System.Collections.Concurrent;

namespace SW.Bus.IntegrationTests;

/// <summary>Singleton that records when each delayed message is received by the consumer.</summary>
public class MessageSink
{
    public ConcurrentDictionary<string, DateTime> ReceivedAtUtc { get; } = new();

    public void Record(string id) => ReceivedAtUtc.TryAdd(id, DateTime.UtcNow);
}
