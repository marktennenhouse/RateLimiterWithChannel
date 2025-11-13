using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentRateLimiter.Core.Models;
using System.Text.Json;

namespace PaymentRateLimiter.Core.Services
{
    /// <summary>
    /// Background service that processes payments from Azure Service Bus queue with rate limiting.
    /// Uses ServiceBusProcessor with MaxConcurrentCalls to limit to 5 concurrent payment operations.
    /// Sends status updates back to clients via PaymentStatusService.
    /// </summary>
    public class PaymentProcessorWorker : BackgroundService
    {
        private readonly PaymentServiceBusService _serviceBusService;
        private readonly PaymentStatusService _statusService;
        private readonly ILogger<PaymentProcessorWorker> _logger;

        public PaymentProcessorWorker(
            PaymentServiceBusService serviceBusService,
            PaymentStatusService statusService,
            ILogger<PaymentProcessorWorker> logger)
        {
            _serviceBusService = serviceBusService;
            _statusService = statusService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PaymentProcessorWorker started. Using Service Bus with MaxConcurrentCalls limit.");

            var processor = _serviceBusService.Processor;

            // Configure message handler
            processor.ProcessMessageAsync += async args =>
            {
                try
                {
                    await ProcessServiceBusMessageAsync(args, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Service Bus message {MessageId}", args.Message.MessageId);
                    // Don't complete the message - let it go to dead letter queue or retry
                    throw;
                }
            };

            // Configure error handler
            processor.ProcessErrorAsync += args =>
            {
                _logger.LogError(args.Exception, "Service Bus processor error: {ErrorSource}", args.ErrorSource);
                return Task.CompletedTask;
            };

            // Start processing messages
            await _serviceBusService.StartProcessingAsync(stoppingToken);

            // Wait until cancellation is requested
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("PaymentProcessorWorker stopping due to cancellation");
            }
            finally
            {
                await _serviceBusService.StopProcessingAsync(stoppingToken);
            }
        }

        private async Task ProcessServiceBusMessageAsync(ProcessMessageEventArgs args, CancellationToken stoppingToken)
        {
            PaymentRequest? paymentRequest = null;
            var paymentId = args.Message.MessageId ?? "";

            try
            {
                // Deserialize payment request from message body
                var messageBody = args.Message.Body.ToString();
                paymentRequest = JsonSerializer.Deserialize<PaymentRequest>(messageBody);

                if (paymentRequest == null)
                {
                    _logger.LogError("Failed to deserialize payment request from message {MessageId}", args.Message.MessageId);
                    await args.CompleteMessageAsync(args.Message, stoppingToken);
                    return;
                }

                paymentId = paymentRequest.PaymentId;

                _logger.LogInformation(
                    "Payment message received from Service Bus. PaymentId: {PaymentId}, Amount: {Amount}",
                    paymentId, paymentRequest.Amount);

                // Send status immediately when message is received (before processing)
                await _statusService.SendStatusAsync(paymentId, new PaymentStatus
                {
                    PaymentId = paymentId,
                    Status = PaymentStatusEnum.Queued,
                    Message = "Payment dequeued, waiting for processing slot...",
                    Timestamp = DateTime.UtcNow
                });

                // Process the payment
                // Note: MaxConcurrentCalls is handled by ServiceBusProcessor automatically
                // Only 5 messages will be processed concurrently
                await ProcessPaymentAsync(paymentRequest, args, stoppingToken);

                // Complete the message after successful processing
                await args.CompleteMessageAsync(args.Message, stoppingToken);

                _logger.LogInformation("Payment {PaymentId} message completed successfully", paymentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing payment {PaymentId} from Service Bus", paymentId);

                // Check if payment was cancelled (client disconnected)
                if (paymentRequest != null && _statusService.IsPaymentCancelled(paymentRequest.PaymentId))
                {
                    _logger.LogWarning(
                        "Payment {PaymentId} was cancelled (client disconnected). Abandoning message.",
                        paymentId);
                    
                    // Abandon the message - Service Bus will redeliver it after lock timeout
                    // Or it will go to dead letter queue if max delivery count is reached
                    await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
                }
                else
                {
                    // For other errors, abandon the message for retry
                    await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
                }

                throw; // Re-throw to let processor handle it
            }
        }

        private async Task ProcessPaymentAsync(
            PaymentRequest request,
            ProcessMessageEventArgs args,
            CancellationToken stoppingToken)
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
                    _logger.LogDebug("Payment {PaymentId} processing completed", paymentId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing payment {PaymentId}", paymentId);
                throw;
            }
        }
    }
}
