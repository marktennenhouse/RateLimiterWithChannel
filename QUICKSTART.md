# Quick Start Guide - PaymentRateLimiter.Core Library

Get up and running with the Payment Rate Limiter library in 5 minutes.

## Prerequisites

- .NET 8.0 SDK
- Azure Service Bus namespace and queue (or use in-memory mock for testing)

## Step 1: Add Library Reference

```bash
# In your project directory
dotnet add reference path/to/PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj
```

## Step 2: Configure Services

In your `Program.cs` or `Startup.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaymentRateLimiter.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure Azure Service Bus
builder.Services.Configure<AzureServiceBusOptions>(
    builder.Configuration.GetSection("AzureServiceBus"));

// Register library services
builder.Services.AddSingleton<PaymentServiceBusService>();
builder.Services.AddSingleton<PaymentStatusService>();
builder.Services.AddHostedService<PaymentProcessorWorker>();

var app = builder.Build();
app.Run();
```

## Step 3: Configure appsettings.json

```json
{
  "AzureServiceBus": {
    "ConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key",
    "QueueName": "payment-requests",
    "MaxConcurrentCalls": 5,
    "AutoCompleteMessages": false
  }
}
```

## Step 4: Use in Your Code

```csharp
using PaymentRateLimiter.Core.Models;
using PaymentRateLimiter.Core.Services;

public class MyPaymentService
{
    private readonly PaymentServiceBusService _serviceBusService;
    private readonly PaymentStatusService _statusService;

    public MyPaymentService(
        PaymentServiceBusService serviceBusService,
        PaymentStatusService statusService)
    {
        _serviceBusService = serviceBusService;
        _statusService = statusService;
    }

    public async Task<string> ProcessPaymentAsync(decimal amount, string cardToken)
    {
        // Create payment request
        var paymentRequest = new PaymentRequest
        {
            PaymentId = Guid.NewGuid().ToString(),
            Amount = amount,
            CardToken = cardToken,
            QueuedAt = DateTime.UtcNow
        };

        // Register payment (creates status channel)
        _statusService.RegisterPayment(paymentRequest);

        // Send to Service Bus queue
        await _serviceBusService.SendPaymentRequestAsync(paymentRequest);

        return paymentRequest.PaymentId;
    }

    public async Task SubscribeToStatusAsync(string paymentId, Action<PaymentStatus> onStatusUpdate)
    {
        var statusReader = _statusService.GetStatusReader(paymentId);
        if (statusReader == null) return;

        await foreach (var status in statusReader.ReadAllAsync())
        {
            onStatusUpdate(status);
            
            if (status.Status == PaymentStatusEnum.Completed || 
                status.Status == PaymentStatusEnum.Failed)
            {
                break; // Terminal state
            }
        }
    }
}
```

## Step 5: Run Your Application

```bash
dotnet run
```

The background worker will start processing payments automatically!

## Next Steps

- See [PaymentRateLimiter.Core/README.md](PaymentRateLimiter.Core/README.md) for detailed usage
- Check [ClientExamples/LibraryUsageExample.cs](ClientExamples/LibraryUsageExample.cs) for more examples
- Review [ARCHITECTURE.md](ARCHITECTURE.md) for system design details

## Troubleshooting

**Error: "Connection string is empty"**
- Make sure `AzureServiceBus:ConnectionString` is set in appsettings.json

**Error: "Queue not found"**
- Create the queue in Azure Portal first
- Ensure queue name matches configuration

**No status updates received**
- Make sure `PaymentProcessorWorker` is registered as a hosted service
- Check that the background worker started (check logs)
