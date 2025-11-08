using PaymentChannelDemo.Models;

namespace PaymentChannelDemo.Services
{
    /// <summary>
    /// Background service that processes payments from the queue with rate limiting.
    /// Limits to 5 concurrent payment operations using a semaphore.
    /// Sends status updates back to clients via PaymentStatusService.
    /// </summary>
    public class PaymentProcessorWorker : BackgroundService
    {
        private readonly PaymentChannelService _channelService;
        private readonly PaymentStatusService _statusService;
        private readonly ILogger<PaymentProcessorWorker> _logger;
        
        // Rate limiter: max 5 concurrent payment processing operations
        // Note: This semaphore is now used within ProcessPaymentAsync for the actual processing
        // The ExecuteAsync method uses its own semaphore to limit task creation
        private readonly SemaphoreSlim _processingSemaphore = new SemaphoreSlim(5, 5);

        public PaymentProcessorWorker(
            PaymentChannelService channelService,
            PaymentStatusService statusService,
            ILogger<PaymentProcessorWorker> logger)
        {
            _channelService = channelService;
            _statusService = statusService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PaymentProcessorWorker started. Max concurrent: 5");

            // Process payments with controlled concurrency
            // This pattern limits both the number of tasks created AND concurrent processing
            // By waiting for semaphore BEFORE creating the task, we prevent creating unlimited tasks
            // With 1000 users: only 5 tasks exist at once (not 1000 tasks waiting)
            
            await foreach (var paymentRequest in _channelService.Reader.ReadAllAsync(stoppingToken))
            {
                var paymentId = paymentRequest.PaymentId;
                
                // Send status immediately when dequeued (before semaphore wait)
                // This lets the client know the payment is in the queue
                await _statusService.SendStatusAsync(paymentId, new PaymentStatus
                {
                    PaymentId = paymentId,
                    Status = PaymentStatusEnum.Queued,
                    Message = "Payment dequeued, waiting for processing slot...",
                    Timestamp = DateTime.UtcNow
                });
                
                // Wait for semaphore slot before creating task
                // This prevents creating unlimited tasks - only creates task when slot is available
                await _processingSemaphore.WaitAsync(stoppingToken);
                
                // Fire and forget - but we've already acquired the slot
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessPaymentAsync(paymentRequest, stoppingToken);
                    }
                    finally
                    {
                        // Release semaphore slot so next payment can start
                        _processingSemaphore.Release();
                    }
                }, stoppingToken);
            }
        }

        private async Task ProcessPaymentAsync(PaymentRequest request, CancellationToken stoppingToken)
        {
            var paymentId = request.PaymentId;
            
            try
            {
                // Check if client disconnected before we even start
                if (_statusService.IsPaymentCancelled(paymentId))
                {
                    _logger.LogInformation("Skipping cancelled payment {PaymentId} (disconnected before processing)", 
                        paymentId);
                    return;
                }

                // Note: Semaphore slot was already acquired in ExecuteAsync before task creation
                // This ensures we don't create unlimited tasks waiting for slots
                _logger.LogInformation("Payment {PaymentId} starting processing. Amount: {Amount}", 
                    paymentId, request.Amount);

                // Check if client disconnected (could have happened while in queue)
                if (_statusService.IsPaymentCancelled(paymentId))
                {
                    _logger.LogInformation("Payment {PaymentId} cancelled before processing", paymentId);
                    return;
                }

                try
                {
                    // Send status that processing slot is acquired and starting
                    await _statusService.SendStatusAsync(paymentId, new PaymentStatus
                    {
                        PaymentId = paymentId,
                        Status = PaymentStatusEnum.Queued,
                        Message = "Processing slot acquired, starting payment processing",
                        Timestamp = DateTime.UtcNow
                    });

                    // Simulate initial processing delay
                    await Task.Delay(1000, stoppingToken);

                    // Check again before expensive operations
                    if (_statusService.IsPaymentCancelled(paymentId))
                    {
                        _logger.LogInformation("Payment {PaymentId} cancelled while queued", paymentId);
                        return;
                    }

                    // Send processing status
                    await _statusService.SendStatusAsync(paymentId, new PaymentStatus
                    {
                        PaymentId = paymentId,
                        Status = PaymentStatusEnum.Processing,
                        Message = "Processing payment details",
                        Timestamp = DateTime.UtcNow
                    });

                    await Task.Delay(2000, stoppingToken);

                    // CRITICAL CHECK: Before calling third-party processor
                    // This prevents charging disconnected clients
                    if (_statusService.IsPaymentCancelled(paymentId))
                    {
                        _logger.LogWarning("Payment {PaymentId} cancelled before processor call - no charge made", 
                            paymentId);
                        return;
                    }

                    // Send status before calling processor
                    await _statusService.SendStatusAsync(paymentId, new PaymentStatus
                    {
                        PaymentId = paymentId,
                        Status = PaymentStatusEnum.SendingToProcessor,
                        Message = "Sending payment to third-party processor",
                        Timestamp = DateTime.UtcNow
                    });

                    // Simulate third-party payment processor API call
                    // In production, this would be: await PaymentProcessor.ChargeAsync(request)
                    await Task.Delay(5000, stoppingToken);

                    // Payment successful
                    await _statusService.SendStatusAsync(paymentId, new PaymentStatus
                    {
                        PaymentId = paymentId,
                        Status = PaymentStatusEnum.Completed,
                        Message = $"Payment of ${request.Amount:F2} completed successfully",
                        Timestamp = DateTime.UtcNow
                    });

                    _logger.LogInformation("Payment {PaymentId} completed successfully. Amount: {Amount}", 
                        paymentId, request.Amount);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Application shutting down
                    _logger.LogInformation("Payment {PaymentId} cancelled due to application shutdown", 
                        paymentId);
                }
                catch (Exception ex)
                {
                    // Payment processing failed
                    _logger.LogError(ex, "Payment {PaymentId} failed with error", paymentId);
                    
                    await _statusService.SendStatusAsync(paymentId, new PaymentStatus
                    {
                        PaymentId = paymentId,
                        Status = PaymentStatusEnum.Failed,
                        Message = $"Payment failed: {ex.Message}",
                        Timestamp = DateTime.UtcNow
                    });
                }
                finally
                {
                    // Semaphore is released in ExecuteAsync's Task.Run finally block
                    _logger.LogDebug("Payment {PaymentId} processing completed", paymentId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing payment {PaymentId}", paymentId);
            }
        }

        public override void Dispose()
        {
            _processingSemaphore?.Dispose();
            base.Dispose();
        }
    }
}
