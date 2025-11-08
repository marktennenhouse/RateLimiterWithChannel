# Payment Rate Limiter API

A C# ASP.NET Core Web API for processing credit card payments with rate limiting and real-time status updates.

## Features

- **Rate-Limited Processing**: Maximum 5 concurrent payment operations using semaphore-based concurrency control
- **Real-Time Updates**: Server-Sent Events (SSE) stream status updates to clients
- **Disconnection Handling**: Detects client disconnects and cancels payment processing before charging
- **Memory Management**: Multi-level cleanup strategy prevents memory leaks
- **Thread-Safe**: Uses channels for safe cross-thread communication
- **Production Ready**: Comprehensive logging, error handling, and monitoring

## Architecture

The system uses a producer-consumer pattern with three main threads:

1. **Client Thread (Controller)**: Receives HTTP requests, streams SSE responses
2. **Payment Queue (Channel)**: Bounded queue with capacity of 1000 requests
3. **Worker Threads**: Process payments with rate limiting (max 5 concurrent)

### Key Components

- **PaymentChannelService**: Manages the main payment queue
- **PaymentStatusService**: Thread-safe bridge for worker→client communication
- **PaymentProcessorWorker**: Background service with rate limiting
- **PaymentCleanupService**: Periodic cleanup of stale records
- **PaymentController**: SSE endpoint for client connections

See [ARCHITECTURE.md](ARCHITECTURE.md) for detailed documentation.

## Getting Started

### Prerequisites

- .NET 8.0 SDK or later
- Visual Studio 2022 / VS Code / Rider

### Running the API

```bash
# Restore dependencies
dotnet restore

# Run the application
dotnet run

# Or with hot reload
dotnet watch run
```

The API will be available at:
- HTTPS: `https://localhost:7000`
- HTTP: `http://localhost:5000`

Swagger UI: `https://localhost:7000/swagger`

## API Endpoints

### Process Payment (SSE Stream)

```http
POST /api/payment/process
Content-Type: application/json

{
  "amount": 99.99,
  "cardToken": "tok_demo_123456",
  "customerEmail": "customer@example.com"
}
```

**Response**: Server-Sent Event stream

```
data: {"paymentId":"abc123","message":"Payment request received"}

data: {"paymentId":"abc123","status":"Queued","message":"Payment queued...","timestamp":"..."}

data: {"paymentId":"abc123","status":"Processing","message":"Processing payment details","timestamp":"..."}

data: {"paymentId":"abc123","status":"SendingToProcessor","message":"Sending payment...","timestamp":"..."}

data: {"paymentId":"abc123","status":"Completed","message":"Payment of $99.99 completed successfully","timestamp":"..."}
```

### Get Statistics

```http
GET /api/payment/status/stats
```

**Response**:
```json
{
  "activePayments": 12,
  "disconnectedPayments": 3,
  "totalInMemory": 15,
  "timestamp": "2025-11-06T10:30:00Z"
}
```

## Client Examples

### Vanilla JavaScript

See [ClientExamples/vanilla-javascript-client.html](ClientExamples/vanilla-javascript-client.html)

```javascript
fetch('https://localhost:7000/api/payment/process', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ amount: 99.99, cardToken: 'tok_demo_123456' })
})
.then(response => {
  const reader = response.body.getReader();
  // Read SSE stream...
});
```

### Angular

See [ClientExamples/angular-payment.service.ts](ClientExamples/angular-payment.service.ts)

```typescript
this.paymentService.processPayment({
  amount: 99.99,
  cardToken: 'tok_demo_123456'
}).subscribe({
  next: (status) => console.log(status),
  complete: () => console.log('Done')
});
```

## Configuration

### Rate Limiting

Edit `Services/PaymentProcessorWorker.cs`:

```csharp
// Change from 5 to desired concurrency
private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(10, 10);
```

### Cleanup Intervals

Edit `Services/PaymentCleanupService.cs`:

```csharp
private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);
private readonly TimeSpan _disconnectedThreshold = TimeSpan.FromMinutes(10);
```

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
      "PaymentChannelDemo.Services.PaymentProcessorWorker": "Debug"
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

- Current implementation: Single server, in-memory state
- For multi-server: Replace `PaymentStatusService` with Redis Pub/Sub
- For persistence: Add database for payment records
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

