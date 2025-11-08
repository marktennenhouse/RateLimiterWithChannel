# Payment Rate Limiter API - Architecture Documentation

## Overview

A credit card payment processing API that uses Channels for queuing requests and a background worker with rate limiting (max 5 concurrent) that communicates status updates back to clients via Server-Sent Events (SSE).

## System Architecture

### Core Problem
Three different threads need to communicate:
1. **Client HTTP Thread** (Controller): Receives API requests, streams SSE responses
2. **Main Payment Queue** (Channel): Shared queue where payment requests are stored
3. **Background Worker Thread(s)**: Process payments with rate limiting and send status updates

The challenge: Client thread must receive real-time status updates from the background worker thread.

### Solution: Per-Payment Status Channels

Uses `PaymentStatusService` as a thread-safe bridge with per-payment channels:

```
┌─────────────────────┐
│  Client Thread      │
│  (Controller)       │
│                     │
│  1. Register payment│──┐
│  2. Write to queue  │  │
│  3. Read status     │  │
│  4. Stream via SSE  │◄─┼─────┐
└─────────────────────┘  │     │
                         │     │
                         ▼     │
        ┌────────────────────────────────┐
        │  PaymentStatusService          │
        │  (Thread-Safe Singleton)       │
        │                                │
        │  ConcurrentDictionary<         │
        │    paymentId,                  │
        │    Channel<PaymentStatus>      │
        │  >                             │
        │                                │
        │  Each payment gets its own     │
        │  status communication channel  │
        └────────────────────────────────┘
                         ▲
                         │
┌─────────────────────┐  │
│ Background Worker   │  │
│ Thread Pool         │  │
│                     │  │
│ 1. Read from queue  │  │
│ 2. Rate limit (5)   │  │
│ 3. Process payment  │  │
│ 4. Send status      │──┘
│    updates          │
└─────────────────────┘
```

## Component Details

### 1. Models

#### PaymentRequest
Enhanced payment object (not just string ID):
```csharp
- PaymentId: string (GUID)
- Amount: decimal
- CardToken: string (tokenized card data)
- QueuedAt: DateTime (when added to queue)
- IsDisconnected: bool (client disconnected?)
- DisconnectedAt: DateTime? (when disconnection occurred)
- ClientIpAddress: string (for logging/debugging)
- RetryCount: int (for future retry logic)
```

#### PaymentStatus
Status message object sent from worker to client:
```csharp
- PaymentId: string
- Status: PaymentStatusEnum (Queued, Processing, SendingToProcessor, Completed, Failed)
- Message: string (human-readable status)
- QueuePosition: int? (position in queue when queued)
- Timestamp: DateTime
```

### 2. Services

#### PaymentChannelService
- Manages main payment queue using `Channel<PaymentRequest>`
- Uses **bounded** channel (capacity: 1000) for backpressure protection
- Exposes `Writer` and `Reader` properties
- Thread-safe by design (Channel handles synchronization)

#### PaymentStatusService
**Critical component** - bridges worker threads to client threads:
- `ConcurrentDictionary<string, Channel<PaymentStatus>>`: per-payment status channels
- `ConcurrentDictionary<string, PaymentMetadata>`: tracking disconnections and lifecycle
- `RegisterPayment(PaymentRequest)`: Creates status channel for new payment
- `SendStatusAsync(paymentId, status)`: Worker sends status updates (thread-safe)
- `GetStatusReader(paymentId)`: Client reads status updates
- `MarkDisconnected(paymentId)`: Marks payment as cancelled
- `IsPaymentCancelled(paymentId)`: Worker checks before processing
- `CleanupStalePayments(threshold)`: Periodic cleanup of orphaned records
- `GetStatistics()`: Monitoring endpoint data

#### PaymentProcessorWorker (BackgroundService)
Rate-limited background processor:
- Reads from `PaymentChannelService.Reader`
- Uses `SemaphoreSlim(5, 5)` to limit to 5 concurrent payments
- Spawns tasks for each payment (fire-and-forget with rate limiting)
- Checks `IsPaymentCancelled()` at multiple checkpoints:
  - Before starting processing
  - After queuing delay
  - **Before calling third-party processor** (critical!)
