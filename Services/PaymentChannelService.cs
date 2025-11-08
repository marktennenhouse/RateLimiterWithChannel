using PaymentChannelDemo.Models;
using System.Threading.Channels;

namespace PaymentChannelDemo.Services
{
    /// <summary>
    /// Manages the main payment queue using a bounded channel for backpressure protection.
    /// This is a singleton service shared between the controller (writer) and worker (reader).
    /// </summary>
    public class PaymentChannelService
    {
        private readonly Channel<PaymentRequest> _paymentChannel;

        public PaymentChannelService()
        {
            // Use bounded channel with capacity of 1000 for backpressure protection
            // When full, WriteAsync will wait until space is available
            _paymentChannel = Channel.CreateBounded<PaymentRequest>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public ChannelWriter<PaymentRequest> Writer => _paymentChannel.Writer;
        public ChannelReader<PaymentRequest> Reader => _paymentChannel.Reader;
    }
}
