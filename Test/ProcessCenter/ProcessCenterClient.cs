using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FlowableWrapper.Test.ProcessCenter;

public sealed class ProcessCenterClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public ProcessCenterClient(HttpClient httpClient, ProcessCenterOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);

        _httpClient.BaseAddress ??= options.BaseUri;
        if (!string.IsNullOrWhiteSpace(options.BearerToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.BearerToken);
    }

    public async Task<StartProcessResponse> StartProcessAsync(
        StartProcessRequest request,
        string operatorEmployeeId,
        CancellationToken cancellationToken = default)
        => await PostEnvelopeAsync<StartProcessRequest, StartProcessResponse>(
            "api/processes/start", request, operatorEmployeeId, cancellationToken);

    public async Task<CompleteTaskResponse> CompleteTaskAsync(
        CompleteTaskRequest request,
        string operatorEmployeeId,
        CancellationToken cancellationToken = default)
        => await PostEnvelopeAsync<CompleteTaskRequest, CompleteTaskResponse>(
            "api/tasks/complete", request, operatorEmployeeId, cancellationToken);

    public async Task<PendingTaskPageResult> GetPendingTasksAsync(
        string employeeId,
        string? businessType = null,
        int pageIndex = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/tasks/pending?employeeId={Uri.EscapeDataString(employeeId)}" +
                  $"&pageIndex={pageIndex}&pageSize={pageSize}";

        if (!string.IsNullOrWhiteSpace(businessType))
            url += $"&businessType={Uri.EscapeDataString(businessType)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-User-Id", employeeId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadEnvelopeAsync<PendingTaskPageResult>(response, cancellationToken);
    }

    private async Task<TResponse> PostEnvelopeAsync<TRequest, TResponse>(
        string url,
        TRequest body,
        string operatorEmployeeId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("X-User-Id", operatorEmployeeId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadEnvelopeAsync<TResponse>(response, cancellationToken);
    }

    private static async Task<T> ReadEnvelopeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ProcessCenterClientException(
                $"Process center HTTP {(int)response.StatusCode}: {raw}");

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(raw, JsonOptions);
        if (envelope == null)
            throw new ProcessCenterClientException("Process center returned an empty response.");

        if (!envelope.Success)
            throw new ProcessCenterClientException(
                envelope.Message ?? "Process center returned success=false.");

        if (envelope.Data == null)
            throw new ProcessCenterClientException("Process center response data is empty.");

        return envelope.Data;
    }
}

public sealed class ProcessCenterClientException : Exception
{
    public ProcessCenterClientException(string message) : base(message)
    {
    }
}
