# Payment Rate Limiter API - Architecture Documentation

## Overview

A credit card payment processing API that uses **Azure Service Bus** for persistent, scalable queuing and a background worker with rate limiting (max 5 concurrent via ServiceBusProcessor MaxConcurrentCalls) that communicates status updates back to clients via Server-Sent Events (SSE).

## System Architecture

### Core Problem
Three different threads need to communicate:
1. **Client HTTP Thread** (Controller): Receives API requests, streams SSE responses
2. **Azure Service Bus Queue**: Persistent queue where payment requests are stored (survives restarts, scales across servers)
3. **Background Worker Thread(s)**: Process payments with rate limiting (via ServiceBusProcessor MaxConcurrentCalls) and send status updates

The challenge: Client thread must receive real-time status updates from the background worker thread.

### Solution: Per-Payment Status Channels

Uses `PaymentStatusService` as a thread-safe bridge with per-payment channels:

```
┌─────────────────────┐
│  Client Thread      │
│  (Controller)       │
│                     │
│  1. Register payment│──┐
│  2. Send to        │  │
│     Service Bus     │  │
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
        │  (Still needed for SSE)        │
        └────────────────────────────────┘
                         ▲
                         │
        ┌────────────────────────────────┐
        │  Azure Service Bus Queue       │
        │  (Persistent, Scalable)        │
        │                                │
        │  - Messages persist across     │
        │    restarts                    │
        │  - Scales across servers       │
        │  - Automatic message           │
        │    completion/cleanup          │
        └────────────────────────────────┘
                         ▲
                         │
┌─────────────────────┐  │
│ ServiceBusProcessor │  │
│ (Background Worker) │  │
│                     │  │
│ 1. Receive messages │  │
│    from Service Bus │  │
│ 2. MaxConcurrentCalls│ │
│    = 5 (built-in)   │  │
│ 3. Process payment  │  │
│ 4. Send status      │──┘
│    updates          │
│ 5. Complete/Abandon │
│    message          │
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

#### PaymentServiceBusService
- Wraps Azure Service Bus operations for payment processing
- Creates `ServiceBusClient`, `ServiceBusSender`, and `ServiceBusProcessor`
- `SendPaymentRequestAsync()`: Sends payment requests to Service Bus queue
- `Processor`: ServiceBusProcessor with MaxConcurrentCalls configured
- Handles message serialization/deserialization
- **Benefits**: Persistent queue, scales across servers, automatic cleanup

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

##### Per-Payment Channel Isolation Architecture

**Why Each Payment Gets Its Own Channel**

The `PaymentStatusService` uses a `ConcurrentDictionary<string, Channel<PaymentStatus>>` to store channels in memory, where each `PaymentId` maps to its own dedicated channel. This design ensures **message isolation** - status updates for a specific payment are delivered only to the client that initiated that payment.

**How It Works:**

1. **Channel Creation (Per Payment)**:
   ```csharp
   // When a payment is registered:
   RegisterPayment(paymentRequest) 
   → Creates Channel<PaymentStatus> for PaymentId "abc-123"
   → Stores in ConcurrentDictionary: { "abc-123" → Channel A }
   ```

2. **Worker Sends Status (PaymentId-Based Routing)**:
   ```csharp
   // Worker processes payment "abc-123":
   SendStatusAsync("abc-123", status)
   → Looks up channel in dictionary: _statusChannels["abc-123"]
   → Writes to Channel A ONLY
   → Message goes to ONLY the listener for PaymentId "abc-123"
   ```

3. **Client Reads Status (PaymentId-Based Reader)**:
   ```csharp
   // Client gets reader for their specific payment:
   GetStatusReader("abc-123")
   → Returns Channel A's reader
   → Client reads ONLY messages for PaymentId "abc-123"
   ```

**Key Design Principles:**

- **One-to-One Mapping**: 1 Payment = 1 Channel = 1 Client Connection
- **Memory Storage**: `ConcurrentDictionary` holds all channels in memory, keyed by `PaymentId`
- **Isolated Communication**: Getting a reader based on `PaymentId` ensures messages are sent to ONLY the specific listener for that payment
- **Thread-Safe**: `ConcurrentDictionary` provides thread-safe access from multiple worker threads
- **Privacy & Security**: Clients only see status updates for their own payment, not others

**Example with Multiple Concurrent Payments:**

```
ConcurrentDictionary State:
{
  "abc-123" → Channel A  (Payment A)
  "def-456" → Channel B  (Payment B)
  "ghi-789" → Channel C  (Payment C)
}

Worker Thread 1: SendStatusAsync("abc-123", status)
  → Writes to Channel A
  → Client 1 (Payment A) receives message ✅
  → Client 2 (Payment B) receives nothing ✅
  → Client 3 (Payment C) receives nothing ✅

