using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 10 — Ultra-high-priority consumer using IConsumeExtended (5–30 ms).
/// P1/P2 operational alerts must be actioned immediately: consumer priority 10,
/// prefetch 1 (to ensure each alert gets full attention).
/// Virtually never fails. Demonstrates a dedicated "golden path" queue
/// that stays clear even when other queues back up.
/// </summary>
public class UrgentAlertConsumer : IConsumeExtended<UrgentAlertMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<UrgentAlertConsumer> logger;

    public UrgentAlertConsumer(ILogger<UrgentAlertConsumer> logger)
        => this.logger = logger;

    public Task<ConsumerOptions> GetConsumerOptions() =>
        Task.FromResult(new ConsumerOptions { Prefetch = 1, Priority = 10 });

    public async Task Process(UrgentAlertMessage message)
    {
        await Task.Delay(Rng.Next(5, 30));
        logger.LogCritical(
            "🚨 ALERT [{Severity}] {Code} — {System}: {Description} (AlertId: {AlertId})",
            message.Severity, message.AlertCode, message.AffectedSystem,
            message.Description, message.AlertId);
    }
}

