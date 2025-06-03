namespace BankingApi.EventReceiver.Models
{
    public class TransactionMessage
    {
        public Guid Id { get; set; }
        public string MessageType { get; set; } = string.Empty;
        public Guid BankAccountId { get; set; }
        public decimal Amount { get; set; }
    }
}
