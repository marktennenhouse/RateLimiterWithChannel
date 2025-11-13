using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentRateLimiter.Core.Models;
using PaymentRateLimiter.Core.Services;
using System.Text.Json;

namespace ClientExamples
{
    /// <summary>
    /// Example showing how to use PaymentRateLimiter.Core library in your application.
    /// This demonstrates:
    /// 1. Registering services
    /// 2. Sending payment requests
    /// 3. Subscribing to status updates
    /// </summary>
    public class LibraryUsageExample
    {
        public static async Task Main(string[] args)
        {
            // Build service collection
            var services = new ServiceCollection();
            
            // Configure logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            // Configure Azure Service Bus options
            services.Configure<AzureServiceBusOptions>(options =>
            {
                options.ConnectionString = "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=your-key";
                options.QueueName = "payment-requests";
                options.MaxConcurrentCalls = 5;
                options.AutoCompleteMessages = false;
                options.MaxAutoLockRenewalDurationString = "00:05:00";
            });

            // Register PaymentRateLimiter.Core services
            services.AddSingleton<PaymentServiceBusService>();
            services.AddSingleton<PaymentStatusService>();
            services.AddHostedService<PaymentProcessorWorker>();

            // Build service provider
            var serviceProvider = services.BuildServiceProvider();

            // Get services
            var serviceBusService = serviceProvider.GetRequiredService<PaymentServiceBusService>();
            var statusService = serviceProvider.GetRequiredService<PaymentStatusService>();

            // Start the background worker
            var host = serviceProvider.GetRequiredService<IHost>();
            await host.StartAsync();

            // Example: Process a payment
            await ProcessPaymentExample(serviceBusService, statusService);

            // Wait a bit for processing
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Stop the host
            await host.StopAsync();
        }

        /// <summary>
        /// Example: How to process a payment using the library
        /// </summary>
        private static async Task ProcessPaymentExample(
            PaymentServiceBusService serviceBusService,
            PaymentStatusService statusService)
        {
            // Create payment request
            var paymentRequest = new PaymentRequest
            {
                PaymentId = Guid.NewGuid().ToString(),
                Amount = 99.99m,
                CardToken = "tok_demo_123456",
                CustomerEmail = "customer@example.com",
                QueuedAt = DateTime.UtcNow,
                IsDisconnected = false
            };

            // Register payment to create status channel
            statusService.RegisterPayment(paymentRequest);

            // Send payment to Service Bus queue
            await serviceBusService.SendPaymentRequestAsync(paymentRequest);

            Console.WriteLine($"Payment {paymentRequest.PaymentId} sent to queue");

            // Subscribe to status updates
            var statusReader = statusService.GetStatusReader(paymentRequest.PaymentId);
            if (statusReader != null)
            {
                // Read status updates asynchronously
                _ = Task.Run(async () =>
                {
                    await foreach (var status in statusReader.ReadAllAsync())
                    {
                        var json = JsonSerializer.Serialize(status, new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });
                        Console.WriteLine($"Status Update: {json}");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Example: Using the library in a web API controller
    /// </summary>
    public class PaymentControllerExample
    {
        private readonly PaymentServiceBusService _serviceBusService;
        private readonly PaymentStatusService _statusService;

        public PaymentControllerExample(
            PaymentServiceBusService serviceBusService,
            PaymentStatusService statusService)
        {
            _serviceBusService = serviceBusService;
            _statusService = statusService;
        }

        /// <summary>
        /// Process a payment and return payment ID immediately.
        /// Client can poll for status or use a separate endpoint to stream status.
        /// </summary>
        public async Task<string> ProcessPaymentAsync(PaymentRequestDto dto)
        {
            var paymentRequest = new PaymentRequest
            {
                PaymentId = Guid.NewGuid().ToString(),
                Amount = dto.Amount,
                CardToken = dto.CardToken,
                CustomerEmail = dto.CustomerEmail,
                QueuedAt = DateTime.UtcNow,
                IsDisconnected = false
            };

            // Register payment
            _statusService.RegisterPayment(paymentRequest);

            // Send to Service Bus
            await _serviceBusService.SendPaymentRequestAsync(paymentRequest);

            return paymentRequest.PaymentId;
        }

        /// <summary>
        /// Get status updates for a payment (for polling)
        /// </summary>
        public async Task<PaymentStatus?> GetPaymentStatusAsync(string paymentId)
        {
            var statusReader = _statusService.GetStatusReader(paymentId);
            if (statusReader == null)
                return null;

            // Try to read the latest status (non-blocking)
            if (statusReader.TryRead(out var status))
            {
                return status;
            }

            return null;
        }

        /// <summary>
        /// Stream status updates (for Server-Sent Events or SignalR)
        /// </summary>
        public async Task StreamStatusUpdatesAsync(string paymentId, Func<PaymentStatus, Task> onStatusUpdate)
        {
            var statusReader = _statusService.GetStatusReader(paymentId);
            if (statusReader == null)
                return;

            await foreach (var status in statusReader.ReadAllAsync())
            {
                await onStatusUpdate(status);
            }
        }
    }
}

