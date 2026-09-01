namespace FlowableWrapper.Domain.Abstractions;

public sealed class ResourceNotFoundException : Exception
{
    public string Code { get; }

    public ResourceNotFoundException(string message, string code)
        : base(message)
    {
        Code = code;
    }
}
