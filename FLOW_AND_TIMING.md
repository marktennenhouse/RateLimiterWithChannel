# Payment Processing Flow and Timing Documentation

This document details the exact sequence of events, timing, and data flow for the payment processing system.

## Complete Event Timeline

### Example: Single Payment Processing (No Queue)

```
Time    Thread          Event                           Data Flow
─────────────────────────────────────────────────────────────────────────────
T+0ms   Client          POST /api/payment/process      
                        Body: {amount: 99.99, ...}     HTTP Request
                        ↓
T+0ms   Controller      Create PaymentRequest          
                        - PaymentId = GUID             PaymentRequest object
                        - QueuedAt = now               
                        - Amount = 99.99                
                        ↓
T+0ms   Controller      RegisterPayment()              
                        Creates status channel          ConcurrentDictionary
                        Creates metadata                + Channel<PaymentStatus>
                        ↓
T+1ms   Controller      Send initial SSE message        
                        "Payment request received"      HTTP Response (SSE)
                        Flush response                  Stream: data: {...}\n\n
                        ↓
T+2ms   Controller      Send to Service Bus queue        
                        _serviceBusService              Service Bus Queue
                        SendPaymentRequestAsync()        Message queued (persistent)
                        ↓
T+3ms   Controller      Get status reader              
                        _statusService.GetStatusReader() ChannelReader<PaymentStatus>
                        ↓
T+4ms   Controller      Start ReadAllAsync()           
                        await foreach (status...)      [BLOCKED - waiting]
                        ↓
                        ─────────────────────────────────────────────────────
T+5ms   Worker          Receive from Service Bus        
                        ServiceBusProcessor             ProcessMessageAsync fires
                        ProcessMessageAsync()            Deserialize PaymentRequest
                        ↓
T+6ms   Worker          SendStatusAsync()              
                        "Payment dequeued, waiting..."  ChannelWriter.WriteAsync()
                        Status written to channel      Channel<PaymentStatus>
                        ↓
T+7ms   Controller      [UNBLOCKED]                    
                        ReadAllAsync() yields           Receive status from channel
                        status = {Status: Queued, ...}  
                        ↓
T+8ms   Controller      Serialize status                
                        JsonSerializer.Serialize()      JSON: {"status":"Queued",...}
                        ↓
T+9ms   Controller      Write to HTTP response          
                        Response.WriteAsync()            Stream: data: {...}\n\n
                        Flush response                  Client receives update
                        ↓
                        ─────────────────────────────────────────────────────
T+10ms  Worker          MaxConcurrentCalls check        
                        ServiceBusProcessor             [Only 5 concurrent allowed]
                        (In this case: immediate)      Message handler starts
                        ↓
T+12ms  Worker Task     ProcessPaymentAsync starts      
                        Check if cancelled              
                        (Not cancelled)                 
                        ↓
T+13ms  Worker Task     SendStatusAsync()               
                        "Processing slot acquired..."   ChannelWriter.WriteAsync()
                        ↓
T+14ms  Controller      [UNBLOCKED]                    
                        ReadAllAsync() yields           Receive status
                        Stream to client                Client sees update
                        ↓
T+15ms  Worker Task     Task.Delay(1000ms)              
                        Simulate processing             [WAITING 1 second]
                        ↓
                        ─────────────────────────────────────────────────────
T+1015ms Worker Task    Check if cancelled              
                        (Not cancelled)                 
                        ↓
T+1016ms Worker Task    SendStatusAsync()               
                        "Processing payment details"    ChannelWriter.WriteAsync()
                        ↓
T+1017ms Controller      [UNBLOCKED]                    
                        ReadAllAsync() yields           Receive status
                        Stream to client                Client sees update
                        ↓
T+1018ms Worker Task    Task.Delay(2000ms)              
                        Continue processing            [WAITING 2 seconds]
                        ↓
                        ─────────────────────────────────────────────────────
T+3018ms Worker Task    CRITICAL CHECK                  
                        IsPaymentCancelled()            Before processor call
                        (Not cancelled)                 
                        ↓
T+3019ms Worker Task    SendStatusAsync()               
                        "Sending to processor"          ChannelWriter.WriteAsync()
                        ↓
T+3020ms Controller      [UNBLOCKED]                    
                        ReadAllAsync() yields           Receive status
                        Stream to client                Client sees update
                        ↓
T+3021ms Worker Task    Task.Delay(5000ms)              
                        Simulate third-party API        [WAITING 5 seconds]
                        ↓
                        ─────────────────────────────────────────────────────
T+8021ms Worker Task    SendStatusAsync()               
                        "Payment completed"             ChannelWriter.WriteAsync()
                        Status = Completed              
                        ↓
T+8022ms Worker Task    Channel.Writer.Complete()       
                        Close channel writer            Signals "no more data"
                        ↓
T+8023ms Controller      [UNBLOCKED]                    
                        ReadAllAsync() yields           Receive final status
                        Stream to client                Client sees "Completed"
                        ↓
T+8024ms Controller      ReadAllAsync() completes      
                        Channel closed, loop ends       [UNBLOCKED - done]
                        ↓
T+8025ms Controller      Stream completed normally      
                        Connection closes               HTTP Response ends
                        ↓
T+8026ms Worker Task     Complete message                
                        args.CompleteMessageAsync()     Message removed from queue
                        ↓
T+8027ms Delayed         Cleanup scheduled              
                        Task.Delay(5000)                Cleanup in 5 seconds
                        ↓
                        ─────────────────────────────────────────────────────
T+13027ms Cleanup        Remove from memory            
                        Cleanup(paymentId)              Remove from dictionaries
```

