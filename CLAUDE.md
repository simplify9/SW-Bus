# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**SimplyWorks.Bus** is a production-grade .NET 8 message bus library for ASP.NET Core microservices, built on RabbitMQ. It is distributed as three NuGet packages:

- **SimplyWorks.Bus** — core runtime (publishing, consuming, retries, dead-letter routing, OpenTelemetry)
- **SimplyWorks.Bus.RabbitMqExtensions** — public contracts and monitoring interfaces (no `RabbitMQ.Client` dependency)
- **SimplyWorks.Bus.RabbitMqViewer** — HTMX-based admin dashboard (Razor Pages, Pico.css, no JS framework)

## Build & Test Commands

```bash
# Restore dependencies
dotnet restore

# Build entire solution
dotnet build SW.Bus.sln

# Run unit tests
dotnet test SW.Bus.UnitTests/SW.Bus.UnitTests.csproj

# Run a single test class or method
dotnet test SW.Bus.UnitTests/SW.Bus.UnitTests.csproj --filter "FullyQualifiedName~ConsumerDiscoveryTests"

# Run integration tests (requires Docker — spins up RabbitMQ via Testcontainers)
dotnet test SW.Bus.IntegrationTests/SW.Bus.IntegrationTests.csproj

# Pack NuGet packages (Release)
dotnet pack SW.Bus/SW.Bus.csproj -c Release
dotnet pack SW.Bus.RabbitMqExtensions/SW.Bus.RabbitMqExtensions.csproj -c Release
dotnet pack SW.Bus.RabbitMqViewer/SW.Bus.RabbitMqViewer.csproj -c Release
```

NuGet packages are published automatically via GitHub Actions on push to `main`.

## Architecture

### Projects

| Project | Role |
|---|---|
| `SW.Bus` | Core runtime — IHostedService, publisher, consumer runner, monitoring |
| `SW.Bus.RabbitMqExtensions` | Contracts only — interfaces and DTOs with no RabbitMQ.Client dependency |
| `SW.Bus.RabbitMqViewer` | Optional Razor Pages dashboard mounted at `/bus-viewer` |
| `SW.Bus.SampleWeb` | Demo application showing all patterns |
| `SW.Bus.UnitTests` | MSTest integration tests using `TestHost` |
| `SW.Bus.IntegrationTests` | MSTest + Testcontainers; boots real RabbitMQ in Docker (with and without the delayed-message plugin) |

### Consumer Lifecycle

`ConsumersService` (IHostedService) → `ConsumerDiscovery` (reflection/Scrutor scan) → `ConsumerRunner` (per-consumer loop)

- Consumers implement `IConsume<T>` (typed) or `IConsume` (multi-message)
- Implement `IConsumeExtended` additionally to configure per-consumer prefetch and priority at runtime
- On failure: message is routed to `.retry` queue; after max retries, to `.bad` (dead-letter) queue

### Queue Naming Convention

```
{env}.{app}.{ConsumerClass}.{MessageType}
{env}.{app}.{ConsumerClass}.{MessageType}.retry
{env}.{app}.{ConsumerClass}.{MessageType}.bad
```

Example: `v3.development.orderservice.ordercreatedconsumer.ordercreated`

The exchange used for publishing is `v3.{environment}`. Dead-letter exchange is `v3.{environment}.deadletter`.

### Broadcasting

`IBroadcast` / `IListen<T>` — fan-out to all running instances via a per-instance `NodeExchange` (keyed by `NodeId`). Use this instead of `IPublish` when every service instance should receive the message.

### Monitoring Stack

- `IConsumerReader` — live queue stats from RabbitMQ Management API (cached, configurable via `MonitoringCacheSeconds`)
- `IErrorQueueReader` — peek into retry/dead-letter queues without removing messages
- `IBusDashboardDataService` — aggregate read layer that combines consumer stats, error queues, and operational events
- `InMemoryOperationalEventStore` — ring buffer storing lifecycle events (configurable `OperationalEventsCapacity`)
- `OperationalEventDispatcher` — batches events to pluggable `IOperationalEventBatchSink` implementations (e.g., Elasticsearch, ClickHouse)
- OpenTelemetry: `ActivitySource` for distributed tracing, `Meter` for metrics

### DI Registration Pattern

```csharp
services.AddBus(config => { config.ApplicationName = "MyApp"; });
services.AddBusPublish();   // registers IPublish, IBroadcast
services.AddBusConsume();   // registers ConsumersService IHostedService + consumer discovery
services.AddBusListen();    // registers broadcast listeners
services.AddBusViewer(...); // optional dashboard
```

### Key Files

- `SW.Bus/BusOptions.cs` — all configuration properties and queue naming logic
- `SW.Bus/IServiceCollectionExtensions.cs` — DI wiring for all extension methods
- `SW.Bus/ConsumersService.cs` — consumer lifecycle management (start/stop/reconnect)
- `SW.Bus/BasicPublisher.cs` — core publish implementation
- `SW.Bus/ConsumerDiscovery.cs` — reflection-based consumer scanning
- `SW.Bus/OperationalEventInfrastructure.cs` — event pipeline components
- `SW.Bus.RabbitMqExtensions/` — all public-facing interfaces and DTOs
- `SW.Bus.RabbitMqViewer/Areas/BusViewer/Pages/` — dashboard Razor Pages

### Testing Approach

Tests use `TestHost` (real in-process ASP.NET Core host) rather than mocks. Tests register consumers and verify discovery, binding, and publishing behavior end-to-end. RabbitMQ connection is required for integration tests.
