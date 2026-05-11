using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 9 — Unpredictable latency consumer (20–1500 ms, bimodal distribution).
/// Simulates syncing with an external CRM/ERP whose response time is highly variable:
/// most calls resolve quickly (50–150 ms), but some hit slow code paths (800–1500 ms).
/// Occasionally fails (15 %) due to external system timeouts.
/// Demonstrates the "long-tail latency" problem visible in P95/P99 histograms.
/// </summary>
public class DataSyncConsumer : IConsume<DataSyncMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<DataSyncConsumer> logger;

    public DataSyncConsumer(ILogger<DataSyncConsumer> logger)
        => this.logger = logger;

    public async Task Process(DataSyncMessage message)
    {
        // Bimodal latency: 80 % fast, 20 % slow
        int delay = Rng.NextDouble() < 0.80
            ? Rng.Next(20, 150)    // fast path
            : Rng.Next(800, 1500); // slow path (long-tail)

        await Task.Delay(delay);

        // 15 % transient external service failure
        if (Rng.NextDouble() < 0.15)
            throw new HttpRequestException(
                $"External sync API timeout for {message.Source}/{message.EntityType}/{message.EntityId}");

        logger.LogInformation(
            "Synced {Operation} {EntityType}/{EntityId} from {Source} in ~{Delay}ms",
            message.Operation, message.EntityType, message.EntityId, message.Source, delay);
    }

    public Task OnFail(Exception ex)
    {
        logger.LogWarning(ex, "DataSyncConsumer: sync operation queued for retry.");
        return Task.CompletedTask;
    }
}

