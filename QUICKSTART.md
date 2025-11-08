# Quick Start Guide

Get the Payment Rate Limiter API running in 5 minutes!

## Step 1: Restore and Build

```bash
dotnet restore
dotnet build
```

## Step 2: Run the Application

```bash
dotnet run
```

You should see:
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:7000
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
info: PaymentChannelDemo.Services.PaymentProcessorWorker[0]
      PaymentProcessorWorker started. Max concurrent: 5
```

## Step 3: Test with Swagger UI

1. Open your browser: `https://localhost:7000/swagger`
2. Click on **POST /api/payment/process**
3. Click **Try it out**
4. Use this request body:
```json
{
  "amount": 99.99,
  "cardToken": "tok_demo_123456",
  "customerEmail": "customer@example.com"
}
```
5. Click **Execute**
6. Watch the response - you'll see Server-Sent Events streaming!

## Step 4: Test with HTML Client (Better UX)

1. Open `ClientExamples/vanilla-javascript-client.html` in your browser
2. Update the API URL if needed (line 143):
   ```javascript
   const API_URL = 'https://localhost:7000/api/payment';
   ```
3. Click **Process Payment**
4. Watch real-time status updates appear!

## What You'll See

### Console Output
```
info: Payment abc123... acquired processing slot. Amount: 99.99
info: Payment abc123... completed successfully. Amount: 99.99
info: Payment abc123... released processing slot
```

### Browser (HTML Client)
```
Payment ID: abc123...
Payment request received

Queued: Payment queued and processing slot acquired
Processing: Processing payment details
SendingToProcessor: Sending payment to third-party processor
Completed: Payment of $99.99 completed successfully
```

## Test Disconnection Handling

1. Start a payment in the HTML client
2. Immediately click **Cancel Payment** button
3. Check the console logs:
```
warn: Client disconnected for payment abc123... after 123ms. 
      Payment marked as cancelled to prevent charging.
info: Skipping cancelled payment abc123... (disconnected before processing)
```

✅ No charge was made!

## Test Rate Limiting

1. Open 10 browser tabs with the HTML client
2. Click **Process Payment** in all tabs quickly
3. Check console - only 5 will show "acquired processing slot" initially
4. Others wait until slots are available
5. All eventually complete

## Check Statistics

```bash
curl https://localhost:7000/api/payment/status/stats
```

Response:
```json
{
  "activePayments": 3,
  "disconnectedPayments": 1,
  "totalInMemory": 4,
  "timestamp": "2025-11-06T10:30:00Z"
}
```

## Next Steps

- Read [ARCHITECTURE.md](ARCHITECTURE.md) for detailed system design
- Read [TESTING.md](TESTING.md) for comprehensive test scenarios
- Read [README.md](README.md) for configuration and deployment

## Troubleshooting

### Port Already in Use
```bash
# Use different ports
dotnet run --urls "https://localhost:7001;http://localhost:5001"
```

### SSL Certificate Issues
```bash
# Trust the development certificate
dotnet dev-certs https --trust
```

### CORS Errors in Browser
- Make sure API is running on the expected port
- Check `Program.cs` CORS configuration
- Update client HTML with correct URL

### No Logs Appearing
- Check `appsettings.Development.json` log levels
- Make sure you're running with `ASPNETCORE_ENVIRONMENT=Development`

## Configuration Quick Reference

| Setting | File | Default |
|---------|------|---------|
| Concurrent limit | `PaymentProcessorWorker.cs` | 5 |
| Queue capacity | `PaymentChannelService.cs` | 1000 |
| Cleanup interval | `PaymentCleanupService.cs` | 5 min |
| Disconnect threshold | `PaymentCleanupService.cs` | 10 min |
| Log level | `appsettings.json` | Information |

## That's It!

You now have a fully functional payment processing API with:
- ✅ Rate limiting (max 5 concurrent)
- ✅ Real-time status updates (SSE)
- ✅ Disconnection handling
- ✅ Memory leak prevention
- ✅ Comprehensive logging

Enjoy! 🚀