## Total Processing Time: ~8 seconds

- **Initial response**: <10ms
- **First status update**: <10ms  
- **Processing delays**: 1s + 2s + 5s = 8s
- **Total**: ~8.03 seconds

---

## Flow with Queue (5 Slots Busy)

### Scenario: 6th Payment Arrives When All 5 Slots Are Busy

```
Time    Payment   Event                           Status
─────────────────────────────────────────────────────────────────────────────
T+0ms   P6        POST /api/payment/process      Request received
T+1ms   P6        Registered, queued             "Payment request received"
T+2ms   P6        Written to channel             In channel queue
T+3ms   P6        Start ReadAllAsync()           [WAITING for status]
        ─────────────────────────────────────────────────────────────────────
T+4ms   Worker    Dequeue P6 from channel       Payment dequeued
T+5ms   Worker    SendStatusAsync()              
                  "Payment dequeued, waiting..." Client sees: "waiting..."
T+6ms   Worker    Wait for semaphore             [BLOCKED - all 5 slots busy]
        ─────────────────────────────────────────────────────────────────────
        [P1-P5 are processing, each takes ~8 seconds]
        ─────────────────────────────────────────────────────────────────────
T+8000ms P1       Completes                      
                  Semaphore.Release()            Slot freed
T+8001ms Worker   [UNBLOCKED]                    
                  Semaphore acquired for P6      Slot acquired
T+8002ms Worker   Create Task.Run()              Task created for P6
T+8003ms Worker   SendStatusAsync()              
                  "Processing slot acquired..."  Client sees update
T+8004ms Worker   ProcessPaymentAsync()          Processing starts
        ─────────────────────────────────────────────────────────────────────
        [P6 processes for ~8 seconds]
        ─────────────────────────────────────────────────────────────────────
T+16004ms P6      Completes                      
                  Semaphore.Release()            Slot freed
```

**Total Time for P6**: ~16 seconds (8s waiting + 8s processing)

---

## Data Structures and Thread Safety

### PaymentRequest Flow

```
Client Thread              Channel              Worker Thread
     │                       │                       │
     ├─Create Request────────┤                       │
     │  PaymentId, Amount    │                       │
     │                       │                       │
     ├─WriteAsync()─────────►│                       │
     │  PaymentRequest       │                       │
     │                       │                       │
     │                       ├─ReadAllAsync()───────►│
     │                       │  Dequeue              │
     │                       │                       │
     │                       │                       ├─Process
```

### Status Update Flow

```
Worker Thread          PaymentStatusService         Controller Thread
     │                       │                            │
     ├─SendStatusAsync()────►│                            │
     │  paymentId, status    │                            │
     │                       ├─Get channel                │
     │                       │  from dictionary          │
     │                       │                            │
     │                       ├─WriteAsync(status)────────►│
     │                       │  Channel<PaymentStatus>    │
     │                       │                            │
     │                       │                            ├─ReadAllAsync()
     │                       │                            │  yields status
     │                       │                            │
     │                       │                            ├─Serialize
     │                       │                            │  JSON
     │                       │                            │
     │                       │                            ├─WriteAsync()
     │                       │                            │  HTTP Response
     │                       │                            │
     │                       │                            └─Client receives
```

---

## Key Timing Points

### 1. Initial Response (< 10ms)
- Payment registered
- Status channel created
- Initial SSE message sent
- Payment queued

### 2. First Status Update (< 10ms after dequeue)
- Worker dequeues payment
- Sends "Payment dequeued" status
- Client receives immediately

### 3. Processing Slot Acquisition (Variable)
- **If slot available**: Immediate (< 10ms)
- **If all slots busy**: Wait until one frees (~8s per payment)

### 4. Processing Phases
- **Phase 1**: Initial processing (1 second delay)
- **Phase 2**: Payment details (2 second delay)
- **Phase 3**: Third-party API call (5 second delay)
- **Total processing**: ~8 seconds

