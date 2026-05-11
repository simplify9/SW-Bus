using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb;

/// <summary>
/// Background service that continuously publishes sample messages to all registered consumers.
/// This keeps the dashboard populated with live data without requiring manual HTTP requests.
///
/// Publish rates per message type (approximate, at full throttle):
///   • OrderCreated           — every 0.5–1.5 s  (moderate, main business flow)
///   • PaymentProcessed       — every 0.8–2 s    (slightly lower than orders)
///   • PaymentFailed          — every 1.5–3 s    (infrequent, but generates retries)
///   • InventoryUpdated       — every 0.2–0.5 s  (high-frequency warehouse events)
///   • LowStockAlert          — every 3–6 s      (less frequent alert)
///   • EmailNotification      — every 0.3–0.8 s  (high volume transactional email)
///   • SmsNotification        — every 0.8–2 s    (lower volume)
///   • PushNotification       — every 0.2–0.6 s  (high volume, very fast consumer)
///   • DailyReport            — every 8–15 s     (slow consumer, low volume)
///   • DataSync               — every 0.5–1.5 s  (variable latency)
///   • UrgentAlert            — every 10–20 s    (rare; high priority)
///   • OrderCancelled         — every 2–5 s      (always fails → dead-letter demo)
///   • OrderShipped           — every 1–3 s
///   • CarDto (legacy)        — every 2–5 s
///   • PersonDto (50 % fail)  — every 1–2.5 s
/// </summary>
public class SamplePublisherBackgroundService : BackgroundService
{
    private static readonly Random Rng = Random.Shared;
    private readonly IPublish publish;
    private readonly ILogger<SamplePublisherBackgroundService> logger;

    // Sample data pools
    private static readonly string[] CustomerIds   = ["C-001", "C-002", "C-003", "C-004", "C-005"];
    private static readonly string[] ProductIds    = ["P-100", "P-200", "P-300", "P-400"];
    private static readonly string[] ProductNames  = ["Widget A", "Gadget B", "Component C", "Tool D"];
    private static readonly string[] Warehouses    = ["WH-EU-1", "WH-US-1", "WH-AU-1"];
    private static readonly string[] Providers     = ["Stripe", "PayPal", "Braintree"];
    private static readonly string[] FailureCodes  = ["insufficient_funds", "card_expired", "do_not_honor"];
    private static readonly string[] ReportTypes   = ["Sales", "Inventory", "UserActivity", "Revenue"];
    private static readonly string[] EntityTypes   = ["Customer", "Order", "Product", "Invoice"];
    private static readonly string[] Sources       = ["CRM", "ERP", "POS", "WebApp"];
    private static readonly string[] Operations    = ["Create", "Update", "Delete"];
    private static readonly string[] AlertCodes    = ["DB_CONN_FAIL", "API_TIMEOUT", "DISK_FULL", "MEM_HIGH"];
    private static readonly string[] Systems       = ["OrderService", "PaymentGateway", "InventoryDB", "CDN"];
    private static readonly string[] Carriers      = ["FedEx", "UPS", "DHL", "USPS"];
    private static readonly string[] CarModels     = ["Tesla Model 3", "BMW X5", "Audi A4", "Toyota Camry"];
    private static readonly string[] PersonNames   = ["Alice", "Bob", "Charlie", "Diana", "Eve"];
    private static readonly string[] Cancellation  = ["Customer request", "Out of stock", "Duplicate order", "Fraud detected"];
    private static readonly string[] Templates     = ["order-confirm", "shipping-notify", "receipt", "promo"];

    public SamplePublisherBackgroundService(IPublish publish, ILogger<SamplePublisherBackgroundService> logger)
    {
        this.publish = publish;
        this.logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the application a moment to fully start before flooding queues
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        logger.LogInformation("SamplePublisher started — publishing to all sample consumers.");

        // Run all publishers concurrently, each at its own cadence
        await Task.WhenAll(
            PublishLoopAsync("OrderCreated",     PublishOrderCreated,     500,  1500, stoppingToken),
            PublishLoopAsync("PaymentProcessed", PublishPaymentProcessed, 800,  2000, stoppingToken),
            PublishLoopAsync("PaymentFailed",    PublishPaymentFailed,    1500, 3000, stoppingToken),
            PublishLoopAsync("InventoryUpdated", PublishInventoryUpdated, 200,  500,  stoppingToken),
            PublishLoopAsync("LowStockAlert",    PublishLowStockAlert,    3000, 6000, stoppingToken),
            PublishLoopAsync("Email",            PublishEmail,            300,  800,  stoppingToken),
            PublishLoopAsync("Sms",              PublishSms,              800,  2000, stoppingToken),
            PublishLoopAsync("Push",             PublishPush,             200,  600,  stoppingToken),
            PublishLoopAsync("DailyReport",      PublishDailyReport,      8000, 15000,stoppingToken),
            PublishLoopAsync("DataSync",         PublishDataSync,         500,  1500, stoppingToken),
            PublishLoopAsync("UrgentAlert",      PublishUrgentAlert,      10000,20000,stoppingToken),
            PublishLoopAsync("OrderCancelled",   PublishOrderCancelled,   2000, 5000, stoppingToken),
            PublishLoopAsync("OrderShipped",     PublishOrderShipped,     1000, 3000, stoppingToken),
            PublishLoopAsync("CarDto",           PublishCarDto,           2000, 5000, stoppingToken),
            PublishLoopAsync("PersonDto",        PublishPersonDto,        1000, 2500, stoppingToken));
    }

