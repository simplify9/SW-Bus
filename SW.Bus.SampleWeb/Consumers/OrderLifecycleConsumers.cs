using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 11 — Order cancellation consumer with 100 % failure rate.
/// Mimics a third-party cancellation API that is currently DOWN.
/// Every message exhausts its retry budget and lands in dead-letter.
/// Useful for demonstrating the Dead-Letter Inspection page to support engineers.
/// </summary>
public class OrderCancellationConsumer : IConsume<OrderCancelledMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<OrderCancellationConsumer> logger;

    public OrderCancellationConsumer(ILogger<OrderCancellationConsumer> logger)
        => this.logger = logger;

    public async Task Process(OrderCancelledMessage message)
    {
        await Task.Delay(Rng.Next(100, 400));

        // Simulates an external cancellation service that is currently unavailable
        throw new InvalidOperationException(
            $"Cancellation service offline. Cannot cancel order {message.OrderId}. Reason: {message.Reason}");
    }

    public Task OnFail(Exception ex)
    {
        logger.LogError(ex, "Order cancellation failed — will exhaust retries and move to dead-letter.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Scenario 12 — Order shipped consumer (30–100 ms), always succeeds.
/// Simulates updating order tracking and notifying the warehouse system.
/// Medium throughput, prefetch 6 (configured via AddQueueOption). 
/// </summary>
public class OrderShippedConsumer : IConsume<OrderShippedMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<OrderShippedConsumer> logger;

    public OrderShippedConsumer(ILogger<OrderShippedConsumer> logger)
        => this.logger = logger;

    public async Task Process(OrderShippedMessage message)
    {
        await Task.Delay(Rng.Next(30, 100));
        logger.LogInformation(
            "Order {OrderId} shipped via {Carrier} — tracking {Tracking}, ETA {Eta:yyyy-MM-dd}",
            message.OrderId, message.Carrier, message.TrackingNumber, message.EstimatedDelivery);
    }
}