### 5. Final Status (~8 seconds after slot acquired)
- "Completed" or "Failed" status sent
- Channel closed
- Client connection closes
- Cleanup scheduled (5 seconds later)

---

## Status Message Sequence

### Normal Flow (All Status Messages)

1. **"Payment request received"** (T+1ms)
   - Source: Controller
   - Sent: Immediately on request
   - Contains: PaymentId, message

2. **"Payment dequeued, waiting for processing slot..."** (T+5ms)
   - Source: Worker (ExecuteAsync)
   - Sent: Immediately when dequeued
   - Status: Queued

3. **"Processing slot acquired, starting payment processing"** (T+13ms or T+8003ms if queued)
   - Source: Worker (ProcessPaymentAsync)
   - Sent: After semaphore acquired
   - Status: Queued

4. **"Processing payment details"** (T+1016ms after slot acquired)
   - Source: Worker (ProcessPaymentAsync)
   - Sent: After 1 second delay
   - Status: Processing

5. **"Sending payment to third-party processor"** (T+3019ms after slot acquired)
   - Source: Worker (ProcessPaymentAsync)
   - Sent: Before API call
   - Status: SendingToProcessor

6. **"Payment of $X.XX completed successfully"** (T+8021ms after slot acquired)
   - Source: Worker (ProcessPaymentAsync)
   - Sent: After API call completes
   - Status: Completed
   - **Channel closed after this**

---

## Disconnection Flow

### Client Disconnects During Processing

```
Time    Event                           Action
─────────────────────────────────────────────────────────────────────────────
T+0ms   Payment processing normally     
        Status updates flowing          
        ──────────────────────────────────────────────────────────────────────
T+5000ms USER CLOSES BROWSER            
        HttpContext.RequestAborted      
        fires                           
        ──────────────────────────────────────────────────────────────────────
T+5001ms Controller catches             
        OperationCanceledException      
        ──────────────────────────────────────────────────────────────────────
T+5002ms Controller calls                
        MarkDisconnected(paymentId)     
        ──────────────────────────────────────────────────────────────────────
T+5003ms PaymentStatusService           
        - Sets IsDisconnected = true    
        - Sets DisconnectedAt = now     
        - Closes status channel         
        ──────────────────────────────────────────────────────────────────────
T+5004ms Controller cleanup              
        Cleanup(paymentId)              
        Connection closed               
        ──────────────────────────────────────────────────────────────────────
T+6000ms Worker checks                  
        IsPaymentCancelled() = true     
        ──────────────────────────────────────────────────────────────────────
T+6001ms Worker exits early             
        return; (no processing)         
        ──────────────────────────────────────────────────────────────────────
T+6002ms Worker releases semaphore      
        Semaphore.Release()             
        Slot freed immediately          
        ──────────────────────────────────────────────────────────────────────
        ✅ No charge made to processor
```

**Key Point**: Worker checks `IsPaymentCancelled()` at multiple points:
- Before acquiring semaphore (if possible)
- After acquiring semaphore
- After queuing delay
- **Before calling third-party processor** (CRITICAL)

---

## Memory and Resource Timeline

### Channel Lifecycle

```
T+0ms     Channel created                _statusChannels[paymentId] = Channel
T+1ms     Channel reader obtained        Controller gets ChannelReader
T+8021ms  Channel writer closed          channel.Writer.Complete()
T+8024ms  Channel reader completes       ReadAllAsync() ends
T+13027ms Channel removed                Cleanup() removes from dictionary
```

### Semaphore Lifecycle (Per Payment)

```
T+10ms    Semaphore.WaitAsync()         Acquire slot (blocks if 5 busy)
T+11ms    Slot acquired                 Task created
T+8021ms  Processing completes           
T+8026ms  Semaphore.Release()           Slot freed
```

### Metadata Lifecycle

```
T+0ms     Metadata created               _paymentMetadata[paymentId] = Metadata
T+0ms     CreatedAt = now                
T+5003ms  IsDisconnected = true         (if disconnected)
T+5003ms  DisconnectedAt = now          (if disconnected)
T+8021ms  LastStatus = Completed        
T+13027ms Metadata removed              Cleanup() removes from dictionary
```

---

## Performance Characteristics

### Single Payment (No Queue)
- **Total time**: ~8 seconds
- **Status updates**: 6 messages
- **Memory**: ~2KB (channel + metadata)
- **Threads**: 1 controller thread, 1 worker task

### 100 Concurrent Payments
- **Processing time**: ~160 seconds (20 batches of 5)
- **First 5**: Start immediately, complete in ~8s
- **Next 5**: Start at ~8s, complete at ~16s
- **Last 5**: Start at ~152s, complete at ~160s
- **Memory**: ~200KB (100 channels + metadata)
- **Active tasks**: Always 5 (not 100)

