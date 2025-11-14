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
- ✅ **Rate Limiting**: MaxConcurrentCalls (default: 5, configurable) - automatically enforced by ServiceBusProcessor
- ✅ **Real-Time Status Updates**: Subscribe via per-payment channels
- ✅ **Per-Payment Channel Isolation**: Each payment gets its own dedicated channel for secure, isolated status updates
- ✅ **Client Disconnection Handling**: Prevents processing abandoned payments
- ✅ **Thread-Safe**: Safe for concurrent use
- ✅ **Automatic Cleanup**: Service Bus handles message lifecycle
- ✅ **Monitoring**: GetStatistics() for active/disconnected/total counts

## Per-Payment Channel Isolation Architecture

### How Status Channels Work

The `PaymentStatusService` uses a `ConcurrentDictionary<string, Channel<PaymentStatus>>` to store channels in memory. **Each payment gets its own dedicated channel**, ensuring that status updates are delivered only to the specific client that initiated that payment.

**Key Concept**: The `ConcurrentDictionary` holds each channel in memory, and getting a reader based on the `PaymentId` allows the code to send messages to **ONLY a single specific listener**.

### One-to-One Mapping

```
1 Payment = 1 Channel = 1 Client Connection
```

**Example Flow:**

1. **Client 1** requests payment (PaymentId: "abc-123"):
   ```csharp
   _statusService.RegisterPayment(paymentRequest); // Creates Channel A
   var reader = _statusService.GetStatusReader("abc-123"); // Gets Channel A's reader
   ```

2. **Client 2** requests payment (PaymentId: "def-456"):
   ```csharp
   _statusService.RegisterPayment(paymentRequest); // Creates Channel B
   var reader = _statusService.GetStatusReader("def-456"); // Gets Channel B's reader
   ```

3. **Worker** processes Payment "abc-123":
   ```csharp
   await _statusService.SendStatusAsync("abc-123", status);
   // Writes to Channel A ONLY → Client 1 receives message ✅
   // Client 2 receives nothing ✅
   ```

4. **Worker** processes Payment "def-456":
   ```csharp
   await _statusService.SendStatusAsync("def-456", status);
   // Writes to Channel B ONLY → Client 2 receives message ✅
   // Client 1 receives nothing ✅
   ```

### Memory Structure

```csharp
ConcurrentDictionary<string, Channel<PaymentStatus>> _statusChannels

// Example in-memory state:
{
  "abc-123" → Channel A  (for Payment A)
  "def-456" → Channel B  (for Payment B)
  "ghi-789" → Channel C  (for Payment C)
  ...
}
```

### Why Per-Payment Channels?

**Benefits:**
- ✅ **Isolation**: Each payment's status updates are completely isolated
- ✅ **Privacy**: Clients only see their own payment status (no cross-contamination)
- ✅ **Simplicity**: No filtering needed - each channel is dedicated to one payment
- ✅ **Concurrency**: Multiple payments can process simultaneously without interference
- ✅ **Cleanup**: When a payment completes, only its channel is cleaned up

**If we used a single shared channel:**
- ❌ Clients would see other payments' statuses (privacy issue)
- ❌ Clients would need to filter messages by PaymentId (overhead)
- ❌ One client's disconnection could affect others (no isolation)

### Thread Safety

The `ConcurrentDictionary` provides thread-safe access, allowing:
- Multiple worker threads to write status updates concurrently
- Multiple client threads to read from their respective channels
- Safe cleanup operations without race conditions

## How Rate Limiting Works (MaxConcurrentCalls)

### Overview

The `MaxConcurrentCalls` setting controls how many payment messages are processed simultaneously. This limit is **automatically enforced by the ServiceBusProcessor** from the Azure SDK - you don't need to manage it manually.

### Configuration

```csharp
// In PaymentServiceBusService.cs
var processorOptions = new ServiceBusProcessorOptions
{
    MaxConcurrentCalls = 5,  // Only 5 messages processed at once
    AutoCompleteMessages = false
};
```

### Example: 100 Messages with MaxConcurrentCalls = 5

When 100 payment requests are in the Service Bus queue:

```
Time    Messages in Queue    Active Handlers    What Happens
─────────────────────────────────────────────────────────────────
T+0s    100 messages         0                  Processor starts
T+0s    100 messages         5                  First 5 handlers start
                                                  (MaxConcurrentCalls = 5)
T+1s    95 messages          5                  Processing 5 payments
                                                  (95 messages waiting in queue)
T+8s    95 messages          4                  One payment completes
                                                  Handler #1 finishes
T+8s    94 messages          5                  Processor immediately starts
                                                  handler #6 (slot freed)
T+16s   90 messages          4                  Another payment completes
                                                  Handler #2 finishes
T+16s   89 messages          5                  Processor starts handler #7
...     ...                  ...                Continues until all 100 done
T+160s  0 messages           0                  All 100 payments processed
                                                  (20 batches of 5)
```

### How It Works Internally

The `ServiceBusProcessor` from the Azure SDK internally manages concurrency:

1. **Receives messages** from Service Bus queue
2. **Tracks active handlers** - counts how many `ProcessMessageAsync` handlers are currently running
3. **Enforces limit** - only invokes your handler when count < MaxConcurrentCalls
4. **Waits for completion** - when a handler finishes, it automatically starts the next one

**Key Points**:
- The limiting happens **inside the Azure SDK**, not in your code
- Messages stay in the Service Bus queue until a handler slot is available
- No manual semaphore needed - the processor manages it automatically
- FIFO order is maintained - messages are processed in order

### Visual Flow

```
Service Bus Queue (100 messages)
    │
    ├─ Message 1 ──► [Handler 1] ──► Processing...
    ├─ Message 2 ──► [Handler 2] ──► Processing...
    ├─ Message 3 ──► [Handler 3] ──► Processing...
    ├─ Message 4 ──► [Handler 4] ──► Processing...
    ├─ Message 5 ──► [Handler 5] ──► Processing...
    │
    ├─ Message 6 ──► [WAITING - MaxConcurrentCalls = 5]
    ├─ Message 7 ──► [WAITING]
    ├─ ...
    └─ Message 100 ─► [WAITING]
    
When Handler 1 completes:
    Message 6 ──► [Handler 6] ──► Processing...
    (Handler 1 slot freed, Handler 6 starts immediately)
```

### Your Code

In your code, you simply register the handler:

```csharp
processor.ProcessMessageAsync += async args => {
    // This handler is only called when a slot is available
    // The processor automatically manages: "How many handlers are running?"
    // If count >= MaxConcurrentCalls, it waits before calling this
    await ProcessServiceBusMessageAsync(args, stoppingToken);
    // When this completes, processor automatically starts next handler
};
```

You don't need to manage semaphores or thread counts - the processor does it for you!

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
