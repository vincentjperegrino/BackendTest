using Azure.Messaging.ServiceBus;

namespace BankingApi.EventReceiver.Models
{
    public class EventMessage
    {
        public Guid Id { get; set; }
        public string? MessageBody { get; set; }
        public int ProcessingCount { get; set; }

        // Internal property to store the actual Service Bus message
        internal ServiceBusReceivedMessage? ServiceBusMessage { get; set; }
    }
}