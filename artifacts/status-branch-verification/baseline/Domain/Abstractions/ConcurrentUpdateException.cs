namespace process.Domain.Abstractions
{
    public class ConcurrentUpdateException : Exception
    {
        public string Code { get; }

        public ConcurrentUpdateException(string message, string code = "CONCURRENT_UPDATE")
            : base(message)
        {
            Code = code;
        }
    }
}
