using BankingApi.EventReceiver.Models;
using Microsoft.EntityFrameworkCore;

namespace BankingApi.EventReceiver.Database
{
    public class BankingApiDbContext : DbContext
    {
        public DbSet<BankAccount> BankAccounts { get; set; }
        public DbSet<ProcessedTransaction> ProcessedTransactions { get; set; }

        public BankingApiDbContext(DbContextOptions<BankingApiDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BankAccount>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Balance)
                    .HasPrecision(18, 2);
            });

            modelBuilder.Entity<ProcessedTransaction>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.TransactionId)
                    .IsUnique()
                    .HasDatabaseName("IX_ProcessedTransaction_TransactionId");
                entity.Property(e => e.Amount)
                    .HasPrecision(18, 2);
                entity.Property(e => e.PreviousBalance)
                    .HasPrecision(18, 2);
                entity.Property(e => e.NewBalance)
                    .HasPrecision(18, 2);
                entity.Property(e => e.MessageType)
                    .HasMaxLength(50)
                    .IsRequired();
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}