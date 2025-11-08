namespace PaymentRateLimiter.Core.Models
{
    public class PaymentStatus
    {
        public string PaymentId { get; set; } = string.Empty;
        public PaymentStatusEnum Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public int? QueuePosition { get; set; }
        public DateTime Timestamp { get; set; }
    }
}

