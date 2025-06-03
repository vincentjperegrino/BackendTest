using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using BankingApi.EventReceiver.Models;
using Microsoft.Extensions.DependencyInjection;
using BankingApi.EventReceiver.Database;
using BankingApi.EventReceiver.Services;

namespace BankingApi.EventReceiver.Consumers
{
    public class MessageWorker
    {
        private readonly IServiceBusReceiver ServiceBusReceiver;
        private readonly IServiceProvider ServiceProvider;
        private readonly ILogger<MessageWorker> Logger;
        private readonly CancellationTokenSource CancellationTokenSource;
        private volatile bool IsRunning;

        // Retry delays for transient failures (5, 25, 125 seconds)
        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(25),
            TimeSpan.FromSeconds(125)
        };

        public MessageWorker(
            IServiceBusReceiver serviceBusReceiver,
            IServiceProvider serviceProvider,
            ILogger<MessageWorker> logger)
        {
            ServiceBusReceiver = serviceBusReceiver ?? throw new ArgumentNullException(nameof(serviceBusReceiver));
            ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            CancellationTokenSource = new CancellationTokenSource();
        }

        public async Task Start()
        {
            IsRunning = true;
            Logger.LogInformation("MessageWorker started");

            try
            {
                while (IsRunning && !CancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        var message = await ServiceBusReceiver.Peek();

                        if (message == null)
                        {
                            // No messages available, wait 10 seconds
                            await Task.Delay(TimeSpan.FromSeconds(10), CancellationTokenSource.Token);
                            continue;
                        }

                        await ProcessMessage(message);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogInformation("MessageWorker operation cancelled");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Unexpected error in message processing loop");
                        // Brief delay to prevent tight loop on persistent errors
                        await Task.Delay(TimeSpan.FromSeconds(1), CancellationTokenSource.Token);
                    }
                }
            }
            finally
            {
                IsRunning = false;
                Logger.LogInformation("MessageWorker stopped");
            }
        }

        private async Task ProcessMessage(EventMessage message)
        {
            try
            {
                Logger.LogDebug("Processing message {MessageId}, attempt {ProcessingCount}",
                    message.Id, message.ProcessingCount + 1);

                // Parse the message
                var transactionMessage = ParseMessage(message.MessageBody);

                if (transactionMessage == null)
                {
                    Logger.LogWarning("Failed to parse message {MessageId}, moving to dead letter", message.Id);
                    await ServiceBusReceiver.MoveToDeadLetter(message);
                    return;
                }

                // Validate message type
                if (!IsValidMessageType(transactionMessage.MessageType))
                {
                    Logger.LogWarning("Invalid message type {MessageType} for message {MessageId}, moving to dead letter",
                        transactionMessage.MessageType, message.Id);
                    await ServiceBusReceiver.MoveToDeadLetter(message);
                    return;
                }

                // Process the transaction
                await ProcessTransaction(transactionMessage);

                // Mark message as completed
                await ServiceBusReceiver.Complete(message);

                Logger.LogDebug("Successfully processed message {MessageId}", message.Id);
            }
            catch (NonTransientException ex)
            {
                Logger.LogError(ex, "Non-transient error processing message {MessageId}, moving to dead letter", message.Id);
                await ServiceBusReceiver.MoveToDeadLetter(message);
            }
            catch (Exception ex)
            {
                await HandleTransientFailure(message, ex);
            }
        }

        private TransactionMessage? ParseMessage(string? messageBody)
        {
            if (string.IsNullOrWhiteSpace(messageBody))
                return null;

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                return JsonSerializer.Deserialize<TransactionMessage>(messageBody, options);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Failed to deserialize message: {MessageBody}", messageBody);
                return null;
            }
        }

        private static bool IsValidMessageType(string? messageType)
        {
            return messageType is "Credit" or "Debit";
        }

        private async Task ProcessTransaction(TransactionMessage transactionMessage)
        {
            using var scope = ServiceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<BankingApiDbContext>();

            try
            {
                using var transaction = await dbContext.Database.BeginTransactionAsync();

                // Check if transaction was already processed (idempotency)
                var existingRecord = await dbContext.ProcessedTransactions
                    .FirstOrDefaultAsync(pt => pt.TransactionId == transactionMessage.Id);

                if (existingRecord != null)
                {
                    Logger.LogInformation("Transaction {TransactionId} already processed, skipping", transactionMessage.Id);
                    await transaction.CommitAsync();
                    return;
                }

                // Get bank account with row-level locking
                var bankAccount = await dbContext.BankAccounts
                    .Where(ba => ba.Id == transactionMessage.BankAccountId)
                    .FirstOrDefaultAsync();

                if (bankAccount == null)
                {
                    throw new NonTransientException($"Bank account {transactionMessage.BankAccountId} not found");
                }

                // Calculate new balance
                var newBalance = transactionMessage.MessageType switch
                {
                    "Credit" => bankAccount.Balance + transactionMessage.Amount,
                    "Debit" => bankAccount.Balance - transactionMessage.Amount,
                    _ => throw new NonTransientException($"Unsupported message type: {transactionMessage.MessageType}")
                };

                // Validate business rules
                if (transactionMessage.MessageType == "Debit" && newBalance < 0)
                {
                    throw new NonTransientException($"Insufficient funds. Current balance: {bankAccount.Balance}, Debit amount: {transactionMessage.Amount}");
                }

                // Update balance
                bankAccount.Balance = newBalance;

                // Record the processed transaction for idempotency
                dbContext.ProcessedTransactions.Add(new ProcessedTransaction
                {
                    TransactionId = transactionMessage.Id,
                    BankAccountId = transactionMessage.BankAccountId,
                    MessageType = transactionMessage.MessageType,
                    Amount = transactionMessage.Amount,
                    ProcessedAt = DateTime.UtcNow,
                    PreviousBalance = bankAccount.Balance - (transactionMessage.MessageType == "Credit" ? transactionMessage.Amount : -transactionMessage.Amount),
                    NewBalance = newBalance
                });

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                Logger.LogInformation("Processed {MessageType} transaction {TransactionId} for account {BankAccountId}. Amount: {Amount}, New Balance: {NewBalance}",
                    transactionMessage.MessageType, transactionMessage.Id, transactionMessage.BankAccountId, transactionMessage.Amount, newBalance);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                Logger.LogWarning(ex, "Concurrency conflict processing transaction {TransactionId}, will retry", transactionMessage.Id);
                throw; // This will be treated as transient
            }
            catch (Exception ex) when (IsTransientDatabaseError(ex))
            {
                Logger.LogWarning(ex, "Transient database error processing transaction {TransactionId}, will retry", transactionMessage.Id);
                throw;
            }
        }

        private async Task HandleTransientFailure(EventMessage message, Exception ex)
        {
            Logger.LogWarning(ex, "Transient error processing message {MessageId}, processing count: {ProcessingCount}",
                message.Id, message.ProcessingCount);

            // Check if we've exceeded maximum retry attempts
            if (message.ProcessingCount >= RetryDelays.Length)
            {
                Logger.LogError("Message {MessageId} exceeded maximum retry attempts, moving to dead letter", message.Id);
                await ServiceBusReceiver.MoveToDeadLetter(message);
                return;
            }

            // Schedule retry with exponential backoff
            var retryDelay = RetryDelays[message.ProcessingCount];
            var nextAvailableTime = DateTime.UtcNow.Add(retryDelay);

            Logger.LogInformation("Scheduling retry for message {MessageId} in {RetryDelay} seconds",
                message.Id, retryDelay.TotalSeconds);

            await ServiceBusReceiver.ReSchedule(message, nextAvailableTime);
        }

        private static bool IsTransientDatabaseError(Exception ex)
        {
            // Check for common transient database errors
            return ex is DbUpdateConcurrencyException ||
                   ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase);
        }

        public void Stop()
        {
            IsRunning = false;
            CancellationTokenSource.Cancel();
        }

        public void Dispose()
        {
            CancellationTokenSource?.Dispose();
        }
    }
}