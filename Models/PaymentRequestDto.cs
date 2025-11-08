namespace PaymentChannelDemo.Models
{
    /// <summary>
    /// DTO for incoming payment requests from clients
    /// </summary>
    public class PaymentRequestDto
    {
        public decimal Amount { get; set; }
        public string CardToken { get; set; } = string.Empty;
        public string? CustomerEmail { get; set; }
    }
}

