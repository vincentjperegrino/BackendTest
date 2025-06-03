using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using BankingApi.EventReceiver.Database;
using BankingApi.EventReceiver.Services;
using BankingApi.EventReceiver.Consumers;

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
                var logger = migrationScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("Database migration completed successfully");
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