- Sends status updates via `PaymentStatusService`
- Releases semaphore in finally block (always frees slot)

#### PaymentCleanupService (BackgroundService)
Periodic cleanup of stale records:
- Runs every 5 minutes
- Removes payments older than 10 minutes (disconnected) or 20 minutes (any)
- Prevents memory leaks from orphaned status channels
- Logs cleanup statistics

### 3. Controller

#### PaymentController
Handles SSE streaming to clients:
- `POST /api/payment/process`: Single endpoint for payment processing
- Sets `Content-Type: text/event-stream` header
- Creates `PaymentRequest` object with metadata
- Registers payment and writes to queue
- **Monitors `HttpContext.RequestAborted`** for disconnection
- Streams status updates in SSE format: `data: {json}\n\n`
- Handles cancellation gracefully
- Ensures cleanup in finally block

## Client Disconnection Handling

### Why It's Critical
Without disconnection handling:
- ❌ Charge credit cards for users who closed their browser
- ❌ Waste processing resources on abandoned requests
- ❌ Memory leaks from orphaned status channels
- ❌ Semaphore slots occupied by dead requests

### Detection Mechanism
- `HttpContext.RequestAborted` cancellation token fires on:
  - Client closes connection
  - Network failure
  - Browser/tab closed
  - Client timeout

### Worker Response
Worker checks `IsPaymentCancelled()` at critical points:
1. **Before starting**: Immediately after dequeue
2. **After queuing delay**: Before expensive operations
3. **Before third-party call**: Most critical - prevents actual charge

### Early Exit Strategy
When disconnection detected:
- Payment marked as disconnected with timestamp
- Status channel closed
- Worker skips processing on next check
- Semaphore slot immediately available for next payment
- Cleanup scheduled

## Cleanup Strategy - Multi-Level Approach

### Why Multiple Cleanup Events?

**Problem**: We have competing concerns:
1. Client must receive ALL status messages (including final "Completed")
2. Memory must be freed promptly to prevent leaks
3. Must handle edge cases (crashes, missed cleanups)

**Solution**: Defense-in-depth with three cleanup levels

### Level 1: Immediate Cleanup (On Completion)
**When**: Payment reaches terminal state (Completed/Failed)
**Action**: 
- Send final status message
- Close status channel writer
- Schedule delayed removal (5 seconds)

**Reasoning**: 
- 5-second delay ensures client receives final message before dictionary removal
- SSE connection remains open for streaming
- Channel closure signals "no more messages" to reader

### Level 2: Disconnection Cleanup (On Cancel)
**When**: Client disconnects (`HttpContext.RequestAborted`)
**Action**:
- Mark payment as disconnected
- Close status channel immediately
- Update metadata with disconnection timestamp

**Reasoning**:
- No point keeping channel open if no reader exists
- Frees memory immediately
- Worker checks before expensive operations
- Full cleanup happens after processing attempt

### Level 3: Periodic Cleanup (Every 5 Minutes)
**When**: Background service runs on schedule
**Action**:
- Scan all payment metadata
- Remove records older than threshold:
  - Disconnected > 10 minutes
  - Any payment > 20 minutes (safety net)
- Log cleanup statistics

**Reasoning**:
- Catches orphaned records from edge cases:
  - Worker crashes before cleanup
  - Exceptions preventing normal cleanup
  - Race conditions
  - Unexpected code paths
- Acts as safety net for memory leaks
- Configurable thresholds for different load patterns

### Cleanup Lifecycle Example

```
Scenario A: Normal Completion
─────────────────────────────────────────────────────────
T+0s    Client connects                 Create channel + metadata
T+1s    Status: Queued                  Message sent via channel
T+3s    Status: Processing              Message sent via channel
T+8s    Status: Completed               Message sent, channel writer closed
T+13s   Delayed cleanup triggers        Remove from dictionaries
        → Level 1 cleanup successful

Scenario B: Client Disconnection
─────────────────────────────────────────────────────────
T+0s    Client connects                 Create channel + metadata
T+1s    Status: Queued                  Message sent (client receives)
T+5s    Client disconnects              Mark disconnected, close channel
        → Level 2 cleanup initiated
T+6s    Worker checks                   IsPaymentCancelled = true, skip processing
T+7s    Worker cleanup                  Remove from dictionaries
        → Level 2 cleanup completed

Scenario C: Orphaned Record (Edge Case)
─────────────────────────────────────────────────────────
T+0s    Client connects                 Create channel + metadata
T+1s    Worker crashes                  No cleanup executed
T+5min  Periodic cleanup runs           Detects stale record (> threshold)
        → Level 3 cleanup catches orphan
        Remove from dictionaries
        → Safety net successful
```

