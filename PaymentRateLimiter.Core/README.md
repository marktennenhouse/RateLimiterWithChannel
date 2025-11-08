# PaymentRateLimiter.Core - Reusable Class Library

This is the reusable class library version of the Payment Rate Limiter system. It contains all the core functionality for payment processing with rate limiting and Server-Sent Events support.

## Contents

### Models
- `PaymentRequest` - Full payment object with disconnection tracking
- `PaymentRequestDto` - API input DTO
- `PaymentStatus` - Status message object
- `PaymentStatusEnum` - Status enumeration (Queued, Processing, SendingToProcessor, Completed, Failed)

### Services
- `PaymentChannelService` - Main payment queue management (bounded channel)
- `PaymentStatusService` - Thread-safe status channel manager
- `PaymentProcessorWorker` - Background worker with rate limiting (BackgroundService)
- `PaymentCleanupService` - Periodic cleanup background service (BackgroundService)

## Dependencies

- .NET 8.0
- Microsoft.Extensions.Hosting (9.0.10)
- Microsoft.AspNetCore.Mvc.Core (2.2.5)

## Usage in Your Project

### 1. Add Project Reference

```bash
dotnet add reference path/to/PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj
```

### 2. Register Services in Program.cs

```csharp
using PaymentRateLimiter.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// Register the payment rate limiter services
builder.Services.AddSingleton<PaymentChannelService>();
builder.Services.AddSingleton<PaymentStatusService>();
builder.Services.AddHostedService<PaymentProcessorWorker>();
builder.Services.AddHostedService<PaymentCleanupService>();

// Configure JSON to serialize enums as strings
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
```

### 3. Create a Controller

```csharp
using Microsoft.AspNetCore.Mvc;
using PaymentRateLimiter.Core.Models;
using PaymentRateLimiter.Core.Services;
using System.Text.Json;

[ApiController]
[Route("api/[controller]")]
public class PaymentController : ControllerBase
{
    private readonly PaymentChannelService _channelService;
    private readonly PaymentStatusService _statusService;

    public PaymentController(
        PaymentChannelService channelService,
        PaymentStatusService statusService)
    {
        _channelService = channelService;
        _statusService = statusService;
    }

    [HttpPost("process")]
    public async Task ProcessPayment([FromBody] PaymentRequestDto dto)
    {
        Response.Headers.Add("Content-Type", "text/event-stream");
        
        var paymentRequest = new PaymentRequest
        {
            PaymentId = Guid.NewGuid().ToString(),
            Amount = dto.Amount,
            CardToken = dto.CardToken,
            QueuedAt = DateTime.UtcNow
        };

        _statusService.RegisterPayment(paymentRequest);
        await _channelService.Writer.WriteAsync(paymentRequest);

        var statusReader = _statusService.GetStatusReader(paymentRequest.PaymentId);
        await foreach (var status in statusReader!.ReadAllAsync())
        {
            var json = JsonSerializer.Serialize(status);
            await Response.WriteAsync($"data: {json}\\n\\n");
            await Response.Body.FlushAsync();
        }
    }
}
```

## Features

- ✅ Rate limiting (max 5 concurrent by default, configurable)
- ✅ Real-time status updates via Server-Sent Events
- ✅ Client disconnection detection
- ✅ Multi-level cleanup strategy
- ✅ Thread-safe cross-thread communication
- ✅ FIFO queue ordering
- ✅ Backpressure protection (bounded channel with capacity 1000)

## Configuration

All configuration is done through the services themselves. No external configuration required.

### To change concurrent processing limit:

Edit `PaymentProcessorWorker.cs` line 19:
```csharp
private readonly SemaphoreSlim _processingSemaphore = new SemaphoreSlim(10, 10); // Changed from 5
```

### To change queue capacity:

Edit `PaymentChannelService.cs` line 18:
```csharp
_paymentChannel = Channel.CreateBounded<PaymentRequest>(new BoundedChannelOptions(2000) // Changed from 1000
```

## Exporting to Another Machine

See the parent directory's [EXPORT_GUIDE.md](../EXPORT_GUIDE.md) for instructions on copying this library to another project under a different Git account.

## License

MIT License - see parent directory for details.

