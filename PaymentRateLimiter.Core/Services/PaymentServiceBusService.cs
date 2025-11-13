using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentRateLimiter.Core.Models;
using System.Text;
using System.Text.Json;

namespace PaymentRateLimiter.Core.Services
{
    /// <summary>
    /// Service that wraps Azure Service Bus operations for payment processing.
    /// Handles sending payment requests to the queue and provides a processor for receiving messages.
    /// </summary>
    public class PaymentServiceBusService : IDisposable
    {
        private readonly ServiceBusClient _client;
        private readonly ServiceBusSender _sender;
        private readonly ServiceBusProcessor _processor;
        private readonly ILogger<PaymentServiceBusService> _logger;
        private readonly string _queueName;

        public PaymentServiceBusService(
            IOptions<AzureServiceBusOptions> options,
            ILogger<PaymentServiceBusService> logger)
        {
            _logger = logger;
            var config = options.Value;
            _queueName = config.QueueName;

            // Create Service Bus client
            _client = new ServiceBusClient(config.ConnectionString);

            // Create sender for sending messages to the queue
            _sender = _client.CreateSender(_queueName);

            // Create processor for receiving messages from the queue
            var processorOptions = new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = config.MaxConcurrentCalls,
                AutoCompleteMessages = config.AutoCompleteMessages
            };

            // Parse MaxAutoLockRenewalDuration from string if provided, or use default
            if (config.MaxAutoLockRenewalDurationString != null)
            {
                if (TimeSpan.TryParse(config.MaxAutoLockRenewalDurationString, out var duration))
                {
                    processorOptions.MaxAutoLockRenewalDuration = duration;
                }
            }

            _processor = _client.CreateProcessor(_queueName, processorOptions);

            _logger.LogInformation(
                "PaymentServiceBusService initialized. Queue: {QueueName}, MaxConcurrentCalls: {MaxConcurrentCalls}",
                _queueName, config.MaxConcurrentCalls);
        }

        /// <summary>
        /// Send a payment request to the Service Bus queue.
        /// </summary>
        public async Task SendPaymentRequestAsync(PaymentRequest paymentRequest, CancellationToken cancellationToken = default)
        {
            try
            {
                // Serialize payment request to JSON
                var json = JsonSerializer.Serialize(paymentRequest);
                var messageBody = Encoding.UTF8.GetBytes(json);

                // Create Service Bus message
                var message = new ServiceBusMessage(messageBody)
                {
                    MessageId = paymentRequest.PaymentId,
                    Subject = "PaymentRequest"
                };

                // Add custom properties for easier filtering/debugging
                message.ApplicationProperties.Add("PaymentId", paymentRequest.PaymentId);
                message.ApplicationProperties.Add("Amount", paymentRequest.Amount);
                message.ApplicationProperties.Add("CustomerEmail", paymentRequest.CustomerEmail ?? "");

                // Send message to queue
                await _sender.SendMessageAsync(message, cancellationToken);

                _logger.LogInformation(
                    "Payment request {PaymentId} sent to Service Bus queue {QueueName}",
                    paymentRequest.PaymentId, _queueName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send payment request {PaymentId} to Service Bus", paymentRequest.PaymentId);
                throw;
            }
        }

        /// <summary>
        /// Get the Service Bus processor for receiving messages.
        /// </summary>
        public ServiceBusProcessor Processor => _processor;

        /// <summary>
        /// Start processing messages from the queue.
        /// </summary>
        public async Task StartProcessingAsync(CancellationToken cancellationToken = default)
        {
            await _processor.StartProcessingAsync(cancellationToken);
            _logger.LogInformation("Service Bus processor started for queue {QueueName}", _queueName);
        }

        /// <summary>
        /// Stop processing messages from the queue.
        /// </summary>
        public async Task StopProcessingAsync(CancellationToken cancellationToken = default)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            _logger.LogInformation("Service Bus processor stopped for queue {QueueName}", _queueName);
        }

        public void Dispose()
        {
            _processor?.DisposeAsync().AsTask().Wait();
            _sender?.DisposeAsync().AsTask().Wait();
            _client?.DisposeAsync().AsTask().Wait();
        }
    }

    /// <summary>
    /// Configuration options for Azure Service Bus.
    /// </summary>
    public class AzureServiceBusOptions
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string QueueName { get; set; } = "payment-requests";
        public int MaxConcurrentCalls { get; set; } = 5;
        public bool AutoCompleteMessages { get; set; } = false;
        public string? MaxAutoLockRenewalDurationString { get; set; }
    }
}

