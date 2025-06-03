using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using BankingApi.EventReceiver.Database;
using BankingApi.EventReceiver.Services;
using BankingApi.EventReceiver.Consumers;
using BankingApi.EventReceiver.Models;

namespace BankingApi.EventReceiver
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Add configuration
            builder.Configuration.AddJsonFile("appsettings.json", optional: false);
            builder.Configuration.AddEnvironmentVariables();

            // Add Db context
            var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                ?? throw new NullReferenceException("DefaultConnection config not found");
            builder.Services.AddDbContext<BankingApiDbContext>(options =>
                options.UseSqlServer(connectionString));

            // Add Azure Service Bus
            builder.Services.AddSingleton<IServiceBusReceiver>(provider =>
            {
                var serviceBusConnectionString = builder.Configuration.GetConnectionString("ServiceBus")
                    ?? throw new NullReferenceException("ServiceBus config not found");
                var queueName = builder.Configuration["ServiceBus:QueueName"]
                    ?? throw new NullReferenceException("ServiceBus:QueueName config not found");
                return new AzureServiceBusReceiver(
                    serviceBusConnectionString,
                    queueName,
                    provider.GetRequiredService<ILogger<AzureServiceBusReceiver>>()
                );
            });

            // Add the message worker
            builder.Services.AddTransient<MessageWorker>();

            // Add logging
            builder.Services.AddLogging(config =>
            {
                config.AddConsole();
                config.AddDebug();
            });

            var host = builder.Build();

            using (var migrationScope = host.Services.CreateScope())
            {
                var context = migrationScope.ServiceProvider.GetRequiredService<BankingApiDbContext>();
                await context.Database.MigrateAsync();

                // Seed bank accounts if they don't exist
                var creditAccountId = Guid.Parse("7d445724-24ec-4d52-aa7a-ff2bac9f191d");
                var debitAccountId = Guid.Parse("3bbaf4ca-5bfa-4922-a395-d755beac475f");

                if (!await context.BankAccounts.AnyAsync(b => b.Id == creditAccountId))
                {
                    context.BankAccounts.Add(new BankAccount
                    {
                        Id = creditAccountId,
                        Balance = 1000.00m // Initial balance
                    });
                }

                if (!await context.BankAccounts.AnyAsync(b => b.Id == debitAccountId))
                {
                    context.BankAccounts.Add(new BankAccount
                    {
                        Id = debitAccountId,
                        Balance = 500.00m // Initial balance
                    });
                }

                await context.SaveChangesAsync();

                var logger = migrationScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("Database migration and seeding completed successfully");
            }

            await RunWorkerAsync(host);
        }

        private static async Task RunWorkerAsync(IHost host)
        {
            Console.WriteLine("Starting Banking Event Receiver...");
            Console.WriteLine("Press Ctrl+C to stop the application");

            using var scope = host.Services.CreateScope();
            var messageWorker = scope.ServiceProvider.GetRequiredService<MessageWorker>();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\nShutdown requested...");
            };

            try
            {
                var workerTask = messageWorker.Start();
                var shutdownTask = Task.Delay(Timeout.Infinite, cts.Token);

                await Task.WhenAny(workerTask, shutdownTask);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Application stopped gracefully");
            }
            catch (Exception ex)
            {
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "Application terminated unexpectedly");
                throw;
            }
            finally
            {
                if (scope.ServiceProvider.GetService<IServiceBusReceiver>() is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync();
                }
                Console.WriteLine("Banking Event Receiver stopped");
            }
        }
    }
}