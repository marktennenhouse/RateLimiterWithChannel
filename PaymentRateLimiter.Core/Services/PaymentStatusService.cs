using Microsoft.Extensions.Logging;
using PaymentRateLimiter.Core.Models;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PaymentRateLimiter.Core.Services
{
    /// <summary>
    /// Thread-safe service that manages per-payment status channels.
    /// Acts as a bridge between background worker threads and client HTTP threads.
    /// Workers send status updates, clients stream them via SSE.
    /// </summary>
    public class PaymentStatusService
    {
        private readonly ConcurrentDictionary<string, Channel<PaymentStatus>> _statusChannels = new();
        private readonly ConcurrentDictionary<string, PaymentMetadata> _paymentMetadata = new();
        private readonly ILogger<PaymentStatusService> _logger;

        public PaymentStatusService(ILogger<PaymentStatusService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Metadata for tracking payment lifecycle and disconnections
        /// </summary>
        private class PaymentMetadata
        {
            public DateTime CreatedAt { get; set; }
            public bool IsDisconnected { get; set; }
            public DateTime? DisconnectedAt { get; set; }
            public PaymentStatusEnum LastStatus { get; set; }
        }

        /// <summary>
        /// Register a new payment and create its status channel.
        /// Called by the client thread when initiating payment.
        /// </summary>
        public void RegisterPayment(PaymentRequest request)
        {
            var channel = Channel.CreateUnbounded<PaymentStatus>();
            _statusChannels.TryAdd(request.PaymentId, channel);
            _paymentMetadata.TryAdd(request.PaymentId, new PaymentMetadata
            {
                CreatedAt = DateTime.UtcNow,
                IsDisconnected = request.IsDisconnected,
                LastStatus = PaymentStatusEnum.Queued
            });

            _logger.LogInformation("Registered payment {PaymentId} for tracking", request.PaymentId);
        }

        /// <summary>
        /// Mark a payment as disconnected (client closed connection).
        /// Called by the controller when HttpContext.RequestAborted fires.
        /// </summary>
        public void MarkDisconnected(string paymentId)
        {
            if (_paymentMetadata.TryGetValue(paymentId, out var metadata))
            {
                metadata.IsDisconnected = true;
                metadata.DisconnectedAt = DateTime.UtcNow;
                
                _logger.LogInformation("Payment {PaymentId} marked as disconnected", paymentId);
            }

            // Close the status channel since no one is listening
            if (_statusChannels.TryGetValue(paymentId, out var channel))
            {
                channel.Writer.Complete();
            }
        }

        /// <summary>
        /// Check if a payment has been cancelled due to client disconnection.
        /// Called by worker thread before expensive operations.
        /// </summary>
        public bool IsPaymentCancelled(string paymentId)
        {
            return _paymentMetadata.TryGetValue(paymentId, out var metadata) 
                && metadata.IsDisconnected;
        }

        /// <summary>
        /// Send a status update for a payment.
        /// Called by background worker thread. Thread-safe.
        /// </summary>
        public async Task SendStatusAsync(string paymentId, PaymentStatus status)
        {
            // Update metadata with latest status
            if (_paymentMetadata.TryGetValue(paymentId, out var metadata))
            {
                metadata.LastStatus = status.Status;
            }

            // Try to write status to the payment's channel
            if (_statusChannels.TryGetValue(paymentId, out var channel))
            {
                try
                {
                    await channel.Writer.WriteAsync(status);
                    
                    _logger.LogInformation("Status {Status} sent for payment {PaymentId}: {Message}", 
                        status.Status, paymentId, status.Message);

                    // On terminal states, close the channel and schedule cleanup
                    if (status.Status == PaymentStatusEnum.Completed || 
                        status.Status == PaymentStatusEnum.Failed)
                    {
                        channel.Writer.Complete();
                        
                        // Schedule delayed cleanup to ensure client receives final message
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5));
                            Cleanup(paymentId);
                            _logger.LogDebug("Delayed cleanup executed for payment {PaymentId}", paymentId);
                        });
                    }
                }
                catch (ChannelClosedException)
                {
                    // Channel already closed (client disconnected), this is expected
                    _logger.LogDebug("Attempted to write to closed channel for payment {PaymentId}", 
                        paymentId);
                }
            }
            else
            {
                _logger.LogWarning("No status channel found for payment {PaymentId}", paymentId);
            }
        }

        /// <summary>
        /// Get a reader for streaming status updates to the client.
        /// Called by the controller thread.
        /// </summary>
        public ChannelReader<PaymentStatus>? GetStatusReader(string paymentId)
        {
            if (_statusChannels.TryGetValue(paymentId, out var channel))
            {
                _logger.LogInformation("Status reader retrieved for payment {PaymentId}. Channel exists: {Exists}", 
                    paymentId, channel != null);
                return channel.Reader;
            }
            
            _logger.LogWarning("No status channel found for payment {PaymentId} when getting reader", paymentId);
            return null;
        }

        /// <summary>
        /// Clean up a payment's status channel and metadata.
        /// Called in finally blocks and during periodic cleanup.
        /// Idempotent - safe to call multiple times.
        /// </summary>
        public void Cleanup(string paymentId)
        {
            // Try to remove channel (idempotent - returns false if already removed)
            if (_statusChannels.TryRemove(paymentId, out var channel))
            {
                try
                {
                    // Complete the channel writer (safe even if already completed)
                    channel.Writer.Complete();
                }
                catch (ChannelClosedException)
                {
                    // Channel already closed, ignore - this is expected
                }
                catch (InvalidOperationException)
                {
                    // Channel already completed, ignore - this is expected
                }
                
            }
            
            // Remove metadata (idempotent - returns false if already removed)
            _paymentMetadata.TryRemove(paymentId, out _);
            
            _logger.LogDebug("Cleaned up payment {PaymentId}", paymentId);
        }

        /// <summary>
        /// Periodic cleanup of stale payment records.
        /// Called by PaymentCleanupService background worker.
        /// Returns the number of records cleaned up.
        /// </summary>
        public Task<int> CleanupStalePayments(TimeSpan disconnectedThreshold, TimeSpan anyPaymentThreshold)
        {
            var now = DateTime.UtcNow;
            var stalePayments = _paymentMetadata
                .Where(kvp =>
                    // Disconnected payments older than threshold
                    (kvp.Value.IsDisconnected &&
                     kvp.Value.DisconnectedAt.HasValue &&
                     now - kvp.Value.DisconnectedAt.Value > disconnectedThreshold)
                    ||
                    // Any payment older than safety threshold (catches orphans)
                    (now - kvp.Value.CreatedAt > anyPaymentThreshold))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var paymentId in stalePayments)
            {
                Cleanup(paymentId);
                _logger.LogInformation("Cleaned up stale payment {PaymentId}", paymentId);
            }

            return Task.FromResult(stalePayments.Count);
        }

        /// <summary>
        /// Get statistics for monitoring.
        /// Returns (active, disconnected, total) counts.
        /// </summary>
        public (int Active, int Disconnected, int Total) GetStatistics()
        {
            var total = _paymentMetadata.Count;
            var disconnected = _paymentMetadata.Count(kvp => kvp.Value.IsDisconnected);
            var active = total - disconnected;

            return (active, disconnected, total);
        }
    }
}