### 1000 Concurrent Payments
- **Processing time**: ~1600 seconds (~27 minutes)
- **Queue position**: Payments 996-1000 wait ~27 minutes
- **Memory**: ~2MB (1000 channels + metadata)
- **Active tasks**: Always 5 (not 1000)
- **Channel capacity**: 1000 (additional requests wait)

---

## Error Scenarios

### Payment Processing Fails

```
T+8021ms Worker Task    Exception thrown              
                        (e.g., API timeout)           
                        ──────────────────────────────────────────────────────
T+8022ms Worker Task    Catch block executes          
                        ──────────────────────────────────────────────────────
T+8023ms Worker Task    SendStatusAsync()             
                        Status = Failed               
                        Message = "Payment failed: ..."
                        ──────────────────────────────────────────────────────
T+8024ms Worker Task    Channel.Writer.Complete()    
                        Close channel                 
                        ──────────────────────────────────────────────────────
T+8025ms Controller     [UNBLOCKED]                   
                        Receive "Failed" status       
                        Stream to client              
                        ──────────────────────────────────────────────────────
T+8026ms Controller     Connection closes             
                        Client sees error              
```

### Application Shutdown

```
T+0ms     Application receives shutdown signal        
          CancellationToken fires                      
          ──────────────────────────────────────────────────────────────────────
T+1ms     Worker ExecuteAsync()                       
          ReadAllAsync() cancelled                    
          ──────────────────────────────────────────────────────────────────────
T+2ms     Worker tasks check                          
          stoppingToken.IsCancellationRequested       
          ──────────────────────────────────────────────────────────────────────
T+3ms     In-progress payments                         
          catch OperationCanceledException            
          Log: "cancelled due to shutdown"            
          ──────────────────────────────────────────────────────────────────────
T+4ms     Semaphores released                         
          Resources cleaned up                        
```

---

## Configuration Impact on Timing

### Changing Semaphore Limit

**Current**: 5 concurrent
- 100 payments: ~160 seconds
- 1000 payments: ~1600 seconds

**If changed to 10 concurrent**:
- 100 payments: ~80 seconds
- 1000 payments: ~800 seconds

**If changed to 1 concurrent**:
- 100 payments: ~800 seconds
- 1000 payments: ~8000 seconds

### Changing Processing Delays

**Current delays**: 1s + 2s + 5s = 8s total

**If third-party API is faster** (e.g., 2s instead of 5s):
- Total: 1s + 2s + 2s = 5s
- 100 payments: ~100 seconds (instead of 160)

**If third-party API is slower** (e.g., 10s instead of 5s):
- Total: 1s + 2s + 10s = 13s
- 100 payments: ~260 seconds (instead of 160)

---

## Monitoring Points

### Key Metrics to Track

1. **Queue Depth**: Number of payments in channel
   - Check: `_channelService.Reader` count
   - Alert if: > 500 (half capacity)

2. **Active Processing**: Number of payments currently processing
   - Check: `_processingSemaphore.CurrentCount` (5 - count = active)
   - Should be: 0-5

3. **Disconnection Rate**: Payments cancelled due to disconnect
   - Check: `_statusService.GetStatistics().DisconnectedPayments`
   - Alert if: > 10% of total

4. **Processing Time**: Time from dequeue to completion
   - Track: `DisconnectedAt - QueuedAt` or `CompletedAt - QueuedAt`
   - Expected: ~8 seconds (plus queue wait time)

5. **Memory Usage**: Number of active status channels
   - Check: `_statusService.GetStatistics().TotalInMemory`
   - Should be: < 1000 (channel capacity)

---

## Summary

### Key Timing Facts

- **Fastest possible**: ~8 seconds (no queue, slot available)
- **Typical with queue**: 8-16 seconds (wait 0-8s + process 8s)
- **Worst case**: 8s + (queue_position / 5) * 8s
  - Example: Position 100 in queue = 8s + (100/5)*8s = 168 seconds

### Status Update Guarantees

- **Always sent**: "Payment request received" (< 10ms)
- **Always sent**: "Payment dequeued" (< 10ms after dequeue)
- **Always sent**: "Processing slot acquired" (when slot available)
- **Always sent**: Final status ("Completed" or "Failed")
- **May be skipped**: Intermediate statuses if client disconnects

### Thread Safety Guarantees

- ✅ Channel operations are thread-safe
- ✅ ConcurrentDictionary operations are thread-safe
- ✅ Semaphore operations are thread-safe
- ✅ No manual locking required
- ✅ No race conditions in normal flow

This system is designed to handle high concurrency while maintaining FIFO order and providing real-time status updates to clients.

