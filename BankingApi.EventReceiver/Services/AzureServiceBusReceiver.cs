using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using BankingApi.EventReceiver.Models;

namespace BankingApi.EventReceiver.Services
{
    public class AzureServiceBusReceiver : IServiceBusReceiver, IAsyncDisposable
    {
        private readonly ServiceBusClient Client;
        private readonly ServiceBusReceiver Receiver;
        private readonly ILogger<AzureServiceBusReceiver> Logger;

        public AzureServiceBusReceiver(string connectionString, string queueName, ILogger<AzureServiceBusReceiver> logger)
        {
            Logger = logger;

            try
            {
                Client = new ServiceBusClient(connectionString);
                Receiver = Client.CreateReceiver(queueName, new ServiceBusReceiverOptions
                {
                    ReceiveMode = ServiceBusReceiveMode.PeekLock
                });

                Logger.LogInformation("ServiceBusReceiver initialized for queue: {QueueName}", queueName);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to initialize ServiceBusReceiver for queue: {QueueName}", queueName);
                throw;
            }
        }

        public async Task<EventMessage?> Peek()
        {
            try
            {
                // Receive a single message with short timeout to avoid blocking
                var message = await Receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));

                if (message == null)
                    return null;

                // Generate GUID if MessageId is not a valid GUID
                var messageId = Guid.TryParse(message.MessageId, out var parsedId)
                    ? parsedId
                    : Guid.NewGuid();

                var eventMessage = new EventMessage
                {
                    Id = messageId,
                    MessageBody = message.Body.ToString(),
                    ProcessingCount = message.DeliveryCount - 1, // DeliveryCount starts at 1
                    ServiceBusMessage = message // Store for later operations
                };

                Logger.LogDebug("Received message {MessageId} with delivery count {DeliveryCount}",
                    eventMessage.Id, message.DeliveryCount);

                return eventMessage;
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
            {
                Logger.LogError("Queue not found: {ErrorMessage}", ex.Message);
                throw;
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.ServiceTimeout)
            {
                // Timeout is expected when no messages are available
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error receiving message from Service Bus");
                throw;
            }
        }

        public async Task Abandon(EventMessage message)
        {
            try
            {
                var serviceBusMessage = GetServiceBusMessage(message);
                await Receiver.AbandonMessageAsync(serviceBusMessage);
                Logger.LogDebug("Abandoned message {MessageId}", message.Id);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error abandoning message {MessageId}", message.Id);
                throw;
            }
        }

        public async Task Complete(EventMessage message)
        {
            try
            {
                var serviceBusMessage = GetServiceBusMessage(message);
                await Receiver.CompleteMessageAsync(serviceBusMessage);
                Logger.LogDebug("Completed message {MessageId}", message.Id);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error completing message {MessageId}", message.Id);
                throw;
            }
        }

        public async Task ReSchedule(EventMessage message, DateTime nextAvailableTime)
        {
            try
            {
                var serviceBusMessage = GetServiceBusMessage(message);

                // Calculate delay
                var delay = nextAvailableTime - DateTime.UtcNow;
                if (delay <= TimeSpan.Zero)
                {
                    // If no delay needed, just abandon to retry immediately
                    await Receiver.AbandonMessageAsync(serviceBusMessage);
                    Logger.LogDebug("Abandoned message {MessageId} for immediate retry", message.Id);
                }
                else
                {
                    // For Azure Service Bus, we abandon and the message will be available after the lock expires
                    // The exponential backoff is handled by the MessageWorker's retry logic
                    await Receiver.AbandonMessageAsync(serviceBusMessage);
                    Logger.LogDebug("Abandoned message {MessageId} for retry after {Delay}", message.Id, delay);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error rescheduling message {MessageId}", message.Id);
                throw;
            }
        }

        public async Task MoveToDeadLetter(EventMessage message)
        {
            try
            {
                var serviceBusMessage = GetServiceBusMessage(message);
                var reason = "ProcessingFailed";
                var description = $"Message processing failed after maximum retry attempts. DeliveryCount: {serviceBusMessage.DeliveryCount}";

                await Receiver.DeadLetterMessageAsync(serviceBusMessage, reason, description);
                Logger.LogWarning("Moved message {MessageId} to dead letter queue. Reason: {Reason}", message.Id, reason);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error moving message {MessageId} to dead letter", message.Id);
                throw;
            }
        }

        private static ServiceBusReceivedMessage GetServiceBusMessage(EventMessage message)
        {
            if (message.ServiceBusMessage is not ServiceBusReceivedMessage serviceBusMessage)
            {
                throw new InvalidOperationException($"EventMessage {message.Id} does not contain a valid ServiceBusReceivedMessage");
            }
            return serviceBusMessage;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Receiver != null)
                {
                    await Receiver.DisposeAsync();
                }
                if (Client != null)
                {
                    await Client.DisposeAsync();
                }
                Logger.LogInformation("ServiceBusReceiver disposed successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error disposing ServiceBusReceiver");
            }
        }
    }
}