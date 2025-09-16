# SimplyWorks.Bus

[![Build and Publish NuGet Package](https://github.com/simplify9/SW-Bus/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/simplify9/SW-Bus/actions/workflows/nuget-publish.yml)
[![NuGet](https://img.shields.io/nuget/v/SimplyWorks.Bus.svg)](https://www.nuget.org/packages/SimplyWorks.Bus)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A lightweight .NET 8 message bus library built on top of RabbitMQ, designed to simplify publish-subscribe patterns and event-driven architectures in ASP.NET Core applications.

## Features

- 🚌 **Simple Message Publishing**: Easy-to-use interfaces for publishing messages
- 📡 **Broadcasting**: Send messages to multiple consumers across different application instances
- 🔄 **Automatic Retries**: Built-in retry mechanism with dead letter queues
- 🎯 **Typed Consumers**: Strong-typed message consumers with `IConsume<T>`
- 📻 **Event Listeners**: Broadcast listeners with `IListen<T>` for cross-application communication
- 🔐 **JWT Integration**: Built-in JWT token propagation for authenticated messaging
- 🏗️ **ASP.NET Core Integration**: Seamless dependency injection and hosted service integration
- 🧪 **Testing Support**: Mock publisher for unit testing scenarios

## Installation

```bash
dotnet add package SimplyWorks.Bus
```

## Quick Start

### 1. Configuration

Add RabbitMQ connection string to your `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "RabbitMQ": "amqp://guest:guest@localhost:5672/"
  }
}
```

### 2. Service Registration

In your `Startup.cs` or `Program.cs`:

```csharp
// Basic bus setup
services.AddBus(config =>
{
    config.ApplicationName = "MyApp";
    // Optional JWT configuration
    config.Token.Key = Configuration["Token:Key"];
    config.Token.Issuer = Configuration["Token:Issuer"];
    config.Token.Audience = Configuration["Token:Audience"];
});

// For publishing messages
services.AddBusPublish();

// For consuming messages
services.AddBusConsume();

// For listening to broadcasts only
services.AddBusListen();
```

### 3. Publishing Messages

```csharp
public class OrderController : ControllerBase
{
    private readonly IPublish _publisher;
    private readonly IBroadcast _broadcaster;

    public OrderController(IPublish publisher, IBroadcast broadcaster)
    {
        _publisher = publisher;
        _broadcaster = broadcaster;
    }

    [HttpPost]
    public async Task<IActionResult> CreateOrder(CreateOrderRequest request)
    {
        var order = new OrderCreated
        {
            OrderId = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            Amount = request.Amount
        };

        // Publish to specific consumers
        await _publisher.Publish(order);

        // Broadcast to all listening applications
        await _broadcaster.Broadcast(new OrderNotification 
        { 
            Message = "New order created" 
        });

        return Ok();
    }
}
```

### 4. Consuming Messages

Create typed consumers by implementing `IConsume<T>`:

```csharp
public class OrderCreatedConsumer : IConsume<OrderCreated>
{
    private readonly ILogger<OrderCreatedConsumer> _logger;
    private readonly IOrderService _orderService;

    public OrderCreatedConsumer(ILogger<OrderCreatedConsumer> logger, IOrderService orderService)
    {
        _logger = logger;
        _orderService = orderService;
    }

    public async Task Process(OrderCreated message)
    {
        _logger.LogInformation("Processing order {OrderId}", message.OrderId);
        await _orderService.ProcessOrder(message);
    }
}
```

### 5. Listening to Broadcasts

Create broadcast listeners by implementing `IListen<T>`:

```csharp
public class OrderNotificationListener : IListen<OrderNotification>
{
    private readonly INotificationService _notificationService;

    public OrderNotificationListener(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    public async Task Process(OrderNotification message)
    {
        await _notificationService.SendNotification(message.Message);
    }
}
```

### 6. String-based Consumers

For dynamic message types, implement `IConsume`:

```csharp
public class GenericConsumer : IConsume
{
    public async Task<IEnumerable<string>> GetMessageTypeNames()
    {
        return new[] { "DynamicMessage1", "DynamicMessage2" };
    }

    public async Task Process(string messageTypeName, string message)
    {
        Console.WriteLine($"Received {messageTypeName}: {message}");
    }
}
```

## Configuration Options

### Bus Options

```csharp
services.AddBus(config =>
{
    config.ApplicationName = "MyApplication";           // Required: Your application name
    config.DefaultQueuePrefetch = 1;                    // Messages to prefetch (default: 1)
    config.DefaultRetryCount = 3;                       // Retry attempts (default: 3)
    config.DefaultRetryAfter = 30000;                   // Retry delay in ms (default: 30s)
    config.HeartBeatTimeOut = 60;                       // RabbitMQ heartbeat timeout
    config.ListenRetryCount = 3;                        // Listener retry attempts
    config.ListenRetryAfter = 30;                       // Listener retry delay
    
    // JWT Token configuration (optional)
    config.Token.Key = "your-secret-key";
    config.Token.Issuer = "your-issuer";
    config.Token.Audience = "your-audience";
});
```

### Queue Options

Customize queue behavior for specific message types:

```csharp
services.AddBus(config =>
{
    config.Options["MyMessageType"] = new QueueOptions
    {
        RetryCount = 5,
        RetryAfter = 60000,
        QueuePrefetch = 10
    };
});
```

## Testing

For unit testing, use the mock publisher:

```csharp
// In your test setup
services.AddBusPublishMock();

// The mock publisher will log publish calls instead of sending to RabbitMQ
```

## Advanced Features

### Error Handling

Consumers can implement error handling methods:

```csharp
public class OrderConsumer : IConsume<OrderCreated>
{
    public async Task Process(OrderCreated message)
    {
        // Main processing logic
    }

    public async Task OnFail(Exception exception, string rawMessage)
    {
        // Handle processing failures
        // This method is optional
    }
}
```

### Message Context

Access request context and user information in consumers:

```csharp
public class AuthenticatedConsumer : IConsume<SecureMessage>
{
    private readonly RequestContext _requestContext;

    public AuthenticatedConsumer(RequestContext requestContext)
    {
        _requestContext = requestContext;
    }

    public async Task Process(SecureMessage message)
    {
        var user = _requestContext.User; // Access the authenticated user
        var correlationId = _requestContext.CorrelationId; // Trace requests
    }
}
```

### Consumer Refresh

Refresh consumers dynamically at runtime:

```csharp
await _broadcaster.RefreshConsumers();
```

## Architecture

SW.Bus uses RabbitMQ exchanges and queues with the following pattern:

- **Process Exchange**: For direct message routing to specific consumers
- **Node Exchange**: For broadcasting messages to all application instances
- **Dead Letter Exchanges**: For failed message handling and retries
- **Retry Queues**: Temporary storage for messages awaiting retry

## Dependencies

- .NET 8.0
- RabbitMQ.Client 6.8.1
- SimplyWorks.HttpExtensions 5.0.0
- SimplyWorks.PrimitiveTypes 6.0.5
- Scrutor 4.2.2 (for assembly scanning)

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Support

For issues and questions:
- Create an issue on [GitHub](https://github.com/simplify9/SW-Bus/issues)
- Check the sample application in `SW.Bus.SampleWeb` for usage examples
