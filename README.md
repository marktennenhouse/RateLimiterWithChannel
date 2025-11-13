# Payment Rate Limiter

A C# class library for processing credit card payments with Azure Service Bus rate limiting and real-time status updates. Can be integrated into any .NET application.

## Features

- **Rate-Limited Processing**: Maximum 5 concurrent payment operations using Azure Service Bus MaxConcurrentCalls
- **Persistent Queue**: Azure Service Bus queue survives server restarts and scales across multiple servers
- **Real-Time Updates**: Server-Sent Events (SSE) stream status updates to clients
- **Disconnection Handling**: Detects client disconnects and cancels payment processing before charging
- **Memory Management**: Simplified cleanup (Service Bus handles queue cleanup automatically)
- **Thread-Safe**: Uses Service Bus and channels for safe cross-thread communication
- **Production Ready**: Comprehensive logging, error handling, and monitoring

## Project Structure

This repository contains:

1. **PaymentRateLimiter.Core** - The reusable class library (use this in your projects)
2. **PaymentChannelDemo** - Example web API demonstrating library usage
3. **ClientExamples** - Examples showing how to use the library

## Architecture

The library uses a producer-consumer pattern with Azure Service Bus:

1. **Your Application**: Sends payment requests via `PaymentServiceBusService`
2. **Azure Service Bus Queue**: Persistent queue that survives restarts and scales across servers
3. **ServiceBusProcessor**: Processes messages with built-in rate limiting (max 5 concurrent)

### Key Components

- **PaymentServiceBusService**: Wraps Azure Service Bus operations (sending/receiving messages)
- **PaymentStatusService**: Thread-safe bridge for worker→client communication (status channels)
- **PaymentProcessorWorker**: Background service using ServiceBusProcessor with MaxConcurrentCalls

See [PaymentRateLimiter.Core/README.md](PaymentRateLimiter.Core/README.md) for library usage and [ARCHITECTURE.md](ARCHITECTURE.md) for detailed documentation.

## Getting Started

### Prerequisites

- .NET 8.0 SDK or later
- Azure Service Bus namespace and queue
- Visual Studio 2022 / VS Code / Rider

### Using the Library

1. **Add the library to your project**:
   ```bash
   dotnet add reference PaymentRateLimiter.Core/PaymentRateLimiter.Core.csproj
   ```

2. **Register services** (see [PaymentRateLimiter.Core/README.md](PaymentRateLimiter.Core/README.md) for details)

3. **Use in your code**:
   ```csharp
   var paymentRequest = new PaymentRequest { ... };
   _statusService.RegisterPayment(paymentRequest);
   await _serviceBusService.SendPaymentRequestAsync(paymentRequest);
   ```

### Running the Example Web API

The `PaymentChannelDemo` project is an example web API that **uses the library** (via project reference). It demonstrates:

```bash
# Restore dependencies
dotnet restore

# Run the application
dotnet run --project PaymentChannelDemo

# The API will be available at:
# HTTPS: https://localhost:7000
# Swagger UI: https://localhost:7000/swagger
```

## Library Usage

### Basic Example

```csharp
// Register services
services.Configure<AzureServiceBusOptions>(options => {
    options.ConnectionString = "Endpoint=sb://...";
    options.QueueName = "payment-requests";
    options.MaxConcurrentCalls = 5;
});
services.AddSingleton<PaymentServiceBusService>();
services.AddSingleton<PaymentStatusService>();
services.AddHostedService<PaymentProcessorWorker>();

// Use in your code
var paymentRequest = new PaymentRequest {
    PaymentId = Guid.NewGuid().ToString(),
    Amount = 99.99m,
    CardToken = "tok_demo_123456",
    QueuedAt = DateTime.UtcNow
};

_statusService.RegisterPayment(paymentRequest);
await _serviceBusService.SendPaymentRequestAsync(paymentRequest);

// Subscribe to status updates
var statusReader = _statusService.GetStatusReader(paymentRequest.PaymentId);
await foreach (var status in statusReader.ReadAllAsync()) {
    Console.WriteLine($"{status.Status}: {status.Message}");
}
```

See [PaymentRateLimiter.Core/README.md](PaymentRateLimiter.Core/README.md) for complete usage examples.

## Client Examples

### C# Library Usage

See [ClientExamples/LibraryUsageExample.cs](ClientExamples/LibraryUsageExample.cs) for a complete example showing how to:
- Register services
- Send payment requests
- Subscribe to status updates

### HTML Client Example

See [ClientExamples/library-client-example.html](ClientExamples/library-client-example.html) for an HTML page demonstrating client-side interaction with an API that uses the library.

### Web API Example

The `PaymentChannelDemo` project shows how to create a web API using the library with Server-Sent Events (SSE) streaming.

## Configuration

### Azure Service Bus Configuration

Configure in your `appsettings.json` or via code:

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

### Rate Limiting

Change `MaxConcurrentCalls` to adjust concurrent processing (default: 5).

### CORS Origins

Edit `Program.cs`:

```csharp
policy.WithOrigins(
    "http://localhost:4200",  // Add your origins
    "https://your-domain.com"
)
```

## Logging

Logs are configured in `appsettings.json` and `appsettings.Development.json`.

To adjust logging levels:

```json
{
  "Logging": {
    "LogLevel": {
      "PaymentRateLimiter.Core.Services.PaymentProcessorWorker": "Debug"
    }
  }
}
```

## Testing

### Test Payment Processing

1. Open the Swagger UI: `https://localhost:7000/swagger`
2. Or use the HTML client: `ClientExamples/vanilla-javascript-client.html`
3. Or use curl:

```bash
curl -X POST https://localhost:7000/api/payment/process \
  -H "Content-Type: application/json" \
  -d '{"amount":99.99,"cardToken":"tok_demo_123456"}' \
  --no-buffer
```

### Test Client Disconnection

1. Start a payment request
2. Close the browser tab or cancel the curl request (Ctrl+C)
3. Check logs - should see "Client disconnected" and "Skipping cancelled payment"
4. Verify no charge was made to the "third-party processor"

### Test Rate Limiting

1. Send 10+ payment requests simultaneously
2. Check logs - only 5 should be "acquired processing slot" at a time
3. Others should wait until slots are available

## Production Considerations

### Security

- Replace `CardToken` with proper payment processor integration (Stripe, Square, etc.)
- Add authentication/authorization to endpoints
- Implement rate limiting per IP/user
- Use HTTPS only
- Validate all input data
- Never log sensitive payment information

### Scaling

- ✅ **Queue scales**: Azure Service Bus queue works across multiple servers
- ✅ **Rate limiting scales**: MaxConcurrentCalls works across servers automatically
- ⚠️ **Status channels**: Still in-memory (for SSE). To scale SSE:
  - Use Redis Pub/Sub for status updates, OR
  - Use webhooks instead of SSE, OR
  - Use polling endpoint instead of SSE
- For observability: Add distributed tracing (OpenTelemetry)

### Monitoring

- Monitor `/api/payment/status/stats` endpoint
- Set up alerts for high `disconnectedPayments` count
- Track payment processing time metrics
- Monitor semaphore wait times

## License

MIT License - feel free to use this in your projects.

## Support

For questions or issues, please check the [ARCHITECTURE.md](ARCHITECTURE.md) documentation.

