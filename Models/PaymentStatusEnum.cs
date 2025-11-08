namespace PaymentChannelDemo.Models
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

