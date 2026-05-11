using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 1 — Happy path, fast consumer (10–80 ms).
/// Simulates lightweight order validation + database write.
/// Always succeeds. High throughput, prefetch 8.
/// </summary>
public class OrderConsumer : IConsume<OrderCreatedMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<OrderConsumer> logger;
    private readonly RequestContext requestContext;

    public OrderConsumer(ILogger<OrderConsumer> logger, RequestContext requestContext)
    {
        this.logger = logger;
        this.requestContext = requestContext;
    }

    public async Task Process(OrderCreatedMessage message)
    {
        await Task.Delay(Rng.Next(10, 80));  // fast: 10–80 ms

        logger.LogInformation(
            "Order {OrderId} processed for customer {CustomerId}. " +
            "Items: {ItemCount}, Total: {Total:C}, CorrelationId: {CorrelationId}",
            message.OrderId, message.CustomerId,
            message.ItemCount, message.TotalAmount,
            requestContext.CorrelationId ?? "none");
    }
}

