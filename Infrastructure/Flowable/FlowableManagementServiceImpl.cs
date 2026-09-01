using FlowableWrapper.Domain.Flowable;

namespace FlowableWrapper.Infrastructure.Flowable;

public sealed class FlowableManagementServiceImpl : IFlowableManagementService
{
    private readonly FlowableHttpClient _http;

    public FlowableManagementServiceImpl(FlowableHttpClient http)
    {
        _http = http;
    }

    public Task<FlowableDeadLetterJobPage> QueryDeadLetterJobsAsync(
        int start,
        int size,
        string? processInstanceId = null)
    {
        var path = $"management/deadletter-jobs?start={Math.Max(0, start)}&size={Math.Clamp(size, 1, 100)}";
        if (!string.IsNullOrWhiteSpace(processInstanceId))
        {
            path += $"&processInstanceId={Uri.EscapeDataString(processInstanceId)}";
        }

        return _http.GetAsync<FlowableDeadLetterJobPage>(path);
    }

    public Task<FlowableDeadLetterJob?> GetDeadLetterJobAsync(string jobId)
        => _http.TryGetAsync<FlowableDeadLetterJob>(
            $"management/deadletter-jobs/{Uri.EscapeDataString(jobId)}");

    public Task<string?> GetDeadLetterJobExceptionStackTraceAsync(string jobId)
        => _http.TryGetStringAsync(
            $"management/deadletter-jobs/{Uri.EscapeDataString(jobId)}/exception-stacktrace");

    public Task MoveDeadLetterJobToExecutableAsync(string jobId)
        => _http.PostAsync(
            $"management/deadletter-jobs/{Uri.EscapeDataString(jobId)}",
            new { action = "move" });
}
