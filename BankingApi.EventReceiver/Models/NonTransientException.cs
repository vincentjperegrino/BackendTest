namespace BankingApi.EventReceiver.Models
{
    public class NonTransientException : Exception
    {
        public NonTransientException(string message) : base(message) { }
        public NonTransientException(string message, Exception innerException) : base(message, innerException) { }
    }
}
