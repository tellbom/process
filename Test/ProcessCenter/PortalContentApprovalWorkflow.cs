namespace FlowableWrapper.Test.ProcessCenter;

public sealed class PortalContentApprovalWorkflow
{
    public const string BusinessType = "portal_content_approval";
    public const string StarterRoleKey = "portal_content_starter";
    public const string LeaderRoleKey = "portal_content_leader";
    public const string LeaderSlotKey = "portal_leader";

    private readonly ProcessCenterClient _client;

    public PortalContentApprovalWorkflow(ProcessCenterClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<StartProcessResponse> StartAsync(
        string businessId,
        string starterEmployeeId,
        IReadOnlyCollection<string> recommendedLeaderEmployeeIds,
        string processCompletedCallbackUrl,
        CancellationToken cancellationToken = default)
    {
        var leaders = NormalizeUsers(recommendedLeaderEmployeeIds, nameof(recommendedLeaderEmployeeIds));

        var request = new StartProcessRequest
        {
            BusinessType = BusinessType,
            BusinessId = businessId,
            InitialSlotSelections =
            {
                new SlotSelection { SlotKey = LeaderSlotKey, Users = leaders }
            },
            BusinessVariables =
            {
                ["starterAssignee"] = starterEmployeeId
            },
            AssigneeContract = new AssigneeContract
            {
                Roles =
                {
                    new RoleAssignment
                    {
                        RoleKey = StarterRoleKey,
                        Mode = "single",
                        Users = new List<string> { starterEmployeeId }
                    },
                    new RoleAssignment
                    {
                        RoleKey = LeaderRoleKey,
                        Mode = "multiple",
                        Users = leaders
                    }
                }
            },
            Callback = new CallbackConfig
            {
                Url = processCompletedCallbackUrl,
                TimeoutSeconds = 30,
                RetryCount = 1
            }
        };

        return await _client.StartProcessAsync(
            request, starterEmployeeId, cancellationToken);
    }

    public async Task<CompleteTaskResponse> CompleteStarterNodeAsync(
        string businessId,
        string starterTaskId,
        string starterEmployeeId,
        IReadOnlyCollection<string> selectedLeaderEmployeeIds,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var leaders = NormalizeUsers(selectedLeaderEmployeeIds, nameof(selectedLeaderEmployeeIds));

        var request = new CompleteTaskRequest
        {
            BusinessId = businessId,
            TaskId = starterTaskId,
            EmployeeId = starterEmployeeId,
            Action = ApprovalAction.Approve,
            Comment = comment ?? "发起人提交门户资讯审批",
            NextSlotSelections =
            {
                new SlotSelection { SlotKey = LeaderSlotKey, Users = leaders }
            }
        };

        return await _client.CompleteTaskAsync(
            request, starterEmployeeId, cancellationToken);
    }

    public async Task<StartAndCompleteStarterResult> StartAndCompleteStarterAsync(
        string businessId,
        string starterEmployeeId,
        IReadOnlyCollection<string> leaderEmployeeIds,
        string processCompletedCallbackUrl,
        CancellationToken cancellationToken = default)
    {
        var start = await StartAsync(
            businessId,
            starterEmployeeId,
            leaderEmployeeIds,
            processCompletedCallbackUrl,
            cancellationToken);

        var complete = await CompleteStarterNodeAsync(
            start.BusinessId,
            start.FirstTaskId,
            starterEmployeeId,
            leaderEmployeeIds,
            cancellationToken: cancellationToken);

        return new StartAndCompleteStarterResult(start, complete);
    }

    public async Task<CompleteTaskResponse> ApproveLeaderTaskAsync(
        string businessId,
        string leaderTaskId,
        string leaderEmployeeId,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var request = new CompleteTaskRequest
        {
            BusinessId = businessId,
            TaskId = leaderTaskId,
            EmployeeId = leaderEmployeeId,
            Action = ApprovalAction.Approve,
            Comment = comment ?? "领导审批通过"
        };

        return await _client.CompleteTaskAsync(
            request, leaderEmployeeId, cancellationToken);
    }

    public async Task<CompleteTaskResponse> RejectLeaderTaskToStarterAsync(
        string businessId,
        string leaderTaskId,
        string leaderEmployeeId,
        string rejectReason,
        CancellationToken cancellationToken = default)
    {
        var request = new CompleteTaskRequest
        {
            BusinessId = businessId,
            TaskId = leaderTaskId,
            EmployeeId = leaderEmployeeId,
            Action = ApprovalAction.Reject,
            RejectCode = "TO_STARTER",
            RejectReason = rejectReason,
            Comment = rejectReason
        };

        return await _client.CompleteTaskAsync(
            request, leaderEmployeeId, cancellationToken);
    }

    private static List<string> NormalizeUsers(
        IReadOnlyCollection<string> users,
        string parameterName)
    {
        var normalized = users
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            throw new ArgumentException("At least one employee id is required.", parameterName);

        return normalized;
    }
}

public sealed record StartAndCompleteStarterResult(
    StartProcessResponse Start,
    CompleteTaskResponse CompleteStarter);
