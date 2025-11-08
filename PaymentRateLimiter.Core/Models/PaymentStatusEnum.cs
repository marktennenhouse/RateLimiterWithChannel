namespace PaymentRateLimiter.Core.Models
{
    public enum PaymentStatusEnum
    {
        Queued,
        Processing,
        SendingToProcessor,
        Completed,
        Failed
    }
}

