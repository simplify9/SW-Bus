using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.Bus.RabbitMqExtensions;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 2 — High-priority typed consumer using IConsumeExtended (50–200 ms).
/// Payments run at consumer priority 8 with prefetch 4.
/// Simulates calling an external payment settlement API.
/// Always succeeds so the retry queue stays clean for this queue.
/// </summary>
public class PaymentProcessedConsumer : IConsumeExtended<PaymentProcessedMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<PaymentProcessedConsumer> logger;

    public PaymentProcessedConsumer(ILogger<PaymentProcessedConsumer> logger)
        => this.logger = logger;

    public Task<ConsumerOptions> GetConsumerOptions() =>
        Task.FromResult(new ConsumerOptions { Prefetch = 4, Priority = 8 });

    public async Task Process(PaymentProcessedMessage message)
    {
        await Task.Delay(Rng.Next(50, 200));  // medium: 50–200 ms

        logger.LogInformation(
            "{Action} {PaymentId} ({Provider}) — Order {OrderId}: {Amount:C} {Currency}",
            message.IsRefund ? "Refund" : "Payment",
            message.PaymentId, message.Provider, message.OrderId,
            message.Amount, message.Currency);
    }
}

