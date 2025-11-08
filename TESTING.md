# Testing Guide for Payment Rate Limiter API

This guide provides step-by-step instructions for testing all features of the payment processing system.

## Quick Start

1. **Start the API**:
   ```bash
   dotnet run
   ```

2. **Open Swagger UI**: Navigate to `https://localhost:7000/swagger`

3. **Open the HTML Client**: Open `ClientExamples/vanilla-javascript-client.html` in your browser

## Test Scenarios

### 1. Basic Payment Processing (Happy Path)

**Goal**: Verify payment completes successfully with status updates

**Steps**:
1. Open the HTML client or use Swagger
2. Submit a payment with:
   - Amount: `99.99`
   - Card Token: `tok_demo_123456`
   - Email: `customer@example.com`
3. Observe the status updates in real-time:
   - Initial message with Payment ID
   - "Queued"
   - "Processing"
   - "SendingToProcessor"
   - "Completed"

**Expected Result**: 
- All status messages appear in order
- Total processing time: ~8 seconds
- Connection closes after "Completed"

**Console Logs to Check**:
```
Payment {PaymentId} acquired processing slot. Amount: 99.99
Payment {PaymentId} completed successfully. Amount: 99.99
```

---

### 2. Rate Limiting (Concurrency Control)

**Goal**: Verify only 5 payments process concurrently

**Steps**:
1. Open 10+ browser tabs with the HTML client
2. Click "Process Payment" in all tabs as quickly as possible
3. Watch the console logs

**Expected Result**:
- Only 5 log messages show "acquired processing slot" initially
- Others wait until a slot becomes available
- As payments complete, new ones start processing
- All eventually complete successfully

**Console Logs to Check**:
```
Payment {Id1} acquired processing slot  <-- First 5
Payment {Id2} acquired processing slot
Payment {Id3} acquired processing slot
Payment {Id4} acquired processing slot
Payment {Id5} acquired processing slot
Payment {Id1} completed successfully
Payment {Id1} released processing slot
Payment {Id6} acquired processing slot  <-- Next one starts
```

**Timing**:
- First 5: Start immediately
- Next 5: Start after ~8 seconds (when first batch completes)
- Last batch: Start after ~16 seconds

---

### 3. Client Disconnection Handling

**Goal**: Verify system cancels payment when client disconnects

**Steps**:
1. Start a payment using the HTML client
2. Immediately click "Cancel Payment" button (or close the browser tab)
3. Check the console logs

**Expected Result**:
- Payment is marked as disconnected
- Worker skips processing: "Skipping cancelled payment"
- No "completed successfully" message
- Semaphore slot is released immediately

**Console Logs to Check**:
```
Client disconnected for payment {PaymentId} after XXXms. Payment marked as cancelled to prevent charging.
Payment {PaymentId} released processing slot
```

**Important**: No charge should be made to the third-party processor!

---

### 4. Client Disconnection During Queue Wait

**Goal**: Verify disconnection is detected before payment starts processing

**Steps**:
1. Start 6+ payments simultaneously (to fill the 5 slots)
2. For payment #6 (which is waiting in queue), close the browser tab
3. Wait and observe logs

**Expected Result**:
- Payment #6 is marked as disconnected
- When it's time to process, worker detects cancellation
- Log shows: "Skipping cancelled payment {PaymentId} (disconnected before processing)"
- No processing occurs

**Console Logs to Check**:
```
Client disconnected for payment {PaymentId6} after XXXms
Skipping cancelled payment {PaymentId6} (disconnected before processing)
```

---

### 5. Multiple Status Updates Flow

**Goal**: Verify all status messages reach the client

**Steps**:
1. Use the HTML client to process a payment
2. Count the number of status updates displayed

**Expected Result**:
1. Initial: "Payment request received" (with Payment ID)
2. "Queued" - Payment queued and processing slot acquired
3. "Processing" - Processing payment details
4. "SendingToProcessor" - Sending payment to third-party processor
5. "Completed" - Payment of $99.99 completed successfully

**Total**: 5 messages (1 initial + 4 status updates)

---

### 6. Statistics Endpoint

**Goal**: Verify monitoring endpoint returns accurate data

**Steps**:
1. Start 3 payments and let them complete
2. Start 2 more payments, then disconnect them
3. Call: `GET https://localhost:7000/api/payment/status/stats`

**Expected Result**:
```json
{
  "activePayments": 0,
  "disconnectedPayments": 2,
  "totalInMemory": 2,
  "timestamp": "2025-11-06T..."
}
```

**Note**: Completed payments are cleaned up after 5 seconds, so they won't appear in the stats.

---

### 7. Periodic Cleanup Service

**Goal**: Verify stale records are cleaned up automatically

**Steps**:
1. Start a payment and disconnect immediately
2. Wait 11+ minutes (disconnected threshold is 10 minutes)
3. Check logs for cleanup message