Worker Thread 2: SendStatusAsync("def-456", status)
  → Writes to Channel B
  → Client 1 (Payment A) receives nothing ✅
  → Client 2 (Payment B) receives message ✅
  → Client 3 (Payment C) receives nothing ✅
```

**Why Not a Single Shared Channel?**

If all payments shared one channel:
- ❌ **Privacy Issue**: Clients would see other payments' statuses
- ❌ **Filtering Overhead**: Clients would need to filter messages by PaymentId
- ❌ **No Isolation**: One client's disconnection could affect others
- ❌ **Complexity**: Would require message routing logic

**Benefits of Per-Payment Channels:**

- ✅ **Isolation**: Each payment's status updates are completely isolated
- ✅ **Privacy**: Clients only see their own payment status
- ✅ **Simplicity**: No filtering needed - each channel is dedicated to one payment
- ✅ **Concurrency**: Multiple payments can process simultaneously without interference
- ✅ **Cleanup**: When a payment completes, only its channel is cleaned up

#### PaymentProcessorWorker (BackgroundService)
Rate-limited background processor using Azure Service Bus:
- Uses `ServiceBusProcessor` to receive messages from Service Bus queue
- **MaxConcurrentCalls = 5**: Built-in rate limiting (no manual semaphore needed)
- Processes messages via `ProcessMessageAsync` event handler
- Checks `IsPaymentCancelled()` at multiple checkpoints:
  - Before starting processing
  - After queuing delay
  - **Before calling third-party processor** (critical!)
- Sends status updates via `PaymentStatusService`
- Completes or abandons messages based on processing result
- **Benefits**: Automatic message locking, retry handling, dead letter queue support

**Note**: PaymentCleanupService was removed - Service Bus handles message lifecycle automatically (completion, expiration, dead letter queue)

### 3. Controller

#### PaymentController
Handles SSE streaming to clients:
- `POST /api/payment/process`: Single endpoint for payment processing
- Sets `Content-Type: text/event-stream` header
- Creates `PaymentRequest` object with metadata
- Registers payment and sends to Azure Service Bus queue
- **Monitors `HttpContext.RequestAborted`** for disconnection
- Streams status updates in SSE format: `data: {json}\n\n`
- Handles cancellation gracefully
- Ensures cleanup in finally block (status channels only - Service Bus handles queue cleanup)

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

## Cleanup Strategy - Simplified with Service Bus

### Service Bus Handles Queue Cleanup Automatically

**Key Change**: With Azure Service Bus, queue message cleanup is handled automatically:
- ✅ Messages are completed after successful processing
- ✅ Messages are abandoned/redelivered on failure
- ✅ Dead letter queue for permanently failed messages
- ✅ Message TTL for automatic expiration
- ✅ No manual queue cleanup needed

### Status Channel Cleanup (Still Required)

**Why**: Service Bus handles the queue, but we still need to clean up in-memory status channels used for SSE communication.

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
- **Service Bus message is abandoned** (will redeliver or go to dead letter queue)

**Reasoning**:
- No point keeping channel open if no reader exists
- Frees memory immediately
- Worker checks before expensive operations
- Service Bus handles message retry/cleanup automatically

### Level 3: Periodic Cleanup (Optional - Status Channels Only)
**When**: Can be run periodically if needed
**Action**:
- Scan all payment metadata (status channels only)
- Remove records older than threshold:
  - Disconnected > 10 minutes
  - Any payment > 20 minutes (safety net)
- **Note**: Service Bus queue cleanup is automatic, this only cleans status channels

**Reasoning**:
- Catches orphaned status channels from edge cases
- Acts as safety net for memory leaks in status channel dictionary
- Service Bus handles queue messages automatically

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

T+0s    Client          Send to Service Bus         _serviceBusService.SendPaymentRequestAsync(request)

T+0s    Client          Wait for status             await foreach (statusReader.ReadAllAsync())
                        [BLOCKED - waiting]         

T+0s    Worker          Receive from Service Bus    ServiceBusProcessor.ProcessMessageAsync()
                        - Deserialize PaymentRequest    

T+0s    Worker          Check cancellation          IsPaymentCancelled() = false ✓

T+0s    Worker          MaxConcurrentCalls check     ServiceBusProcessor [4 slots available]

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

T+8s    Worker          Complete message            args.CompleteMessageAsync() [Message removed]

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

T+6s    Worker          Abandon message             args.AbandonMessageAsync()
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

### ServiceBusProcessor MaxConcurrentCalls
```csharp
// In PaymentServiceBusService.cs
var processorOptions = new ServiceBusProcessorOptions
{
    MaxConcurrentCalls = 5,  // Limits concurrent message processing
    AutoCompleteMessages = false
};
```

### How It Works

**Where It's Configured**: In `PaymentServiceBusService.cs` when creating the `ServiceBusProcessor`.

**Where It's Enforced**: Inside the `ServiceBusProcessor` class from the Azure SDK (not in your code). The processor internally manages concurrency.

**How It Works**:
- **ServiceBusProcessor automatically limits** concurrent message handlers to 5
- No manual semaphore needed - built into the processor
- When MaxConcurrentCalls=5, only 5 messages are processed concurrently
- Additional messages wait in the Service Bus queue until a slot is available
- Message locking prevents duplicate processing across multiple servers

### Example: 100 Messages with MaxConcurrentCalls = 5

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

### Internal Mechanism (Azure SDK)

The `ServiceBusProcessor` from the Azure SDK internally manages concurrency similar to this (simplified):

```csharp
// Inside ServiceBusProcessor (Azure SDK code, not yours)
private int _activeHandlerCount = 0;
private readonly SemaphoreSlim _concurrencySemaphore;

