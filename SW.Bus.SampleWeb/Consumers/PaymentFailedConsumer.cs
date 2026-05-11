using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 3 — Intermittently failing consumer with retries and dead-lettering.
/// Simulates an unreliable downstream fraud-check API.
/// ~40 % of messages fail on first attempt → land in retry queue.
/// After configured retries exhausted (~5 attempts) → dead-letter queue.
/// This intentionally populates the retry and dead-letter queues so the
/// dashboard has interesting data to display.
/// </summary>
public class PaymentFailedConsumer : IConsume<PaymentFailedMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<PaymentFailedConsumer> logger;

    public PaymentFailedConsumer(ILogger<PaymentFailedConsumer> logger)
        => this.logger = logger;

    public async Task Process(PaymentFailedMessage message)
    {
        await Task.Delay(Rng.Next(30, 120));

        // 40 % chance of transient failure — generates retry traffic
        if (Rng.NextDouble() < 0.40)
            throw new InvalidOperationException(
                $"Fraud-check service unavailable for payment {message.PaymentId} " +
                $"(code {message.FailureCode}). Will retry.");

        logger.LogWarning(
            "Payment failure logged for order {OrderId}: [{Code}] {Reason}",
            message.OrderId, message.FailureCode, message.FailureReason);
    }

    public Task OnFail(Exception ex)
    {
        logger.LogError(ex, "PaymentFailedConsumer OnFail — message sent to retry/DL queue.");
        return Task.CompletedTask;
    }
}

