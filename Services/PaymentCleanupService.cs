namespace PaymentChannelDemo.Services
{
    /// <summary>
    /// Background service that removes payments from the main queue/channel  which have been processed or abandoned.
    /// It cleans up stale payment records by looping thru the full channel,
    /// filters out the older already processed records
    /// and removes any that accidentally were left in there when the standard remove somehow failed.
    /// Acts as a safety net to prevent memory leaks from orphaned status channels.
    /// Runs every 5 minutes and removes:
    /// - Disconnected payments older than 10 minutes
    /// - Any payment older than 20 minutes (catches edge cases)
    /// </summary>
    public class PaymentCleanupService : BackgroundService
    {
        private readonly PaymentStatusService _statusService;
        private readonly ILogger<PaymentCleanupService> _logger;
        
        // How often to run cleanup
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);
        
        // Thresholds for cleanup
        private readonly TimeSpan _disconnectedThreshold = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _anyPaymentThreshold = TimeSpan.FromMinutes(20);

        public PaymentCleanupService(
            PaymentStatusService statusService,
            ILogger<PaymentCleanupService> logger)
        {
            _statusService = statusService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "PaymentCleanupService started. Cleanup interval: {Interval}, " +
                "Disconnected threshold: {DisconnectedThreshold}, " +
                "Any payment threshold: {AnyThreshold}",
                _cleanupInterval, _disconnectedThreshold, _anyPaymentThreshold);

            // Wait a bit before first cleanup to let the system start
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogDebug("Starting periodic payment cleanup");

                    var cleanedUp = await _statusService.CleanupStalePayments(
                        _disconnectedThreshold, 
                        _anyPaymentThreshold);

                    if (cleanedUp > 0)
                    {
                        _logger.LogInformation(
                            "Periodic cleanup completed. Removed {Count} stale payment record(s)", 
                            cleanedUp);
                    }
                    else
                    {
                        _logger.LogDebug("Periodic cleanup completed. No stale records found");
                    }

                    // Wait until next cleanup cycle
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Application shutting down
                    _logger.LogInformation("PaymentCleanupService stopping due to cancellation");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during periodic payment cleanup");
                    
                    // Wait a bit before retrying after error
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }

            _logger.LogInformation("PaymentCleanupService stopped");
        }
    }
}

