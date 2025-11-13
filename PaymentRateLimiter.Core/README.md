# PaymentRateLimiter.Core - Class Library

A reusable .NET class library for payment processing with Azure Service Bus rate limiting and real-time status updates.

## Overview

This library provides a complete payment processing system with:
- **Azure Service Bus Integration**: Persistent, scalable queuing
- **Rate Limiting**: Built-in concurrency control via ServiceBusProcessor MaxConcurrentCalls
- **Real-Time Status Updates**: Subscribe to payment status changes via channels
- **Client Disconnection Handling**: Prevents processing abandoned payments
- **Thread-Safe**: Safe for use in multi-threaded applications

## Installation

### Add Project Reference

```bash
dotnet add reference path/to/PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj
```

### NuGet Package (if published)

```bash
dotnet add package PaymentRateLimiter.Core
```

## Quick Start

### 1. Configure Services

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentRateLimiter.Core.Services;

var services = new ServiceCollection();

// Configure Azure Service Bus
services.Configure<AzureServiceBusOptions>(options =>
{
    options.ConnectionString = "Endpoint=sb://your-namespace.servicebus.windows.net/;...";
    options.QueueName = "payment-requests";
    options.MaxConcurrentCalls = 5;
    options.AutoCompleteMessages = false;
});

// Register library services
services.AddSingleton<PaymentServiceBusService>();
services.AddSingleton<PaymentStatusService>();
services.AddHostedService<PaymentProcessorWorker>();

// Add logging
services.AddLogging(builder => builder.AddConsole());

var serviceProvider = services.BuildServiceProvider();
```

### 2. Send Payment Request

```csharp
var serviceBusService = serviceProvider.GetRequiredService<PaymentServiceBusService>();
var statusService = serviceProvider.GetRequiredService<PaymentStatusService>();

var paymentRequest = new PaymentRequest
{
    PaymentId = Guid.NewGuid().ToString(),
    Amount = 99.99m,
    CardToken = "tok_demo_123456",
    CustomerEmail = "customer@example.com",
    QueuedAt = DateTime.UtcNow
};

// Register payment to create status channel
statusService.RegisterPayment(paymentRequest);

// Send to Service Bus queue
await serviceBusService.SendPaymentRequestAsync(paymentRequest);
```

### 3. Subscribe to Status Updates

```csharp
var statusReader = statusService.GetStatusReader(paymentRequest.PaymentId);

if (statusReader != null)
{
    await foreach (var status in statusReader.ReadAllAsync())
    {
        Console.WriteLine($"{status.Status}: {status.Message}");
        
        if (status.Status == PaymentStatusEnum.Completed || 
            status.Status == PaymentStatusEnum.Failed)
        {
            break; // Terminal state reached
        }
    }
}
```

## Components

### Services

#### PaymentServiceBusService
- Wraps Azure Service Bus operations
- `SendPaymentRequestAsync()`: Sends payment to queue
- `Processor`: ServiceBusProcessor for receiving messages
- `StartProcessingAsync()` / `StopProcessingAsync()`: Control message processing

#### PaymentStatusService
- Manages per-payment status channels
- `RegisterPayment()`: Creates status channel for new payment
- `SendStatusAsync()`: Worker sends status updates
- `GetStatusReader()`: Client subscribes to status updates
- `MarkDisconnected()`: Mark payment as cancelled
- `IsPaymentCancelled()`: Check if payment was cancelled
- `GetStatistics()`: Get monitoring statistics

#### PaymentProcessorWorker
- Background service that processes payments from Service Bus
- Uses MaxConcurrentCalls for rate limiting (default: 5)
- Automatically handles message completion/abandonment
- Sends status updates via PaymentStatusService

### Models

- **PaymentRequest**: Payment data with metadata
- **PaymentRequestDto**: Input DTO for payment requests
- **PaymentStatus**: Status update with enum and message
- **PaymentStatusEnum**: Queued, Processing, SendingToProcessor, Completed, Failed

## Configuration

### AzureServiceBusOptions

```csharp
public class AzureServiceBusOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string QueueName { get; set; } = "payment-requests";
    public int MaxConcurrentCalls { get; set; } = 5;
    public bool AutoCompleteMessages { get; set; } = false;
    public string? MaxAutoLockRenewalDurationString { get; set; }
}
```

### appsettings.json Example

```json
{
  "AzureServiceBus": {
    "ConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key",
    "QueueName": "payment-requests",
    "MaxConcurrentCalls": 5,
    "AutoCompleteMessages": false,
    "MaxAutoLockRenewalDuration": "00:05:00"
  }
}
```

## Usage Examples

### Example 1: Simple Payment Processing

```csharp
public class PaymentService
{
    private readonly PaymentServiceBusService _serviceBusService;
    private readonly PaymentStatusService _statusService;

    public PaymentService(
        PaymentServiceBusService serviceBusService,
        PaymentStatusService statusService)
    {
        _serviceBusService = serviceBusService;
        _statusService = statusService;
    }

    public async Task<string> ProcessPaymentAsync(decimal amount, string cardToken)
    {
        var paymentRequest = new PaymentRequest
        {
            PaymentId = Guid.NewGuid().ToString(),
            Amount = amount,
            CardToken = cardToken,
            QueuedAt = DateTime.UtcNow
        };

        _statusService.RegisterPayment(paymentRequest);
        await _serviceBusService.SendPaymentRequestAsync(paymentRequest);

        return paymentRequest.PaymentId;
    }

