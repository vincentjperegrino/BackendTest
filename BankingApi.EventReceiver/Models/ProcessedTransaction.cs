namespace BankingApi.EventReceiver.Models
{
    public class ProcessedTransaction
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TransactionId { get; set; }
        public Guid BankAccountId { get; set; }
        public string MessageType { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime ProcessedAt { get; set; }
        public decimal PreviousBalance { get; set; }
        public decimal NewBalance { get; set; }
    }
}
