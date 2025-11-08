namespace PaymentChannelDemo.Models
{
    public class PaymentRequest
    {
        public string PaymentId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string CardToken { get; set; } = string.Empty;
        public string? CustomerEmail { get; set; }
        public DateTime QueuedAt { get; set; }
        public bool IsDisconnected { get; set; }
        public DateTime? DisconnectedAt { get; set; }
        public string? ClientIpAddress { get; set; }
        public int RetryCount { get; set; }
    }
}

