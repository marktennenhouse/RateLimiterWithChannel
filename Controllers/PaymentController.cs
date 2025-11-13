using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PaymentChannelDemo.Models;
using PaymentChannelDemo.Services;
using System.Text.Json;

namespace PaymentChannelDemo.Controllers
{
    /// <summary>
    /// Controller for processing credit card payments.
    /// Uses Server-Sent Events (SSE) to stream real-time status updates to clients.
    /// Handles client disconnection gracefully to prevent charging abandoned payments.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class PaymentController : ControllerBase
    {
        private readonly PaymentServiceBusService _serviceBusService;
        private readonly PaymentStatusService _statusService;
        private readonly ILogger<PaymentController> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public PaymentController(
            PaymentServiceBusService serviceBusService,
            PaymentStatusService statusService,
            ILogger<PaymentController> logger,
            IOptions<JsonOptions> jsonOptions)
        {
            _serviceBusService = serviceBusService;
            _statusService = statusService;
            _logger = logger;
            _jsonOptions = jsonOptions.Value.JsonSerializerOptions;
        }

        /// <summary>
        /// Process a payment with real-time status updates via Server-Sent Events.
        /// Connection stays open until payment completes or client disconnects.
        /// </summary>
        /// <param name="dto">Payment request containing amount and card token</param>
        /// <returns>SSE stream of payment status updates</returns>
        [HttpPost("process")]
        public async Task ProcessPayment([FromBody] PaymentRequestDto dto)
        {
            // Set up Server-Sent Events response
            Response.Headers.Add("Content-Type", "text/event-stream");
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            // Create payment request with metadata
            var paymentRequest = new PaymentRequest
            {
                PaymentId = Guid.NewGuid().ToString(),
                Amount = dto.Amount,
                CardToken = dto.CardToken,
                CustomerEmail = dto.CustomerEmail,
                QueuedAt = DateTime.UtcNow,
                IsDisconnected = false,
                ClientIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                RetryCount = 0
            };

            _logger.LogInformation(
                "Payment request received. PaymentId: {PaymentId}, Amount: {Amount}, IP: {IP}",
                paymentRequest.PaymentId, paymentRequest.Amount, paymentRequest.ClientIpAddress);

            // Create linked cancellation token that fires on client disconnect OR app shutdown
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                HttpContext.RequestAborted
            );

            var wasDisconnected = false;

            try
            {
                // Register payment to create its status channel
                _statusService.RegisterPayment(paymentRequest);

                // Send initial response with payment ID
                var initialMessage = JsonSerializer.Serialize(new
                {
                    paymentId = paymentRequest.PaymentId,
                    message = "Payment request received"
                }, _jsonOptions);
                await Response.WriteAsync($"data: {initialMessage}\n\n", cts.Token);
                await Response.Body.FlushAsync(cts.Token);

                // Send payment request to Azure Service Bus queue
                await _serviceBusService.SendPaymentRequestAsync(paymentRequest, cts.Token);

                // Get reader for this payment's status updates
                var statusReader = _statusService.GetStatusReader(paymentRequest.PaymentId);
                
                if (statusReader == null)
                {
                    _logger.LogError("Failed to get status reader for payment {PaymentId}", 
                        paymentRequest.PaymentId);
                    return;
                }

                _logger.LogInformation("Starting to read status updates for payment {PaymentId}", 
                    paymentRequest.PaymentId);

                // Stream status updates to client
                try
                {
                    await foreach (var status in statusReader.ReadAllAsync(cts.Token))
                    {
                        _logger.LogInformation("Received status update for payment {PaymentId}: {Status} - {Message}", 
                            paymentRequest.PaymentId, status.Status, status.Message);
                        
                        var json = JsonSerializer.Serialize(status, _jsonOptions);
                        await Response.WriteAsync($"data: {json}\n\n", cts.Token);
                        await Response.Body.FlushAsync(cts.Token);

                        _logger.LogInformation("Streamed status {Status} to client for payment {PaymentId}",
                            status.Status, paymentRequest.PaymentId);
                    }
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                {
                    _logger.LogInformation("Status reading cancelled for payment {PaymentId}", paymentRequest.PaymentId);
                    throw; // Re-throw to be caught by outer catch
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading status updates for payment {PaymentId}", paymentRequest.PaymentId);
                    throw;
                }

                _logger.LogInformation("Payment {PaymentId} stream completed normally", 
                    paymentRequest.PaymentId);
                
                // Normal completion - delayed cleanup will handle it (5 seconds after final status)
                // Don't cleanup here to avoid race condition
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Client disconnected - mark payment as cancelled
                wasDisconnected = true;
                paymentRequest.IsDisconnected = true;
                paymentRequest.DisconnectedAt = DateTime.UtcNow;
                _statusService.MarkDisconnected(paymentRequest.PaymentId);

                var duration = (DateTime.UtcNow - paymentRequest.QueuedAt).TotalMilliseconds;
                _logger.LogWarning(
                    "Client disconnected for payment {PaymentId} after {Duration}ms. " +
                    "Payment marked as cancelled to prevent charging.",
                    paymentRequest.PaymentId, duration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing payment {PaymentId}", paymentRequest.PaymentId);
                wasDisconnected = true; // Treat errors as disconnection for cleanup purposes
            }
            finally
            {
                // Only cleanup immediately if disconnected or error occurred
                // For normal completions, let the delayed cleanup handle it (avoids race condition)
                if (wasDisconnected)
                {
                    _statusService.Cleanup(paymentRequest.PaymentId);
                }
                // Note: For normal completions, cleanup happens via delayed cleanup in SendStatusAsync
                // This prevents race conditions where cleanup happens while channel is still being read
            }
        }

        /// <summary>
        /// Get statistics about payments currently in memory.
        /// Useful for monitoring and debugging.
        /// </summary>
        /// <returns>Active, disconnected, and total payment counts</returns>
        [HttpGet("status/stats")]
        public IActionResult GetStatistics()
        {
            var (active, disconnected, total) = _statusService.GetStatistics();

            return Ok(new
            {
                activePayments = active,
                disconnectedPayments = disconnected,
                totalInMemory = total,
                timestamp = DateTime.UtcNow
            });
        }
    }
}
