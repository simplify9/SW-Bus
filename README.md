# SimplyWorks.Bus

[![Build and Publish NuGet Package](https://github.com/simplify9/SW-Bus/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/simplify9/SW-Bus/actions/workflows/nuget-publish.yml)
[![NuGet](https://img.shields.io/nuget/v/SimplyWorks.Bus.svg)](https://www.nuget.org/packages/SimplyWorks.Bus)
[![NuGet](https://img.shields.io/nuget/v/SimplyWorks.Bus.RabbitMqExtensions.svg?label=RabbitMqExtensions)](https://www.nuget.org/packages/SimplyWorks.Bus.RabbitMqExtensions)
[![NuGet](https://img.shields.io/nuget/v/SimplyWorks.Bus.RabbitMqViewer.svg?label=RabbitMqViewer)](https://www.nuget.org/packages/SimplyWorks.Bus.RabbitMqViewer)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A lightweight .NET 8 message bus library built on top of RabbitMQ, designed for event-driven microservice architectures in ASP.NET Core.

The library ships as three complementary NuGet packages:

| Package | Purpose |
|---|---|
| `SimplyWorks.Bus` | Core runtime — publishing, consuming, retries, dead-letter, tracing |
| `SimplyWorks.Bus.RabbitMqExtensions` | Public contracts, consumer interfaces, dashboard data models |
| `SimplyWorks.Bus.RabbitMqViewer` | Built-in HTMX operations dashboard (dark + light mode, zero JS framework) |

---

## Table of Contents

1. [Features](#features)
2. [Installation](#installation)
3. [Quick Start](#quick-start)
4. [Publishing Messages](#publishing-messages)
5. [Consuming Messages](#consuming-messages)
6. [Broadcasting & Listeners](#broadcasting--listeners)
7. [Per-Queue Configuration](#per-queue-configuration)
8. [Extended Consumer Options (IConsumeExtended)](#extended-consumer-options-iconsumeextended)
9. [Monitoring — IConsumerReader](#monitoring--iconsumerreader)
10. [Error Queue Inspection — IErrorQueueReader](#error-queue-inspection--ierrorqueuereader)
11. [Operational Events Pipeline](#operational-events-pipeline)
12. [Dashboard Data Service — IBusDashboardDataService](#dashboard-data-service--ibusdashboarddataservice)
13. [Custom Alert Thresholds — IAlertEvaluator](#custom-alert-thresholds--ialertevaluator)
14. [Building a Custom Dashboard](#building-a-custom-dashboard)
15. [Operations Viewer Dashboard](#operations-viewer-dashboard)
16. [Full BusOptions Reference](#full-busoptions-reference)
17. [Architecture](#architecture)
18. [Testing](#testing)
19. [Dependencies](#dependencies)

## Features

- 🚌 **Simple Message Publishing** — typed and string-based `IPublish`
- 📡 **Broadcasting** — fan-out to all application instances via `IBroadcast` / `IListen<T>`
- 🔄 **Automatic Retries** — configurable per-queue retry counts and delay with dead-letter routing
- 🎯 **Typed Consumers** — `IConsume<T>` with strongly-typed message deserialization
- ⚙️ **Extended Consumer Options** — per-consumer prefetch and priority via `IConsumeExtended`
- 🔐 **JWT Propagation** — user context forwarded through message headers across services
- 📊 **Queue Monitoring** — live queue depth, rates, and consumer counts via `IConsumerReader`
- 🔍 **Error Queue Inspection** — peek retry and dead-letter queues via `IErrorQueueReader`
- 📈 **Operational Events** — structured lifecycle events with `ActivitySource` tracing and `Meter` metrics
- 🖥️ **Built-in Dashboard** — HTMX admin UI with dark/light mode toggle, form-based login, CSS refresh animations, and critical status tooltips via `SimplyWorks.Bus.RabbitMqViewer`
- 🏗️ **Custom Dashboard API** — all data exposed through `IBusDashboardDataService` in `SimplyWorks.Bus.RabbitMqExtensions` — build your own UI without the viewer package
- 🧪 **Testing Support** — mock publisher for unit testing

---

## Installation

```bash
# Core — always required
dotnet add package SimplyWorks.Bus

# Public contracts + monitoring interfaces (no RabbitMQ.Client dependency)
dotnet add package SimplyWorks.Bus.RabbitMqExtensions

# Optional: built-in operations dashboard
dotnet add package SimplyWorks.Bus.RabbitMqViewer
```

---

## Quick Start

### 1. Connection string

Add RabbitMQ connection string to your `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "RabbitMQ": "amqp://guest:guest@localhost:5672/"
  }
}
```

### 2. Service registration

In your `Startup.cs` or `Program.cs`:

```csharp
services.AddBus(config =>
{
    config.ApplicationName = "MyApp";
    // Optional JWT configuration
    config.Token.Key      = Configuration["Token:Key"];
    config.Token.Issuer   = Configuration["Token:Issuer"];
    config.Token.Audience = Configuration["Token:Audience"];
});

services.AddBusPublish();  // registers IPublish, IBroadcast
services.AddBusConsume();  // registers IHostedService consumer + scans calling assembly
```

---

## Publishing Messages

```csharp
public class OrderController : ControllerBase
{
    private readonly IPublish _publish;
    private readonly IBroadcast _broadcast;

    public OrderController(IPublish publish, IBroadcast broadcast)
    {
        _publish   = publish;
        _broadcast = broadcast;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderRequest req)
    {
        // Routed to consumers subscribed to OrderCreated
        await _publish.Publish(new OrderCreated { OrderId = Guid.NewGuid() });

        // Fan-out to all connected application instances
        await _broadcast.Broadcast(new OrderNotification { Message = "New order" });

        return Ok();
    }
}
```

String-based publish (useful for dynamic routing):

```csharp
await _publish.Publish("OrderCreated", jsonPayload);
await _publish.Publish("OrderCreated", payloadBytes);
```

---

## Consuming Messages

### Typed consumer

```csharp
public class OrderCreatedConsumer : IConsume<OrderCreated>
{
    public async Task Process(OrderCreated message)
    {
        // Auto-acked on success, rejected/retried on exception
    }
}
```

### Multi-message string consumer

```csharp
public class GenericConsumer : IConsume
{
    public Task<IEnumerable<string>> GetMessageTypeNames()
        => Task.FromResult<IEnumerable<string>>(new[] { "OrderCreated", "OrderCancelled" });

    public async Task Process(string typeName, string message) { ... }
}
```

### Optional failure handler

```csharp
public class OrderCreatedConsumer : IConsume<OrderCreated>
{
    public async Task Process(OrderCreated message) { ... }

    // Called on every failure (including retries) — optional
    public async Task OnFail(Exception ex) { ... }
}
```

### Accessing request context

```csharp
public class SecureConsumer : IConsume<SecureMessage>
{
    private readonly RequestContext _ctx;
    public SecureConsumer(RequestContext ctx) => _ctx = ctx;

    public async Task Process(SecureMessage msg)
    {
        var user          = _ctx.User;
        var correlationId = _ctx.CorrelationId;
        var remaining     = _ctx.GetValue("RemainingRetries");
    }
}
```

### Consumer registration

```csharp
services.AddBusConsume();                                        // scan calling assembly
services.AddBusConsume(typeof(OrderCreatedConsumer).Assembly);  // specific assembly
```

---

## Broadcasting & Listeners

Broadcasts are fan-out messages delivered to **every running instance** simultaneously.

```csharp
// Send
await _broadcast.Broadcast(new PricingUpdated { Version = 42 });

// Trigger live consumer refresh across all instances
await _broadcast.RefreshConsumers();
```

```csharp
// Receive
public class PricingUpdatedListener : IListen<PricingUpdated>
{
    public async Task Process(PricingUpdated message) { ... }
    public async Task OnFail(Exception ex) { ... }  // optional
}

services.AddBusListen();                                            // scan calling assembly
services.AddBusListen(typeof(PricingUpdatedListener).Assembly);    // specific assembly
```

---

## Per-Queue Configuration

Fine-tune individual queues via `BusOptions.AddQueueOption`. The key is `"{ConsumerClass}.{MessageType}"` (case-insensitive).

```csharp
services.AddBus(config =>
{
    config.AddQueueOption(
        "OrderCreatedConsumer.OrderCreated",
        prefetch: 10,
        retryCount: 3,
        retryAfterSeconds: 30);

    // Enable priority queue (0–10 range)
    config.AddQueueOption(
        "PaymentConsumer.PaymentProcessed",
        priority: 10);
});
```

---

## Extended Consumer Options (`IConsumeExtended`)

For consumers that need per-instance prefetch or priority without touching global config:

```csharp
// Typed consumer with runtime options
public class PriorityOrderConsumer : IConsumeExtended<OrderCreated>
{
    public async Task Process(OrderCreated message) { ... }

    public Task<ConsumerOptions> GetConsumerOptions() =>
        Task.FromResult(new ConsumerOptions { Prefetch = 8, Priority = 5 });
}

// Multi-message consumer with per-type options
public class MultiConsumer : IConsumeExtended
{
    public Task<IEnumerable<string>> GetMessageTypeNames() =>
        Task.FromResult<IEnumerable<string>>(new[] { "TypeA", "TypeB" });

    public Task<IDictionary<string, ConsumerOptions>> GetMessageTypeNamesWithOptions() =>
        Task.FromResult<IDictionary<string, ConsumerOptions>>(
            new Dictionary<string, ConsumerOptions>
            {
                ["TypeA"] = new ConsumerOptions { Prefetch = 4 },
                ["TypeB"] = new ConsumerOptions { Prefetch = 16, Priority = 3 }
            });

    public async Task Process(string typeName, string message) { ... }
}
```

`IConsumeExtended` consumers are **hot-reloadable** — call `IBroadcast.RefreshConsumers()` to apply updated options across all running instances without restart.

---

## Monitoring — `IConsumerReader`

Registered automatically by `AddBus()`. Returns live queue statistics from the RabbitMQ Management API with configurable caching.

```csharp
// All consumers
var all = await _reader.GetAllConsumersCount();

// Typed consumer + message type
var order = await _reader.GetConsumerCount<OrderConsumer, OrderCreated>();

// Multi-message consumer by message name
var generic = await _reader.GetConsumerCount<GenericConsumer>("OrderCreated");

// All queues for a consumer class
var byClass = await _reader.GetConsumerCount<OrderConsumer>();
```

**`ConsumerCount` fields:**

| Field | Description |
|---|---|
| `Name` | Consumer class name |
| `MessageName` | Message type name |
| `TotalNodes` | Active consumer instances |
| `ProcessingCount` | Messages in-flight (unacknowledged) |
| `QueueCount` | Messages ready in main queue |
| `RetryCount` | Messages in retry queue |
| `FailedCount` | Messages in dead-letter queue |
| `Priority` | Consumer priority level |
| `Prefetch` | QoS prefetch count |
| `IncomingRate` | Publish rate (msg/s) |
| `ProcessingRate` | Deliver rate to consumers (msg/s) |
| `AckRate` | Acknowledge rate (msg/s) |

```csharp
services.AddBus(config =>
{
    config.MonitoringCacheSeconds = 10;   // cache duration 3–60 s, default 5
    config.ManagementUrl      = "http://localhost:15672";  // defaults from AMQP URI
    config.ManagementUsername = "guest";
    config.ManagementPassword = "guest";
    config.VirtualHost        = "/";
});
```

---

## Error Queue Inspection — `IErrorQueueReader`

Peek at messages in retry or dead-letter queues without removing them.

```csharp
// Typed consumer
var failed = await _errorReader.Peek<OrderConsumer, OrderCreated>(ErrorQueueType.Bad, count: 20);

// Multi-message consumer
var retrying = await _errorReader.Peek<GenericConsumer>("OrderCreated", ErrorQueueType.Retry);

// Raw queue name
var raw = await _errorReader.PeekByQueueName("v3.production.orderconsumer.ordercreated.bad");
```

**`ErrorMessage` properties:**

| Property | Description |
|---|---|
| `RawBody` | Original JSON payload |
| `Exchange` | Exchange where message was published |
| `RoutingKey` | Routing key used at publish |
| `Properties` | All AMQP properties |
| `Headers` | Extracted AMQP headers |
| `CorrelationId` | Correlation ID if set |
| `ExceptionHistory` | All recorded exceptions oldest-first |
| `LastException` | Most recent exception string |

---

## Operational Events Pipeline

SW.Bus emits strongly-typed lifecycle events that flow through a lock-free buffered pipeline. The goal is **observability and diagnostics**, not logging.

### Events emitted

| Event | Trigger |
|---|---|
| `PublishStarted` | Message about to be published |
| `PublishCompleted` | Publish succeeded (includes duration ms, payload bytes) |
| `PublishFailed` | Publish threw an exception |
| `MessageProcessingStarted` | Consumer received and started processing |
| `MessageProcessingCompleted` | Processed successfully (includes duration ms) |
| `MessageProcessingFailed` | Consumer threw an exception (type, message, stack trace) |
| `MessageRetryScheduled` | Message rejected back to retry queue |
| `MessageMovedToDeadLetter` | Message exhausted retries |
| `ConsumerConnected` | Consumer channel attached to a queue |
| `ConsumerDisconnected` | Consumer channel shut down |
| `QueueBackpressureDetected` | Queue depth exceeded `QueueBackpressureThreshold` |

Every event carries: `TimestampUtc`, `SchemaVersion`, `EventName`, `MachineName`, `Environment`, `ApplicationName`, `Exchange`, `QueueName`, `ConsumerName`, `MessageType`, `MessageId`, `CorrelationId`, `CausationId`, `TraceId`, `SpanId`, `DeliveryTag`.

### Pipeline architecture

```
Consumer / Publisher hot path
       │  (non-blocking TryWrite)
       ▼
  BoundedChannel<IOperationalEvent>  ← configurable capacity, drop-oldest on full
       │
       ▼  (BackgroundService, configurable flush interval)
  OperationalEventDispatcher
       ├── InMemoryOperationalEventStore  (ring buffer — always present)
       └── [your IOperationalEventBatchSink registrations]
```

### Plugging in a custom external sink

```csharp
using SW.Bus.RabbitMqExtensions;

public class ElasticsearchSink : IOperationalEventBatchSink
{
    public async Task PublishBatch(IReadOnlyList<IOperationalEvent> events,
        CancellationToken cancellationToken = default)
    {
        await _esClient.BulkAsync(events, cancellationToken);
    }
}

// Sinks stack additively — InMemoryStore is always active alongside yours
services.AddSingleton<IOperationalEventBatchSink, ElasticsearchSink>();
```

### OpenTelemetry tracing

```csharp
services.AddOpenTelemetry()
    .WithTracing(b => b
        .AddSource("SimplyWorks.Bus")   // ActivitySource name
        .AddOtlpExporter());
```

### Prometheus / OpenTelemetry metrics (meter name: `"SimplyWorks.Bus"`)

```csharp
services.AddOpenTelemetry()
    .WithMetrics(b => b
        .AddMeter("SimplyWorks.Bus")
        .AddPrometheusExporter());
```

| Instrument | Type | Description |
|---|---|---|
| `sw_bus_publish_started_total` | Counter | Publish attempts |
| `sw_bus_publish_completed_total` | Counter | Successful publishes |
| `sw_bus_publish_failed_total` | Counter | Failed publishes |
| `sw_bus_processing_started_total` | Counter | Messages picked up |
| `sw_bus_processing_completed_total` | Counter | Messages processed successfully |
| `sw_bus_processing_failed_total` | Counter | Messages that threw exceptions |
| `sw_bus_retry_scheduled_total` | Counter | Messages sent to retry queue |
| `sw_bus_dead_letter_total` | Counter | Messages moved to dead-letter |
| `sw_bus_operational_event_dropped_total` | Counter | Events dropped (buffer full) |
| `sw_bus_processing_latency_ms` | Histogram | Consumer processing time |
| `sw_bus_publish_latency_ms` | Histogram | Publish time |

### Pipeline configuration

```csharp
services.AddBus(config =>
{
    config.OperationalEventsEnabled         = true;   // default
    config.OperationalEventsBufferCapacity  = 8192;   // channel buffer before drop
    config.OperationalEventsBatchSize       = 256;    // events per flush batch
    config.OperationalEventsFlushIntervalMs = 1000;   // ms between flushes
    config.OperationalEventsDropOldest      = true;    // drop strategy when full
    config.OperationalEventsSchemaVersion   = "1.0";  // stamped on every event
    config.OperationalEventsStoreCapacity   = 10000;  // in-memory ring buffer size
});
```

---

## Dashboard Data Service — `IBusDashboardDataService`

All dashboard data is exposed through `IBusDashboardDataService` (defined in `SimplyWorks.Bus.RabbitMqExtensions`, implemented and registered by `SimplyWorks.Bus`). Inject it directly to build your own custom dashboard, REST API, or health probe — no dependency on `SimplyWorks.Bus.RabbitMqViewer` needed. See [Building a Custom Dashboard](#building-a-custom-dashboard) for a complete guide.

```csharp
public class OpsController : ControllerBase
{
    private readonly IBusDashboardDataService _dash;
    public OpsController(IBusDashboardDataService dash) => _dash = dash;

    [HttpGet("ops/summary")]
    public async Task<IActionResult> Summary()
        => Ok(await _dash.GetSummaryAsync());

    [HttpGet("ops/consumers")]
    public async Task<IActionResult> Consumers()
        => Ok(await _dash.GetConsumerHealthAsync());

    [HttpGet("ops/queues")]
    public async Task<IActionResult> Queues()
        => Ok(await _dash.GetQueueDetailsAsync());

    [HttpGet("ops/retries")]
    public async Task<IActionResult> Retries()
        => Ok(await _dash.GetRetryAnalysisAsync());

    [HttpGet("ops/dead-letters")]
    public async Task<IActionResult> DeadLetters()
        => Ok(await _dash.GetDeadLetterSummaryAsync());

    [HttpGet("ops/alerts")]
    public async Task<IActionResult> Alerts()
        => Ok(await _dash.GetAlertsAsync());

    [HttpGet("ops/events")]
    public IActionResult Events(
        [FromQuery] string? consumer, [FromQuery] string? messageType,
        [FromQuery] string? correlationId, [FromQuery] string? traceId,
        [FromQuery] string? eventName, [FromQuery] int limit = 200)
        => Ok(_dash.GetRecentEvents(new OperationalEventFilter(
            ConsumerName: consumer, MessageType: messageType,
            CorrelationId: correlationId, TraceId: traceId,
            EventName: eventName, Limit: limit)));
}
```

**View models:**

| Record | Key fields |
|---|---|
| `DashboardSummary` | `TotalConsumers`, `UnhealthyConsumers`, `DisconnectedConsumers`, `TotalQueueDepth`, `TotalRetryBacklog`, `TotalDeadLetterBacklog`, `TotalIncomingRate`, `TotalAckRate`, `ActiveAlerts`, `LastUpdatedUtc` |
| `ConsumerHealthView` | All `ConsumerCount` fields + `QueueName`, `IsBackpressured`, `HealthStatus` (`AlertSeverity`) |
| `QueueDetailView` | Main/retry/dead-letter queue names and depths, consumer count, rates |
| `RetryAnalysisView` | Per-consumer retry backlog ordered by size with severity |
| `DeadLetterSummaryView` | Per-consumer DL count, last exception type/message, last failure timestamp |
| `DashboardAlert` | `Severity` (`Info/Warning/Critical`), `Title`, `Detail`, `QueueName`, `ConsumerName`, `TimestampUtc` |

**`OperationalEventFilter` fields** (all optional, string fields are case-insensitive substring matches):

`ApplicationName`, `ConsumerName`, `MessageType`, `CorrelationId`, `TraceId`, `QueueName`, `EventName` (exact), `From`, `To`, `Limit` (default 200)

---

## Custom Alert Thresholds — `IAlertEvaluator`

Default thresholds are configured on `BusOptions`:

```csharp
services.AddBus(config =>
{
    config.AlertRetryWarningThreshold       = 10;   // Warning when retry backlog ≥ N
    config.AlertRetryCriticalThreshold      = 100;  // Critical when retry backlog ≥ N
    config.AlertDeadLetterCriticalThreshold = 100;  // Critical when DL count ≥ N
    config.QueueBackpressureThreshold       = 5000; // backpressure warning threshold
});
```

To apply domain-specific or SLA-based thresholds, replace the default evaluator:

```csharp
public class MyAlertEvaluator : IAlertEvaluator
{
    public IReadOnlyList<DashboardAlert> Evaluate(ConsumerHealthView[] consumers)
    {
        var alerts = new List<DashboardAlert>();
        foreach (var c in consumers)
        {
            if (c.Name == "PaymentConsumer" && c.FailedCount > 0)
                alerts.Add(new DashboardAlert(
                    AlertSeverity.Critical, "Payment Dead Letter",
                    $"{c.FailedCount} failed payments require immediate attention.",
                    c.QueueName, c.Name, DateTime.UtcNow));
        }
        return alerts;
    }
}

// Register before AddBus() — TryAddSingleton means yours wins
services.AddSingleton<IAlertEvaluator, MyAlertEvaluator>();
services.AddBus(...);
```

---

## Building a Custom Dashboard

If you do not want to use `SimplyWorks.Bus.RabbitMqViewer` — e.g. you want React, Blazor, an existing admin framework, or a JSON API for a mobile app — everything you need is in `SimplyWorks.Bus.RabbitMqExtensions`. You **never** need to install the viewer package.

### Package requirements

```bash
dotnet add package SimplyWorks.Bus                       # core runtime (always required)
dotnet add package SimplyWorks.Bus.RabbitMqExtensions    # contracts + data service
# SimplyWorks.Bus.RabbitMqViewer is NOT needed
```

### What the library provides

All interfaces below are registered automatically by `services.AddBus(...)`. Just inject what you need.

| Interface | Description |
|---|---|
| `IBusDashboardDataService` | Aggregate read layer — summary, consumer health, queues, retries, dead letters, alerts, events |
| `IConsumerReader` | Raw per-consumer queue statistics from the RabbitMQ Management API (with configurable caching) |
| `IErrorQueueReader` | Peek at messages in retry and dead-letter queues without removing them |
| `IOperationalEventStore` | Query the in-memory event ring buffer with `OperationalEventFilter` |
| `IAlertEvaluator` | Evaluate a `ConsumerHealthView[]` snapshot and return `DashboardAlert` objects |
| `IOperationalEventBatchSink` | Implement to stream event batches to external systems (Elasticsearch, ClickHouse, etc.) |

### Minimal REST API example (no viewer package)

```csharp
// Program.cs
builder.Services.AddBus(config => { config.ApplicationName = "MyApp"; /* ... */ });
builder.Services.AddBusPublish();
builder.Services.AddBusConsume();

var app = builder.Build();

app.MapGet("/ops/summary",      async (IBusDashboardDataService d) => await d.GetSummaryAsync());
app.MapGet("/ops/consumers",    async (IBusDashboardDataService d) => await d.GetConsumerHealthAsync());
app.MapGet("/ops/queues",       async (IBusDashboardDataService d) => await d.GetQueueDetailsAsync());
app.MapGet("/ops/retries",      async (IBusDashboardDataService d) => await d.GetRetryAnalysisAsync());
app.MapGet("/ops/dead-letters", async (IBusDashboardDataService d) => await d.GetDeadLetterSummaryAsync());
app.MapGet("/ops/alerts",       async (IBusDashboardDataService d) => await d.GetAlertsAsync());
app.MapGet("/ops/events",       (IBusDashboardDataService d,
                                  string? consumer, string? messageType,
                                  string? eventName, int limit = 100) =>
    d.GetRecentEvents(new OperationalEventFilter(
        ConsumerName: consumer, MessageType: messageType,
        EventName: eventName, Limit: limit)));

app.Run();
```

### Querying the operational event store directly

```csharp
public class MyOpsService
{
    private readonly IOperationalEventStore _store;
    public MyOpsService(IOperationalEventStore store) => _store = store;

    public IReadOnlyList<IOperationalEvent> GetRecentFailures(int limit = 50) =>
        _store.GetRecent(new OperationalEventFilter(
            EventName: "MessageProcessingFailed",
            Limit: limit));

    public long TotalEventsSinceStartup => _store.TotalReceived;
}
```

### Pattern matching on event records

All events are strongly typed C# records inheriting from `OperationalEventBase`. Use pattern matching to extract type-specific fields:

```csharp
foreach (var evt in _store.GetRecent())
{
    switch (evt)
    {
        case MessageProcessingFailed f:
            Console.WriteLine($"[FAIL]     {f.ConsumerName} — {f.ExceptionType}: {f.ExceptionMessage}");
            break;
        case MessageProcessingCompleted c:
            Console.WriteLine($"[OK]       {c.ConsumerName} in {c.ProcessingDurationMs:F1} ms");
            break;
        case MessageRetryScheduled r:
            Console.WriteLine($"[RETRY]    {r.ConsumerName} attempt {r.RetryCount}, {r.RemainingRetryCount} remaining");
            break;
        case MessageMovedToDeadLetter dl:
            Console.WriteLine($"[DLQ]      {dl.ConsumerName} → {dl.DeadLetterRoutingKey}");
            break;
        case QueueBackpressureDetected bp:
            Console.WriteLine($"[PRESSURE] {bp.QueueName} depth={bp.QueueDepth} threshold={bp.Threshold}");
            break;
        case ConsumerConnected cc:
            Console.WriteLine($"[CONNECT]  {cc.ConsumerName} tag={cc.ConsumerTag}");
            break;
        case ConsumerDisconnected cd:
            Console.WriteLine($"[DISCONN]  {cd.ConsumerName} reason={cd.Reason}");
            break;
        case PublishFailed pf:
            Console.WriteLine($"[PUB FAIL] {pf.MessageType} — {pf.ExceptionType}");
            break;
    }
}
```

### Replacing or extending the in-memory event store

Register `IOperationalEventBatchSink` to stream events alongside the built-in ring buffer:

```csharp
public class ElasticsearchSink : IOperationalEventBatchSink
{
    public async Task PublishBatch(IReadOnlyList<IOperationalEvent> events,
        CancellationToken cancellationToken = default)
        => await _esClient.BulkAsync(events, cancellationToken);
}

services.AddSingleton<IOperationalEventBatchSink, ElasticsearchSink>();
services.AddBus(...); // in-memory store and your sink both active
```

To **replace** query access with your own persistent store:

```csharp
public class ClickHouseEventStore : IOperationalEventStore, IOperationalEventBatchSink
{
    public long TotalReceived => /* query row count */;
    public IReadOnlyList<IOperationalEvent> GetRecent(OperationalEventFilter? filter = null)
        => /* translate filter → SQL → query */;
    public async Task PublishBatch(IReadOnlyList<IOperationalEvent> events,
        CancellationToken ct = default)
        => await _clickHouse.BulkInsertAsync(events, ct);
}

// Register BEFORE AddBus() — TryAddSingleton means yours wins
services.AddSingleton<ClickHouseEventStore>();
services.AddSingleton<IOperationalEventStore>(sp => sp.GetRequiredService<ClickHouseEventStore>());
services.AddSingleton<IOperationalEventBatchSink>(sp => sp.GetRequiredService<ClickHouseEventStore>());
services.AddBus(...);
```

### Health probe using `ConsumerHealthView.HealthStatus`

```csharp
builder.Services.AddHealthChecks()
    .AddAsyncCheck("bus-consumers", async (IBusDashboardDataService dash, ct) =>
    {
        var consumers    = await dash.GetConsumerHealthAsync(ct);
        var disconnected = consumers.Where(c => c.TotalNodes == 0).ToList();
        var critical     = consumers.Where(c => c.HealthStatus == AlertSeverity.Critical).ToList();

        if (disconnected.Any())
            return HealthCheckResult.Unhealthy(
                $"{disconnected.Count} consumer(s) disconnected: " +
                string.Join(", ", disconnected.Select(c => c.Name)));

        if (critical.Any())
            return HealthCheckResult.Degraded($"{critical.Count} consumer(s) in critical state.");

        return HealthCheckResult.Healthy($"{consumers.Length} consumer(s) all healthy.");
    });
```

---

## Operations Viewer Dashboard

`SimplyWorks.Bus.RabbitMqViewer` adds a server-rendered operations dashboard built with **Pico.css + HTMX**. No JavaScript framework required. All tables auto-refresh via HTMX polling with CSS animations.

```bash
dotnet add package SimplyWorks.Bus.RabbitMqViewer
```

### Pages

| Route | Content |
|---|---|
| `/bus-viewer` | **Overview** — summary cards, consumer health table, active alerts, live event feed |
| `/bus-viewer/consumers` | **Consumer Health** — full grid with status badges, rates, prefetch, priority |
| `/bus-viewer/queues` | **Queue Details** — main + retry + dead-letter depths and rates per queue |
| `/bus-viewer/retries` | **Retry Analysis** — consumers with retry backlogs ordered by severity |
| `/bus-viewer/dead-letters` | **Dead-Letter Inspection** — counts, last exception, last failure timestamp |
| `/bus-viewer/events` | **Live Events** — filterable operational event stream with inline exception details |
| `/bus-viewer/login` | **Login page** — form-based credential entry (no browser credential caching) |
| `/bus-viewer/logout` | **Logout** — clears session cookie and redirects to login |

### UI features

- 🌗 **Dark / Light mode toggle** — persisted in `localStorage`; theme restored before first paint to prevent flash
- ⚠️ **Critical status tooltips** — hovering a "⚠ Critical" badge shows a bullet list of exactly why the consumer is critical (disconnected nodes, dead-letter count, retry count, backpressure, publish/ack imbalance)
- ✨ **Live refresh animations** — a blue-purple shimmer bar sweeps the top edge of each table zone while loading; each `<tbody>` row slides up and fades in with a 60 ms stagger on data arrival
- 🔴 **Pulsing live-data dot** — a green dot next to "Updated HH:mm:ss" confirms live polling is active
- Data auto-refreshes: consumer/queue/retry tables every 10 s, events every 5 s, dead-letters every 15 s

### Registration

```csharp
// Program.cs / Startup.ConfigureServices — call after AddBus()
builder.Services.AddBusViewer(o => o.UseBasicAuth());
```

```csharp
// Middleware pipeline
app.UseStaticFiles();       // serves /_content/SimplyWorks.Bus.RabbitMqViewer/bus-viewer.css
app.UseRouting();
app.UseAuthentication();    // required for RequirePolicy() mode
app.UseAuthorization();
app.UseBusViewer();
app.MapRazorPages();        // discovers BusViewer area automatically
```

> **Note:** `UseBusViewer()` can be placed at any position in the pipeline. When `UseBasicAuth()` is active, an `IStartupFilter` automatically inserts the viewer's authentication gate at the very beginning of the pipeline — before `UseAuthorization` — so 403 errors from the host application's authorization policies can never block the viewer.

### Authentication

#### Mode 1 — Form-based login (`UseBasicAuth()`)

Credentials are validated against `IConfiguration` at request time (supports secret rotation without restart). On success a short-lived **session cookie** is issued:

- Cookie `.BusViewerAuth`, path-scoped to `/bus-viewer`, `HttpOnly`, `SameSite=Strict`
- **Session-only**: deleted when the browser closes, 8-hour absolute maximum
- No browser credential caching (unlike HTTP Basic auth browser dialogs)
- Logout at `/bus-viewer/logout` clears the cookie immediately

```csharp
builder.Services.AddBusViewer(o => o.UseBasicAuth(
    usernameConfigKey: "BusViewer:Username",   // default key
    passwordConfigKey: "BusViewer:Password")); // default key
```

```json
{
  "BusViewer": { "Username": "ops", "Password": "super-secret" }
}
```

Environment variable equivalents: `BusViewer__Username`, `BusViewer__Password`

#### Mode 2 — Decoupled / policy (recommended for production)

Delegates entirely to the host's authorization pipeline. Any scheme works (JWT, cookie, OIDC, Windows Auth):

```csharp
builder.Services.AddAuthorization(o =>
    o.AddPolicy("OpsOnly", p => p.RequireRole("ops")));

builder.Services.AddBusViewer(o => o.RequirePolicy("OpsOnly"));
```

#### Mode 3 — Anonymous (development only)

Throws `InvalidOperationException` at startup when `IHostEnvironment.IsProduction()` is true unless explicitly suppressed:

```csharp
builder.Services.AddBusViewer(o =>
{
    o.AllowAnonymous();
    o.AllowAnonymousInProduction = true; // only if behind proxy/network policy
});
```

### Viewer options reference

| Option | Default | Description |
|---|---|---|
| `Title` | `"SW.Bus Operations"` | Header title displayed in the sidebar |
| `AuthMode` | `None` | Set via `RequirePolicy()`, `UseBasicAuth()`, or `AllowAnonymous()` |
| `PolicyName` | `null` | Policy name used when `AuthMode = Policy` |
| `UsernameConfigKey` | `"BusViewer:Username"` | Config key for the login username |
| `PasswordConfigKey` | `"BusViewer:Password"` | Config key for the login password |
| `AllowAnonymousInProduction` | `false` | Suppress production guard for anonymous mode |

---

## Full `BusOptions` Reference

```csharp
services.AddBus(config =>
{
    // ── Identity ──────────────────────────────────────────────────────────
    config.ApplicationName = "OrderService";

    // ── Connection ────────────────────────────────────────────────────────
    config.HeartBeatTimeOut   = 60;                      // heartbeat seconds (0 = off)
    config.ManagementUrl      = "http://localhost:15672"; // defaults from AMQP URI
    config.ManagementUsername = "guest";
    config.ManagementPassword = "guest";
    config.VirtualHost        = "/";

    // ── Queue defaults ────────────────────────────────────────────────────
    config.DefaultQueuePrefetch = 4;      // QoS prefetch per consumer
    config.DefaultRetryCount    = 5;      // retries before dead-letter
    config.DefaultRetryAfter    = 60;     // seconds between retries
    config.DefaultMaxPriority   = 0;      // 0 = priority queues disabled

    // ── Per-queue overrides ───────────────────────────────────────────────
    config.AddQueueOption("OrderConsumer.OrderCreated",
        prefetch: 10, retryCount: 3, retryAfterSeconds: 30, priority: 5);

    // ── Broadcast listeners ───────────────────────────────────────────────
    config.ListenRetryCount = 5;
    config.ListenRetryAfter = 60;

    // ── Monitoring ────────────────────────────────────────────────────────
    config.MonitoringCacheSeconds = 5;    // cache management API responses (3–60)

    // ── JWT context propagation ───────────────────────────────────────────
    config.Token.Key      = "your-secret-key";
    config.Token.Issuer   = "your-issuer";
    config.Token.Audience = "your-audience";

    // ── Operational events pipeline ───────────────────────────────────────
    config.OperationalEventsEnabled         = true;
    config.OperationalEventsBufferCapacity  = 8192;    // channel buffer before drop
    config.OperationalEventsBatchSize       = 256;     // events per flush batch
    config.OperationalEventsFlushIntervalMs = 1000;    // ms between flushes
    config.OperationalEventsDropOldest      = true;    // drop strategy when buffer full
    config.OperationalEventsSchemaVersion   = "1.0";   // stamped on every event
    config.OperationalEventsStoreCapacity   = 10000;   // in-memory ring buffer size

    // ── Alert thresholds ─────────────────────────────────────────────────
    config.QueueBackpressureThreshold       = 5000;    // queue depth → backpressure event
    config.AlertRetryWarningThreshold       = 10;      // retry count → Warning alert
    config.AlertRetryCriticalThreshold      = 100;     // retry count → Critical alert
    config.AlertDeadLetterCriticalThreshold = 100;     // DL count → Critical alert
});
```

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        RabbitMQ Broker                          │
│  Process Exchange ──► Consumer Queue ──► Retry Queue            │
│  Dead-Letter Exchange ──────────────────► Bad Queue             │
│  Node Exchange ─────► Per-Instance Queue (broadcasts)           │
└─────────────────────────────────────────────────────────────────┘
          ▲                       │
          │ publish               │ consume
          ▼                       ▼
┌─────────────────────────────────────────────────────────────────┐
│                    SimplyWorks.Bus Runtime                       │
│  BasicPublisher ─── ActivitySource ─── OperationalEventPublisher│
│  ConsumerRunner ─── ActivitySource ─── OperationalEventPublisher│
│  ConsumersService ──────────────────── OperationalEventPublisher│
│                                                                 │
│  OperationalEventDispatcher (BackgroundService)                 │
│  ├── InMemoryOperationalEventStore (ring buffer)                │
│  └── [custom IOperationalEventBatchSink sinks]                  │
│                                                                 │
│  BusDashboardDataService ── IConsumerReader (+ cache)           │
│                          ── IOperationalEventStore              │
│                          ── IAlertEvaluator                     │
└─────────────────────────────────────────────────────────────────┘
          │
          ▼ (optional)
┌─────────────────────────────────────────────────────────────────┐
│         SimplyWorks.Bus.RabbitMqViewer                          │
│  /bus-viewer            Overview dashboard                      │
│  /bus-viewer/consumers  Consumer health grid                    │
│  /bus-viewer/queues     Queue depths & rates                    │
│  /bus-viewer/retries    Retry analysis                          │
│  /bus-viewer/dead-letters Dead-letter inspection                │
│  /bus-viewer/events     Live operational event stream           │
└─────────────────────────────────────────────────────────────────┘
```

**Queue naming convention:**

```
{env}.{app}.{ConsumerClass}.{MessageType}
{env}.{app}.{ConsumerClass}.{MessageType}.retry
{env}.{app}.{ConsumerClass}.{MessageType}.bad
```

Example with `ApplicationName = "OrderService"` in `Development`:
```
v3.development.orderservice.ordercreatedconsumer.ordercreated
v3.development.orderservice.ordercreatedconsumer.ordercreated.retry
v3.development.orderservice.ordercreatedconsumer.ordercreated.bad
```

---

## Testing

```csharp
// Use mock publisher — logs publish calls without sending to RabbitMQ
services.AddBusPublishMock();

// IBusDashboardDataService, IConsumerReader, IErrorQueueReader are standard
// interfaces — mock them with any mocking library in unit tests
```

---

## Dependencies

| Package | Version | Used for |
|---|---|---|
| `RabbitMQ.Client` | 6.8.1 | AMQP connection and channel management |
| `EasyNetQ.Management.Client` | 3.0.1 | RabbitMQ Management API calls |
| `Scrutor` | 4.2.2 | Assembly scanning for consumers |
| `SimplyWorks.HttpExtensions` | 8.1.1 | JWT and request context propagation |
| `SimplyWorks.PrimitiveTypes` | 8.1.3 | `RequestContext`, `IConsume<T>`, etc. |

---

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes with tests
4. Submit a pull request

## License

MIT — see [LICENSE](LICENSE)

## Support

- Issues: [github.com/simplify9/SW-Bus/issues](https://github.com/simplify9/SW-Bus/issues)
- Sample app: `SW.Bus.SampleWeb` project in this repository