**Expected Result**:
```
Periodic cleanup completed. Removed 1 stale payment record(s)
```

**To test faster** (for development):
- Edit `PaymentCleanupService.cs`
- Change `_disconnectedThreshold = TimeSpan.FromMinutes(10)` to `TimeSpan.FromSeconds(30)`
- Change `_cleanupInterval = TimeSpan.FromMinutes(5)` to `TimeSpan.FromSeconds(15)`

---

### 8. Error Handling

**Goal**: Verify system handles invalid input gracefully

**Test Cases**:

**a) Invalid Amount (Negative)**:
```json
{
  "amount": -10.00,
  "cardToken": "tok_demo_123456"
}
```
Expected: Validation error (add validation attributes to model in production)

**b) Missing Card Token**:
```json
{
  "amount": 99.99,
  "cardToken": ""
}
```
Expected: Validation error

**c) Very Large Amount**:
```json
{
  "amount": 999999999.99,
  "cardToken": "tok_demo_123456"
}
```
Expected: Processes successfully (or add business rules to reject)

---

### 9. Stress Test (Queue Capacity)

**Goal**: Test bounded channel behavior with 1000+ requests

**Steps**:
1. Use a script to send 1100 requests simultaneously
2. Observe behavior

**Expected Result**:
- First 1000 are queued immediately (channel capacity)
- Request 1001+ will wait until channel has space (backpressure)
- All eventually complete successfully
- No crashes or errors

**Script** (PowerShell):
```powershell
for ($i=1; $i -le 1100; $i++) {
    Start-Job {
        Invoke-RestMethod -Uri "https://localhost:7000/api/payment/process" `
            -Method Post `
            -ContentType "application/json" `
            -Body '{"amount":1.00,"cardToken":"tok_test"}'
    }
}
```

---

### 10. Application Shutdown

**Goal**: Verify graceful shutdown with in-flight payments

**Steps**:
1. Start 5 payments
2. Press Ctrl+C to stop the application
3. Observe shutdown behavior

**Expected Result**:
- In-progress payments are cancelled gracefully
- Semaphores are released
- No exceptions or crashes
- Log messages show: "Payment {PaymentId} cancelled due to application shutdown"

---

## Performance Benchmarks

### Expected Processing Times

| Scenario | Time |
|----------|------|
| Single payment (no queue) | ~8 seconds |
| 5 concurrent payments | ~8 seconds |
| 10 concurrent payments | ~16 seconds (two batches) |
| 100 concurrent payments | ~160 seconds (20 batches) |

### Memory Usage

- Per payment in memory: ~1-2 KB (including channel and metadata)
- 1000 active payments: ~1-2 MB
- Clean up happens automatically

---

## Debugging Tips

### Enable Detailed Logging

Edit `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "PaymentChannelDemo": "Trace"
    }
  }
}
```

### Watch Logs in Real-Time

```bash
dotnet run | grep "Payment"
```

### Check Statistics Continuously

```bash
# PowerShell
while ($true) { 
    Invoke-RestMethod https://localhost:7000/api/payment/status/stats
    Start-Sleep -Seconds 5
}
```

---

## Common Issues

### Issue: "Client disconnected" but payment still completes

**Cause**: Disconnection detected after "SendingToProcessor" status
**Solution**: This is expected. The check happens before the processor call, not during it.

### Issue: Status updates not appearing in browser

**Cause**: 
1. CORS not configured
2. Wrong API URL in client
3. Browser security blocking localhost

**Solution**: 
1. Check CORS in `Program.cs`
2. Update API URL in HTML client
3. Use `https://` not `http://` for localhost

### Issue: All requests waiting, none processing

**Cause**: Semaphore deadlock (shouldn't happen, but check)
**Solution**: Restart application, check logs for exceptions

---

## Automated Testing (Future)

Consider adding:
- Unit tests for `PaymentStatusService`
- Integration tests for full payment flow
- Load tests with k6 or JMeter
- Chaos engineering (random disconnects)

Example xUnit test structure:
```csharp
[Fact]
public async Task ProcessPayment_ClientDisconnects_PaymentCancelled()
{
    // Arrange
    var cts = new CancellationTokenSource();
    
    // Act
    var task = ProcessPaymentAsync(request, cts.Token);
    cts.Cancel(); // Simulate disconnect
    
    // Assert
    Assert.True(statusService.IsPaymentCancelled(paymentId));
}
```

---

## Production Checklist

Before deploying to production:

- [ ] Replace simulated processor with real API (Stripe, Square, etc.)
- [ ] Add authentication/authorization
- [ ] Add input validation
- [ ] Set up monitoring/alerts
- [ ] Load test with expected traffic
- [ ] Test failover scenarios
- [ ] Review security (no sensitive data in logs)
- [ ] Add rate limiting per user/IP
- [ ] Set up distributed tracing
- [ ] Configure production-grade logging (Serilog, etc.)

