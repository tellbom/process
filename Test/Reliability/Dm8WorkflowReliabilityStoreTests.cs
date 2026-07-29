using Dm;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Reliability;
using FlowableWrapper.Infrastructure.Dm8;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlowableWrapper.Test.Reliability;

[Collection("DM8 serial")]
public class Dm8WorkflowReliabilityStoreTests
{
    [Dm8Fact]
    public async Task Definition_config_is_versioned_and_immutable()
    {
        var store = CreateStore();
        var key = $"definition-{Guid.NewGuid():N}";
        var config = new WorkflowDefinitionConfig
        {
            ProcessDefinitionKey = key,
            ProcessDefinitionVersion = 1,
            ProcessDefinitionId = $"{key}:1:flowable-id",
            ContentHash = new string('a', 64),
            ConfigJson = "{\"node\":{\"canReject\":true}}"
        };

        await store.SaveDefinitionConfigAsync(config);
        await store.SaveDefinitionConfigAsync(config);
        var loaded = await store.GetDefinitionConfigAsync(key, 1);

        Assert.NotNull(loaded);
        Assert.Equal(config.ProcessDefinitionId, loaded!.ProcessDefinitionId);
        Assert.Equal(config.ConfigJson, loaded.ConfigJson);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.SaveDefinitionConfigAsync(new WorkflowDefinitionConfig
            {
                ProcessDefinitionKey = key,
                ProcessDefinitionVersion = 1,
                ProcessDefinitionId = config.ProcessDefinitionId,
                ContentHash = new string('b', 64),
                ConfigJson = "{}"
            }));
    }

    [Dm8Fact]
    public async Task Task_action_prepare_is_idempotent_and_result_converges()
    {
        var store = CreateStore();
        var suffix = Guid.NewGuid().ToString("N");
        var command = new PrepareTaskActionCommand
        {
            ActionId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"complete:task-{suffix}",
            BusinessId = $"business-action:{suffix}",
            ProcessInstanceId = $"process-action:{suffix}",
            TaskId = $"task-{suffix}",
            TaskDefinitionKey = "approve",
            ActionType = "complete",
            OperatorId = "196045",
            RequestJson = "{\"approved\":true}"
        };

        var prepared = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => store.PrepareTaskActionAsync(command)));
        Assert.Single(prepared.Select(x => x.ActionId).Distinct());

        await store.MarkTaskActionResultAsync(
            command.ActionId, "applied", "completed", null);
        var duplicate = await store.PrepareTaskActionAsync(command);
        Assert.Equal("applied", duplicate.ResultState);
        Assert.Equal("completed", duplicate.FlowableResult);
    }

    [Dm8Fact]
    public async Task Configured_callback_binding_round_trips_for_http_acceptance()
    {
        var store = CreateStore();
        var suffix = Environment.GetEnvironmentVariable("FLOW_DM8_E2E_ID")
                     ?? Guid.NewGuid().ToString("N");
        var businessId = $"http-business-{suffix}";
        var processInstanceId = $"http-process-{suffix}";
        var callbackUrl = Environment.GetEnvironmentVariable(
                              "FLOW_DM8_E2E_CALLBACK_URL")
                          ?? "http://127.0.0.1:5012/api/test/process-callback/B?delayMs=5000";
        var snapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            url = callbackUrl,
            timeoutSeconds = 10,
            retryCount = 0,
            headers = new Dictionary<string, string>()
        });

        var reservation = await store.ReserveBusinessAsync(
            new ReserveBusinessCommand
            {
                BusinessId = businessId,
                BusinessType = "portal_content_approval",
                ProcessDefinitionKey = "portal_content_approval",
                CallbackConfigSnapshot = snapshot
            });
        if (reservation.Created)
        {
            await store.BindStartedProcessAsync(
                businessId,
                processInstanceId,
                1);
        }

        var loaded = await store.GetBusinessByProcessInstanceAsync(
            processInstanceId);
        Assert.NotNull(loaded);
        Assert.Equal(snapshot, loaded!.CallbackConfigSnapshot);
    }

    [Dm8Fact]
    public async Task Business_reservation_is_unique_and_binding_round_trips()
    {
        var store = CreateStore();
        var suffix = Guid.NewGuid().ToString("N");
        var command = new ReserveBusinessCommand
        {
            BusinessId = $"business-binding:{suffix}",
            BusinessType = "portal_content_approval",
            ProcessDefinitionKey = "portal_content_approval",
            CallbackConfigSnapshot = "{\"url\":\"http://localhost/callback\"}"
        };

        var reservations = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => store.ReserveBusinessAsync(command)));

        Assert.Single(reservations.Where(x => x.Created));
        await store.BindStartedProcessAsync(
            command.BusinessId,
            $"process-binding:{suffix}",
            7);
        var binding = await store.GetBusinessByProcessInstanceAsync(
            $"process-binding:{suffix}");

        Assert.NotNull(binding);
        Assert.Equal(7, binding!.ProcessDefinitionVersion);
        Assert.Equal("running", binding.FlowState);
        Assert.Equal(command.CallbackConfigSnapshot, binding.CallbackConfigSnapshot);
    }

    [Dm8Fact]
    public async Task Duplicate_callback_is_persisted_once_and_clob_round_trips()
    {
        var store = CreateStore();
        var suffix = Guid.NewGuid().ToString("N");
        var command = new EnqueueCallbackCommand
        {
            EventId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"test:{suffix}",
            BusinessId = $"business:{suffix}",
            ProcessInstanceId = $"process:{suffix}",
            CallbackActivityId = "st03_framework_callback",
            CallbackType = "process_completed",
            Payload = "{\"large\":\"" + new string('测', 5000) + "\"}"
        };

        var writes = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => store.EnqueueCallbackAsync(command)));

        Assert.Single(writes.Select(x => x.EventId).Distinct());
        var loaded = await store.GetCallbackByIdempotencyKeyAsync(
            command.IdempotencyKey);
        Assert.NotNull(loaded);
        Assert.Equal(command.Payload, loaded!.Payload);
    }

    [Dm8Fact]
    public async Task Lease_is_exclusive_between_workers()
    {
        await DeleteLeasableTestCallbacksAsync();
        var store = CreateStore();
        var suffix = Guid.NewGuid().ToString("N");
        await store.EnqueueCallbackAsync(new EnqueueCallbackCommand
        {
            EventId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"lease:{suffix}",
            BusinessId = $"business:{suffix}",
            ProcessInstanceId = $"process:{suffix}",
            CallbackActivityId = "st03_framework_callback",
            CallbackType = "process_completed",
            Payload = "{}"
        });

        var leases = await Task.WhenAll(
            store.LeaseCallbacksAsync("worker-a", 10, TimeSpan.FromMinutes(1)),
            store.LeaseCallbacksAsync("worker-b", 10, TimeSpan.FromMinutes(1)));

        Assert.Equal(
            1,
            leases.SelectMany(x => x)
                .Count(x => x.IdempotencyKey == $"lease:{suffix}"));
    }

    [Dm8Fact]
    public async Task Expired_processing_lease_is_recovered_after_worker_restart()
    {
        await DeleteLeasableTestCallbacksAsync();
        var store = CreateStore();
        var suffix = Guid.NewGuid().ToString("N");
        var inserted = await store.EnqueueCallbackAsync(new EnqueueCallbackCommand
        {
            EventId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"restart:{suffix}",
            BusinessId = $"business:{suffix}",
            ProcessInstanceId = $"process:{suffix}",
            CallbackActivityId = "st03_framework_callback",
            CallbackType = "process_completed",
            Payload = "{}"
        });
        var firstLease = await store.LeaseCallbacksAsync(
            $"crashed-worker:{suffix}",
            100,
            TimeSpan.FromMilliseconds(100));
        Assert.Contains(firstLease, x => x.EventId == inserted.EventId);

        await Task.Delay(200);
        var recovered = await store.LeaseCallbacksAsync(
            $"replacement-worker:{suffix}",
            100,
            TimeSpan.FromMinutes(1));

        Assert.Contains(recovered, x => x.EventId == inserted.EventId);
    }

    [Dm8Fact]
    public async Task Failed_lease_can_be_dead_lettered_and_manually_retried()
    {
        await DeleteLeasableTestCallbacksAsync();
        var store = CreateStore();
        var suffix = Guid.NewGuid().ToString("N");
        var inserted = await store.EnqueueCallbackAsync(new EnqueueCallbackCommand
        {
            EventId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"dead:{suffix}",
            BusinessId = $"business:{suffix}",
            ProcessInstanceId = $"process:{suffix}",
            CallbackActivityId = "st03_framework_callback",
            CallbackType = "process_completed",
            Payload = "{}"
        });
        var leased = await store.LeaseCallbacksAsync(
            $"worker:{suffix}",
            100,
            TimeSpan.FromMinutes(1));
        Assert.Contains(leased, x => x.EventId == inserted.EventId);

        await store.MarkCallbackFailedAsync(
            inserted.EventId,
            $"worker:{suffix}",
            new CallbackRetryDecision(CallbackEventStatus.DeadLetter, null),
            400,
            "bad request");
        Assert.True(await store.RetryDeadLetterAsync(inserted.EventId));

        var retried = await store.GetCallbackByEventIdAsync(inserted.EventId);
        Assert.Equal(CallbackEventStatus.Pending, retried!.Status);
        Assert.Equal(0, retried.AttemptCount);
    }

    private static Dm8WorkflowReliabilityStore CreateStore()
        => new(Options.Create(new Dm8Options
        {
            ConnectionString = Environment.GetEnvironmentVariable(
                "FLOW_DM8_TEST_CONNECTION_STRING")!,
            Schema = "FLOW_RELIABILITY",
            CommandTimeoutSeconds = 30
        }));

    private static async Task DeleteLeasableTestCallbacksAsync()
    {
        await using var connection = new DmConnection(
            Environment.GetEnvironmentVariable(
                "FLOW_DM8_TEST_CONNECTION_STRING"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
DELETE FROM FLOW_RELIABILITY.WORKFLOW_CALLBACK_EVENT
WHERE BUSINESS_ID LIKE 'business:%'";
        await command.ExecuteNonQueryAsync();
    }
}