## Complete Payment Lifecycle

### Happy Path (Success)
```
Time    Thread          Event                       Data Structures
─────────────────────────────────────────────────────────────────────────
T+0s    Client          POST /api/payment/process   
                        - Create PaymentRequest     
                        - QueuedAt = now            

T+0s    Client          Register payment            _statusChannels[id] = new Channel
                                                    _paymentMetadata[id] = new Metadata

T+0s    Client          Write to queue              _paymentChannel.Writer.WriteAsync(request)

T+0s    Client          Wait for status             await foreach (statusReader.ReadAllAsync())
                        [BLOCKED - waiting]         

T+0s    Worker          Read from queue             _paymentChannel.Reader.ReadAllAsync()
                        - Dequeue PaymentRequest    

T+0s    Worker          Check cancellation          IsPaymentCancelled() = false ✓

T+0s    Worker          Acquire semaphore           _semaphore.WaitAsync() [4 slots left]

T+1s    Worker          Send status: Queued         _statusChannels[id].Writer.WriteAsync()

T+1s    Client          Receive status              [UNBLOCKED] yield return "Queued"
                        Stream via SSE              Response.WriteAsync("data: {...}\n\n")

T+2s    Worker          Check cancellation          IsPaymentCancelled() = false ✓

T+2s    Worker          Send status: Processing     _statusChannels[id].Writer.WriteAsync()

T+2s    Client          Receive status              yield return "Processing"
                        Stream via SSE              

T+2s    Worker          Check cancellation          IsPaymentCancelled() = false ✓
                        (before processor call)     

T+3s    Worker          Call third-party API        await ThirdPartyProcessor.ChargeAsync()
                        [Simulate: 5 second delay]  

T+8s    Worker          Send status: Completed      _statusChannels[id].Writer.WriteAsync()
                                                    channel.Writer.Complete()

T+8s    Client          Receive status              yield return "Completed"
                        Stream via SSE              
                        End of stream               ReadAllAsync() completes

T+8s    Client          Close connection            Response completes
                        Finally block               Cleanup(paymentId)

T+8s    Worker          Release semaphore           _semaphore.Release() [5 slots available]

T+13s   Delayed         Remove from memory          _statusChannels.Remove(id)
        Cleanup                                     _paymentMetadata.Remove(id)
```

### Disconnection Path
```
Time    Thread          Event                       Data Structures
─────────────────────────────────────────────────────────────────────────
T+0s    Client          POST /api/payment/process   [Same as above]
T+0s    Client          Register + Queue            [Same as above]
T+0s    Worker          Dequeue + Acquire           [Same as above]
T+1s    Worker→Client   Status: Queued              [Same as above]

T+5s    Client          USER CLOSES BROWSER         
                        HttpContext.RequestAborted  
                        fires                       

T+5s    Client          Catch cancellation          OperationCanceledException
                        Mark disconnected           request.IsDisconnected = true
                                                    request.DisconnectedAt = now
                                                    _statusService.MarkDisconnected(id)

T+5s    Client          Close channel               _statusChannels[id].Writer.Complete()
                        Update metadata             metadata.IsDisconnected = true

T+5s    Client          Connection closed           Response ends, finally executes

T+6s    Worker          Check cancellation          IsPaymentCancelled() = true ✗
                        SKIP processing             No third-party call made ✓

T+6s    Worker          Release semaphore           _semaphore.Release()
                        Early exit                  return; (no completion status sent)

T+6s    Worker          Cleanup                     _statusChannels.Remove(id)
                                                    _paymentMetadata.Remove(id)

T+10min Periodic        Scan for stale              [Already cleaned, nothing found]
        Cleanup         
```

