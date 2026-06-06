namespace FlowableWrapper.Test.ProcessCenter;

public sealed class ProcessCenterOptions
{
    public Uri BaseUri { get; }
    public string? BearerToken { get; init; }

    public ProcessCenterOptions(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("baseUrl cannot be empty.", nameof(baseUrl));

        BaseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }
}