    private async Task PublishLoopAsync(string name, Func<Task> publisher, int minMs, int maxMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await publisher();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Telemetry failure must never crash the publisher loop
                logger.LogWarning(ex, "SamplePublisher loop [{Name}] encountered a publish error.", name);
            }

            try
            {
                await Task.Delay(Rng.Next(minMs, maxMs), ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    // ── Individual message publishers ─────────────────────────────────────────

    private Task PublishOrderCreated() => publish.Publish(new OrderCreatedMessage(
        $"ORD-{Guid.NewGuid():N}"[..16],
        CustomerIds[Rng.Next(CustomerIds.Length)],
        Math.Round((decimal)(Rng.NextDouble() * 500 + 9.99), 2),
        Rng.Next(1, 10)));

    private Task PublishPaymentProcessed() => publish.Publish(new PaymentProcessedMessage(
        $"PAY-{Guid.NewGuid():N}"[..16],
        $"ORD-{Rng.Next(1000, 9999)}",
        Math.Round((decimal)(Rng.NextDouble() * 500 + 9.99), 2),
        Rng.NextDouble() < 0.15 ? "EUR" : "USD",
        Providers[Rng.Next(Providers.Length)],
        Rng.NextDouble() < 0.05)); // 5 % are refunds

    private Task PublishPaymentFailed() => publish.Publish(new PaymentFailedMessage(
        $"PAY-{Guid.NewGuid():N}"[..16],
        $"ORD-{Rng.Next(1000, 9999)}",
        Math.Round((decimal)(Rng.NextDouble() * 300 + 9.99), 2),
        FailureCodes[Rng.Next(FailureCodes.Length)],
        "Transaction declined by issuing bank"));

    private Task PublishInventoryUpdated()
    {
        var idx = Rng.Next(ProductIds.Length);
        var prev = Rng.Next(50, 500);
        var delta = Rng.Next(-30, 100);
        return publish.Publish(new InventoryUpdatedMessage(
            ProductIds[idx], ProductNames[idx], prev, Math.Max(0, prev + delta),
            Warehouses[Rng.Next(Warehouses.Length)], "WMS-AutoJob"));
    }

    private Task PublishLowStockAlert()
    {
        var idx = Rng.Next(ProductIds.Length);
        return publish.Publish(new LowStockAlertMessage(
            ProductIds[idx], ProductNames[idx],
            Rng.Next(1, 15), 20,
            Warehouses[Rng.Next(Warehouses.Length)]));
    }

    private Task PublishEmail() => publish.Publish(new EmailNotificationMessage(
        $"user{Rng.Next(100, 999)}@example.com",
        "Your order update",
        Templates[Rng.Next(Templates.Length)],
        Rng.NextDouble() < 0.3 ? "ar" : "en",
        Rng.NextDouble() < 0.1 ? 1 : 0));

    private Task PublishSms() => publish.Publish(new SmsNotificationMessage(
        $"+1555{Rng.Next(1000000, 9999999)}",
        "Your order has been confirmed. Track at example.com/track",
        "SW-OPS"));

    private Task PublishPush() => publish.Publish(new PushNotificationMessage(
        Guid.NewGuid().ToString("N"),
        "Order update",
        "Your order is on its way!",
        $"app://orders/{Rng.Next(1000, 9999)}"));

    private Task PublishDailyReport() => publish.Publish(new DailyReportMessage(
        Guid.NewGuid().ToString("N")[..8],
        DateTime.UtcNow.Date.AddDays(-Rng.Next(0, 7)),
        ReportTypes[Rng.Next(ReportTypes.Length)],
        "scheduler@system"));

    private Task PublishDataSync() => publish.Publish(new DataSyncMessage(
        Guid.NewGuid().ToString("N")[..8],
        EntityTypes[Rng.Next(EntityTypes.Length)],
        $"ID-{Rng.Next(1000, 9999)}",
        Sources[Rng.Next(Sources.Length)],
        Operations[Rng.Next(Operations.Length)]));

    private Task PublishUrgentAlert() => publish.Publish(new UrgentAlertMessage(
        Guid.NewGuid().ToString("N")[..8],
        AlertCodes[Rng.Next(AlertCodes.Length)],
        Rng.NextDouble() < 0.2 ? "P1" : "P2",
        "Automated monitoring detected an anomaly requiring immediate attention.",
        Systems[Rng.Next(Systems.Length)]));

    private Task PublishOrderCancelled() => publish.Publish(new OrderCancelledMessage(
        $"ORD-{Rng.Next(1000, 9999)}",
        CustomerIds[Rng.Next(CustomerIds.Length)],
        Cancellation[Rng.Next(Cancellation.Length)],
        Rng.NextDouble() < 0.7));

    private Task PublishOrderShipped() => publish.Publish(new OrderShippedMessage(
        $"ORD-{Rng.Next(1000, 9999)}",
        $"1Z{Rng.Next(100000, 999999)}",
        Carriers[Rng.Next(Carriers.Length)],
        DateTime.UtcNow.AddDays(Rng.Next(2, 7))));

    private Task PublishCarDto() => publish.Publish(new Models.CarDto
    {
        Model = CarModels[Rng.Next(CarModels.Length)]
    });

    private Task PublishPersonDto() => publish.Publish(new Models.PersonDto
    {
        Name = PersonNames[Rng.Next(PersonNames.Length)]
    });
}