### Edge Case: Orphaned Record
```
Time    Thread          Event                       Data Structures
─────────────────────────────────────────────────────────────────────────
T+0s    Client          POST /api/payment/process   
T+0s    Client          Register payment            _statusChannels[id] = Channel
                                                    _paymentMetadata[id] = Metadata

T+0s    Worker          Dequeue                     
T+0s    Worker          CRASH / EXCEPTION           No cleanup executed!

T+0s    [Orphaned]      Leaked state                _statusChannels[id] still exists
                                                    _paymentMetadata[id] still exists
                                                    Memory leak!

T+5min  Cleanup         Periodic scan runs          
        Service         Check metadata              metadata.CreatedAt = T+0s
                        Now - CreatedAt > 20min?    No, skip

T+10min Cleanup         Periodic scan runs          
        Service         Check metadata              metadata.CreatedAt = T+0s
                        Now - CreatedAt > 20min?    No, skip

T+20min Cleanup         Periodic scan runs          
        Service         Check metadata              metadata.CreatedAt = T+0s
                        Now - CreatedAt > 20min?    Yes! Remove
                        Remove from memory          _statusChannels.Remove(id) ✓
                                                    _paymentMetadata.Remove(id) ✓
                        Log cleanup                 "Cleaned up 1 stale record"
```

## Rate Limiting

### Semaphore Pattern
```csharp
private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(5, 5);

await _semaphore.WaitAsync();  // Blocks if 5 payments already processing
try {
    // Process payment (only 5 concurrent)
} finally {
    _semaphore.Release();  // Always release, even on disconnect/error
}
```

### Why 5 Concurrent?
- Third-party processor limits
- Resource constraints (memory, connections)
- Quality of service (prevent overload)
- Configurable for different environments

## Monitoring & Debugging

### Statistics Endpoint
`GET /api/payment/status/stats`

Returns:
```json
{
  "activePayments": 12,
  "disconnectedPayments": 3,
  "totalInMemory": 15,
  "timestamp": "2025-11-06T10:30:00Z"
}
```

### Logging Strategy
- Client connections/disconnections
- Queue positions
- Cancellation events
- Third-party API calls
- Cleanup operations
- Exception handling

## Server-Sent Events (SSE)

### Format
```
HTTP/1.1 200 OK
Content-Type: text/event-stream
Cache-Control: no-cache
Connection: keep-alive

data: {"paymentId":"abc123","status":"Queued","message":"Payment queued at position 3"}\n\n
data: {"paymentId":"abc123","status":"Processing","message":"Processing payment"}\n\n
data: {"paymentId":"abc123","status":"Completed","message":"Payment completed successfully"}\n\n
```

### Client Consumption

**JavaScript**:
```javascript
const eventSource = new EventSource('/api/payment/process');
eventSource.onmessage = (event) => {
    const status = JSON.parse(event.data);
    console.log(status.message);
};
```

**Angular**:
```typescript
processPayment(id: string): Observable<PaymentStatus> {
  return new Observable(observer => {
    const es = new EventSource(`/api/payment/process/${id}`);
    es.onmessage = (e) => observer.next(JSON.parse(e.data));
    es.onerror = (e) => { es.close(); observer.error(e); };
    return () => es.close();
  });
}
```

## Configuration

### Configurable Parameters
- Semaphore limit (concurrent payments): Default 5
- Channel capacity (queue size): Default 1000
- Cleanup interval: Default 5 minutes
- Stale threshold: Default 10 minutes (disconnected), 20 minutes (any)
- Delayed cleanup: Default 5 seconds

## Service Registration

```csharp
// Program.cs
builder.Services.AddSingleton<PaymentChannelService>();
builder.Services.AddSingleton<PaymentStatusService>();
builder.Services.AddHostedService<PaymentProcessorWorker>();
builder.Services.AddHostedService<PaymentCleanupService>();
```

## Security Considerations

- Validate payment data before queuing
- Use tokenized card data (never raw card numbers)
- Rate limit per IP address (DoS protection)
- Authentication/authorization on endpoints
- Logging for audit trail
- CORS configuration for web clients

## Future Enhancements

- Persistent queue (survive restarts)
- Payment retry logic
- Dead letter queue for failed payments
- Distributed rate limiting (multiple servers)
- Redis-backed status channels (scale-out)
- Webhook callbacks as alternative to SSE
- Payment status query endpoint (polling alternative)

