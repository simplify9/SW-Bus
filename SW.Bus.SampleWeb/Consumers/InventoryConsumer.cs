using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 4 — Multi-type consumer with per-type prefetch via IConsumeExtended.
/// Handles two message types with different throughput needs:
///   • InventoryUpdatedMessage  — high-throughput warehouse writes (prefetch 16, 20–100 ms)
///   • LowStockAlertMessage     — lower-volume priority alerts  (prefetch 2,  80–300 ms)
/// Demonstrates how a single consumer service can own an entire domain
/// with tailored QoS per message type.
/// </summary>
public class InventoryConsumer : IConsumeExtended
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<InventoryConsumer> logger;

    public InventoryConsumer(ILogger<InventoryConsumer> logger)
        => this.logger = logger;

    public Task<IEnumerable<string>> GetMessageTypeNames() =>
        Task.FromResult<IEnumerable<string>>(
            [nameof(InventoryUpdatedMessage), nameof(LowStockAlertMessage)]);

    public Task<IDictionary<string, ConsumerOptions>> GetMessageTypeNamesWithOptions() =>
        Task.FromResult<IDictionary<string, ConsumerOptions>>(
            new Dictionary<string, ConsumerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(InventoryUpdatedMessage)] = new ConsumerOptions { Prefetch = 16 },
                [nameof(LowStockAlertMessage)]    = new ConsumerOptions { Prefetch = 2, Priority = 5 }
            });

    public async Task Process(string messageTypeName, string message)
    {
        if (messageTypeName.Equals(nameof(InventoryUpdatedMessage), StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(Rng.Next(20, 100));   // fast warehouse write
            logger.LogInformation("[Inventory] Stock update processed: {Payload}", message.Length > 60 ? message[..60] + "…" : message);
        }
        else if (messageTypeName.Equals(nameof(LowStockAlertMessage), StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(Rng.Next(80, 300));   // alert routing takes longer
            logger.LogWarning("[Inventory] LOW STOCK ALERT: {Payload}", message.Length > 100 ? message[..100] + "…" : message);
        }
    }
}