// When StartProcessingAsync() is called:
while (!cancellationToken.IsCancellationRequested)
{
    // Wait if we're at max concurrent calls
    await _concurrencySemaphore.WaitAsync();
    
    // Receive message from Service Bus
    var message = await ReceiveMessageAsync();
    
    // Increment active count
    Interlocked.Increment(ref _activeHandlerCount);
    
    // Fire your handler (but don't await it - fire and forget)
    _ = Task.Run(async () =>
    {
        try
        {
            await ProcessMessageAsync?.Invoke(args);  // Your handler
        }
        finally
        {
            // Decrement and release semaphore when done
            Interlocked.Decrement(ref _activeHandlerCount);
            _concurrencySemaphore.Release();  // Frees slot for next message
        }
    });
}
```

**Key Points**:
- The limiting happens **inside the Azure SDK**, not in your code
- Messages stay in the Service Bus queue until a handler slot is available
- No manual semaphore needed - the processor manages it automatically
- FIFO order is maintained - messages are processed in order

### Comparison: Old Semaphore vs. Service Bus MaxConcurrentCalls

**Old Approach (SemaphoreSlim):**
```csharp
// Your code had to manage this:
await _semaphore.WaitAsync();  // Your code controls this
try {
    await ProcessPaymentAsync(...);
} finally {
    _semaphore.Release();  // Your code controls this
}
```

**New Approach (ServiceBusProcessor):**
```csharp
// ServiceBusProcessor manages this internally:
processor.ProcessMessageAsync += async args => {
    // This handler is only called when a slot is available
    // The processor tracks: "How many handlers are running?"
    // If count >= MaxConcurrentCalls, it waits
    await ProcessServiceBusMessageAsync(args, stoppingToken);
    // When this completes, processor automatically starts next handler
};
```

### Why 5 Concurrent?
- Third-party processor limits
- Resource constraints (memory, connections)
- Quality of service (prevent overload)
- Configurable via `AzureServiceBusOptions.MaxConcurrentCalls`

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
- **MaxConcurrentCalls**: Default 5 (via `AzureServiceBusOptions`)
- **Service Bus Connection String**: Required in `appsettings.json`
- **Queue Name**: Default "payment-requests"
- **AutoCompleteMessages**: Default false (manual completion for better control)
- **MaxAutoLockRenewalDuration**: Default 5 minutes
- **Status channel cleanup**: Delayed cleanup (5 seconds) for completed payments

### Azure Service Bus Configuration
```json
{
  "AzureServiceBus": {
    "ConnectionString": "Endpoint=sb://...",
    "QueueName": "payment-requests",
    "MaxConcurrentCalls": 5,
    "AutoCompleteMessages": false,
    "MaxAutoLockRenewalDuration": "00:05:00"
  }
}
```

## Service Registration

```csharp
// Program.cs
builder.Services.Configure<AzureServiceBusOptions>(
    builder.Configuration.GetSection("AzureServiceBus"));
builder.Services.AddSingleton<PaymentServiceBusService>();
builder.Services.AddSingleton<PaymentStatusService>();
builder.Services.AddHostedService<PaymentProcessorWorker>();
// PaymentCleanupService removed - Service Bus handles queue cleanup automatically
```

## Security Considerations

- Validate payment data before queuing
- Use tokenized card data (never raw card numbers)
- Rate limit per IP address (DoS protection)
- Authentication/authorization on endpoints
- Logging for audit trail
- CORS configuration for web clients

## Future Enhancements

- ✅ **Persistent queue**: Implemented via Azure Service Bus (survives restarts)
- ✅ **Distributed rate limiting**: Implemented via Service Bus (works across multiple servers)
- ✅ **Dead letter queue**: Available via Service Bus configuration
- Payment retry logic (can use Service Bus retry policies)
- Redis-backed status channels (for multi-server SSE scaling)
- Webhook callbacks as alternative to SSE
- Payment status query endpoint (polling alternative)
- Service Bus sessions for FIFO ordering (if needed)