    public async Task<PaymentStatus?> GetLatestStatusAsync(string paymentId)
    {
        var reader = _statusService.GetStatusReader(paymentId);
        if (reader == null) return null;

        // Try to read latest status (non-blocking)
        if (reader.TryRead(out var status))
        {
            return status;
        }

        return null;
    }
}
```

### Example 2: Web API Controller

```csharp
[ApiController]
[Route("api/[controller]")]
public class PaymentController : ControllerBase
{
    private readonly PaymentServiceBusService _serviceBusService;
    private readonly PaymentStatusService _statusService;

    public PaymentController(
        PaymentServiceBusService serviceBusService,
        PaymentStatusService statusService)
    {
        _serviceBusService = serviceBusService;
        _statusService = statusService;
    }

    [HttpPost("process")]
    public async Task<IActionResult> ProcessPayment([FromBody] PaymentRequestDto dto)
    {
        var paymentRequest = new PaymentRequest
        {
            PaymentId = Guid.NewGuid().ToString(),
            Amount = dto.Amount,
            CardToken = dto.CardToken,
            CustomerEmail = dto.CustomerEmail,
            QueuedAt = DateTime.UtcNow
        };

        _statusService.RegisterPayment(paymentRequest);
        await _serviceBusService.SendPaymentRequestAsync(paymentRequest);

        return Ok(new { paymentId = paymentRequest.PaymentId });
    }

    [HttpGet("status/{paymentId}")]
    public async Task<IActionResult> GetStatus(string paymentId)
    {
        var reader = _statusService.GetStatusReader(paymentId);
        if (reader == null)
            return NotFound();

        var statuses = new List<PaymentStatus>();
        await foreach (var status in reader.ReadAllAsync())
        {
            statuses.Add(status);
            if (status.Status == PaymentStatusEnum.Completed || 
                status.Status == PaymentStatusEnum.Failed)
            {
                break;
            }
        }

        return Ok(statuses);
    }
}
```

### Example 3: Server-Sent Events (SSE)

```csharp
[HttpPost("process-stream")]
public async Task ProcessPaymentStream([FromBody] PaymentRequestDto dto)
{
    Response.Headers.Add("Content-Type", "text/event-stream");
    Response.Headers.Add("Cache-Control", "no-cache");
    Response.Headers.Add("Connection", "keep-alive");

    var paymentRequest = new PaymentRequest
    {
        PaymentId = Guid.NewGuid().ToString(),
        Amount = dto.Amount,
        CardToken = dto.CardToken,
        QueuedAt = DateTime.UtcNow
    };

    _statusService.RegisterPayment(paymentRequest);
    await _serviceBusService.SendPaymentRequestAsync(paymentRequest);

    var statusReader = _statusService.GetStatusReader(paymentRequest.PaymentId);
    if (statusReader == null) return;

    try
    {
        await foreach (var status in statusReader.ReadAllAsync(HttpContext.RequestAborted))
        {
            var json = JsonSerializer.Serialize(status);
            await Response.WriteAsync($"data: {json}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected
        _statusService.MarkDisconnected(paymentRequest.PaymentId);
    }
}
```

## Features

- ✅ **Azure Service Bus Integration**: Persistent queue, scales across servers
- ✅ **Rate Limiting**: MaxConcurrentCalls (default: 5, configurable)
- ✅ **Real-Time Status Updates**: Subscribe via channels
- ✅ **Client Disconnection Handling**: Prevents processing abandoned payments
- ✅ **Thread-Safe**: Safe for concurrent use
- ✅ **Automatic Cleanup**: Service Bus handles message lifecycle
- ✅ **Monitoring**: GetStatistics() for active/disconnected/total counts

## Dependencies

- .NET 8.0
- Azure.Messaging.ServiceBus (7.20.1)
- Microsoft.Extensions.Hosting (9.0.10)
- Microsoft.Extensions.Logging.Abstractions (9.0.0)
- Microsoft.Extensions.Options (9.0.0)

## Requirements

- Azure Service Bus namespace and queue
- Connection string with appropriate permissions

## Status Flow

1. **Queued**: Payment dequeued, waiting for processing slot
2. **Queued**: Processing slot acquired, starting payment processing
3. **Processing**: Processing payment details
4. **SendingToProcessor**: Sending payment to third-party processor
5. **Completed** or **Failed**: Terminal state

## Error Handling

- Failed payments send `Failed` status
- Disconnected payments are abandoned (Service Bus handles retry)
- Dead letter queue for permanently failed messages
- All exceptions are logged

## Monitoring

```csharp
var (active, disconnected, total) = _statusService.GetStatistics();
Console.WriteLine($"Active: {active}, Disconnected: {disconnected}, Total: {total}");
```

## Thread Safety

All services are thread-safe:
- `PaymentServiceBusService`: Thread-safe (Service Bus SDK)
- `PaymentStatusService`: Uses ConcurrentDictionary and Channels
- `PaymentProcessorWorker`: Background service, thread-safe

## License

MIT License
