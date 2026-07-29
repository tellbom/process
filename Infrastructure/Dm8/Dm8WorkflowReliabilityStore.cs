using System.Data;
using System.Data.Common;
using Dm;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Reliability;
using Microsoft.Extensions.Options;

namespace FlowableWrapper.Infrastructure.Dm8;

public sealed class Dm8WorkflowReliabilityStore : IWorkflowReliabilityStore
{
    private readonly Dm8Options _options;
    private readonly string _callbackTable;
    private readonly string _businessTable;
    private readonly string _leaseTable;
    private readonly string _definitionTable;
    private readonly string _actionTable;

    public Dm8WorkflowReliabilityStore(IOptions<Dm8Options> options)
    {
        _options = options.Value;
        var schema = ValidateIdentifier(_options.Schema);
        _callbackTable = $"{schema}.WORKFLOW_CALLBACK_EVENT";
        _businessTable = $"{schema}.WORKFLOW_BUSINESS_INSTANCE";
        _leaseTable = $"{schema}.WORKFLOW_CALLBACK_LEASE";
        _definitionTable = $"{schema}.WORKFLOW_DEFINITION_CONFIG";
        _actionTable = $"{schema}.WORKFLOW_TASK_ACTION";
    }

    public async Task<BusinessReservation> ReserveBusinessAsync(
        ReserveBusinessCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var insert = connection.CreateCommand();
            Configure(insert);
            insert.CommandText = $@"
INSERT INTO {_businessTable}
(
    BUSINESS_ID, BUSINESS_TYPE, PROCESS_DEFINITION_KEY, FLOW_STATE,
    CALLBACK_STATE, CALLBACK_CONFIG_SNAPSHOT, ROW_VERSION, DATA_VERSION,
    CREATED_AT, UPDATED_AT
)
VALUES
(
    :business_id, :business_type, :definition_key, 'starting',
    'not_requested', :callback_snapshot, 0, 0,
    CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
)";
            AddParameter(insert, "business_id", command.BusinessId);
            AddParameter(insert, "business_type", command.BusinessType);
            AddParameter(insert, "definition_key", command.ProcessDefinitionKey);
            AddParameter(insert, "callback_snapshot", command.CallbackConfigSnapshot, DbType.String);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            var existing = await GetBusinessByBusinessIdAsync(
                command.BusinessId,
                cancellationToken);
            if (existing != null)
                return new BusinessReservation(false, existing);
            throw;
        }

        return new BusinessReservation(
            true,
            (await GetBusinessByBusinessIdAsync(
                command.BusinessId,
                cancellationToken))!);
    }

    public async Task BindStartedProcessAsync(
        string businessId,
        string processInstanceId,
        int? processDefinitionVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
UPDATE {_businessTable}
SET PROCESS_INSTANCE_ID = :process_instance_id,
    PROCESS_DEFINITION_VERSION = :definition_version,
    FLOW_STATE = 'running',
    UPDATED_AT = CURRENT_TIMESTAMP,
    ROW_VERSION = ROW_VERSION + 1,
    DATA_VERSION = DATA_VERSION + 1
WHERE BUSINESS_ID = :business_id
  AND FLOW_STATE IN ('starting', 'reconcile_required')
  AND PROCESS_INSTANCE_ID IS NULL";
        AddParameter(command, "process_instance_id", processInstanceId);
        AddParameter(command, "definition_version", processDefinitionVersion);
        AddParameter(command, "business_id", businessId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new DBConcurrencyException(
                $"Unable to bind started process for business '{businessId}'.");
    }

    public async Task MarkBusinessFlowStateAsync(
        string businessId,
        string flowState,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
UPDATE {_businessTable}
SET FLOW_STATE = :flow_state,
    UPDATED_AT = CURRENT_TIMESTAMP,
    ROW_VERSION = ROW_VERSION + 1
WHERE BUSINESS_ID = :business_id";
        AddParameter(command, "flow_state", flowState);
        AddParameter(command, "business_id", businessId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateRecommendedAssigneesSnapshotAsync(
        string businessId,
        string snapshotJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
UPDATE {_businessTable}
SET RECOMMENDED_ASSIGNEES_SNAPSHOT = :snapshot,
    UPDATED_AT = CURRENT_TIMESTAMP,
    ROW_VERSION = ROW_VERSION + 1,
    DATA_VERSION = DATA_VERSION + 1
WHERE BUSINESS_ID = :business_id";
        AddParameter(command, "snapshot", snapshotJson, DbType.String);
        AddParameter(command, "business_id", businessId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new DBConcurrencyException(
                $"Business binding not found: {businessId}.");
    }

    public async Task MarkBusinessCallbackStateAsync(
        string businessId,
        string callbackState,
        bool flowCompleted,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
UPDATE {_businessTable}
SET CALLBACK_STATE = :callback_state,
    FLOW_STATE = CASE WHEN :flow_completed = 1 THEN 'completed' ELSE FLOW_STATE END,
    COMPLETED_AT = CASE
        WHEN :flow_completed = 1 AND COMPLETED_AT IS NULL THEN CURRENT_TIMESTAMP
        ELSE COMPLETED_AT
    END,
    UPDATED_AT = CURRENT_TIMESTAMP,
    ROW_VERSION = ROW_VERSION + 1,
    DATA_VERSION = DATA_VERSION + 1
WHERE BUSINESS_ID = :business_id";
        AddParameter(command, "callback_state", callbackState);
        AddParameter(command, "flow_completed", flowCompleted ? 1 : 0);
        AddParameter(command, "business_id", businessId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<WorkflowBusinessInstance?> GetBusinessByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken cancellationToken = default)
        => GetBusinessAsync(
            "PROCESS_INSTANCE_ID",
            "process_instance_id",
            processInstanceId,
            cancellationToken);

    public async Task<IReadOnlyDictionary<string, WorkflowBusinessInstance>>
        GetBusinessesByProcessInstancesAsync(
            IReadOnlyCollection<string> processInstanceIds,
            CancellationToken cancellationToken = default)
    {
        var ids = processInstanceIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(100)
            .ToList() ?? new List<string>();
        if (ids.Count == 0)
            return new Dictionary<string, WorkflowBusinessInstance>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        var parameters = ids.Select((_, index) => $":process_id_{index}")
            .ToList();
        command.CommandText = $@"
SELECT ID, BUSINESS_ID, BUSINESS_TYPE, PROCESS_INSTANCE_ID,
       PROCESS_DEFINITION_KEY, PROCESS_DEFINITION_VERSION, FLOW_STATE,
       CALLBACK_STATE, CALLBACK_CONFIG_SNAPSHOT,
       RECOMMENDED_ASSIGNEES_SNAPSHOT, ROW_VERSION, DATA_VERSION,
       CREATED_AT, UPDATED_AT, COMPLETED_AT
FROM {_businessTable}
WHERE PROCESS_INSTANCE_ID IN ({string.Join(",", parameters)})";
        for (var index = 0; index < ids.Count; index++)
            AddParameter(command, $"process_id_{index}", ids[index]);

        var result = new Dictionary<string, WorkflowBusinessInstance>(
            StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var instance = ReadBusiness(reader);
            if (!string.IsNullOrWhiteSpace(instance.ProcessInstanceId))
                result[instance.ProcessInstanceId] = instance;
        }
        return result;
    }

    public async Task SaveDefinitionConfigAsync(
        WorkflowDefinitionConfig config,
        CancellationToken cancellationToken = default)
    {
        if (config == null
            || string.IsNullOrWhiteSpace(config.ProcessDefinitionKey)
            || config.ProcessDefinitionVersion < 1
            || string.IsNullOrWhiteSpace(config.ProcessDefinitionId)
            || string.IsNullOrWhiteSpace(config.ContentHash)
            || string.IsNullOrWhiteSpace(config.ConfigJson))
            throw new ArgumentException("Definition config is incomplete.", nameof(config));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var insert = connection.CreateCommand();
            Configure(insert);
            insert.CommandText = $@"
INSERT INTO {_definitionTable}
(
    PROCESS_DEFINITION_KEY, PROCESS_DEFINITION_VERSION,
    PROCESS_DEFINITION_ID, CONTENT_HASH, CONFIG_JSON,
    DATA_VERSION, CREATED_AT, UPDATED_AT
)
VALUES
(
    :definition_key, :definition_version, :definition_id,
    :content_hash, :config_json, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
)";
            AddParameter(insert, "definition_key", config.ProcessDefinitionKey);
            AddParameter(insert, "definition_version", config.ProcessDefinitionVersion);
            AddParameter(insert, "definition_id", config.ProcessDefinitionId);
            AddParameter(insert, "content_hash", config.ContentHash);
            AddParameter(insert, "config_json", config.ConfigJson, DbType.String);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            var existing = await GetDefinitionConfigAsync(
                config.ProcessDefinitionKey,
                config.ProcessDefinitionVersion,
                cancellationToken);
            if (existing != null
                && string.Equals(existing.ContentHash, config.ContentHash,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.ProcessDefinitionId,
                    config.ProcessDefinitionId, StringComparison.Ordinal))
                return;
            throw;
        }
    }

    public async Task<WorkflowDefinitionConfig?> GetDefinitionConfigAsync(
        string processDefinitionKey,
        int processDefinitionVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
SELECT PROCESS_DEFINITION_KEY, PROCESS_DEFINITION_VERSION,
       PROCESS_DEFINITION_ID, CONTENT_HASH, CONFIG_JSON, DATA_VERSION
FROM {_definitionTable}
WHERE PROCESS_DEFINITION_KEY = :definition_key
  AND PROCESS_DEFINITION_VERSION = :definition_version";
        AddParameter(command, "definition_key", processDefinitionKey);
        AddParameter(command, "definition_version", processDefinitionVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new WorkflowDefinitionConfig
        {
            ProcessDefinitionKey = reader.GetString(0),
            ProcessDefinitionVersion = reader.GetInt32(1),
            ProcessDefinitionId = reader.GetString(2),
            ContentHash = reader.GetString(3),
            ConfigJson = reader.GetString(4),
            DataVersion = reader.GetInt64(5)
        };
    }

    public async Task<WorkflowTaskAction> PrepareTaskActionAsync(
        PrepareTaskActionCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command == null
            || string.IsNullOrWhiteSpace(command.ActionId)
            || string.IsNullOrWhiteSpace(command.IdempotencyKey)
            || string.IsNullOrWhiteSpace(command.BusinessId)
            || string.IsNullOrWhiteSpace(command.ProcessInstanceId)
            || string.IsNullOrWhiteSpace(command.ActionType)
            || string.IsNullOrWhiteSpace(command.OperatorId))
            throw new ArgumentException("Task action is incomplete.", nameof(command));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var insert = connection.CreateCommand();
            Configure(insert);
            insert.CommandText = $@"
INSERT INTO {_actionTable}
(
    ACTION_ID, IDEMPOTENCY_KEY, BUSINESS_ID, PROCESS_INSTANCE_ID,
    TASK_ID, TASK_DEFINITION_KEY, ACTION_TYPE, OPERATOR_ID,
    REQUEST_JSON, RESULT_STATE, DATA_VERSION, CREATED_AT, UPDATED_AT
)
VALUES
(
    :action_id, :idempotency_key, :business_id, :process_instance_id,
    :task_id, :task_definition_key, :action_type, :operator_id,
    :request_json, 'prepared', 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
)";
            AddParameter(insert, "action_id", command.ActionId);
            AddParameter(insert, "idempotency_key", command.IdempotencyKey);
            AddParameter(insert, "business_id", command.BusinessId);
            AddParameter(insert, "process_instance_id", command.ProcessInstanceId);
            AddParameter(insert, "task_id", command.TaskId);
            AddParameter(insert, "task_definition_key", command.TaskDefinitionKey);
            AddParameter(insert, "action_type", command.ActionType);
            AddParameter(insert, "operator_id", command.OperatorId);
            AddParameter(insert, "request_json", command.RequestJson, DbType.String);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            var existing = await GetTaskActionAsync(
                "IDEMPOTENCY_KEY", "idempotency_key",
                command.IdempotencyKey, cancellationToken);
            if (existing != null)
                return existing;
            throw;
        }
        return (await GetTaskActionAsync(
            "ACTION_ID", "action_id", command.ActionId, cancellationToken))!;
    }

    public async Task MarkTaskActionResultAsync(
        string actionId,
        string resultState,
        string flowableResult,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (resultState is not ("applied" or "failed" or "reconcile_required"))
            throw new ArgumentOutOfRangeException(nameof(resultState));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
UPDATE {_actionTable}
SET RESULT_STATE = :result_state,
    FLOWABLE_RESULT = :flowable_result,
    LAST_ERROR = :last_error,
    DATA_VERSION = DATA_VERSION + 1,
    UPDATED_AT = CURRENT_TIMESTAMP
WHERE ACTION_ID = :action_id
  AND RESULT_STATE IN ('prepared', 'reconcile_required')";
        AddParameter(command, "result_state", resultState);
        AddParameter(command, "flowable_result", flowableResult);
        AddParameter(command, "last_error", Truncate(error ?? string.Empty, 4000));
        AddParameter(command, "action_id", actionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WorkflowCallbackEvent> EnqueueCallbackAsync(
        EnqueueCallbackCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            Configure(insert);
            insert.CommandText = $@"
INSERT INTO {_callbackTable}
(
    EVENT_ID, IDEMPOTENCY_KEY, BUSINESS_ID, PROCESS_INSTANCE_ID,
    CALLBACK_ACTIVITY_ID, CALLBACK_TYPE, PAYLOAD, STATUS,
    ATTEMPT_COUNT, NEXT_ATTEMPT_AT, CREATED_AT, UPDATED_AT, ROW_VERSION
)
VALUES
(
    :event_id, :idempotency_key, :business_id, :process_instance_id,
    :callback_activity_id, :callback_type, :payload, :status,
    0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 0
)";
            AddParameter(insert, "event_id", command.EventId);
            AddParameter(insert, "idempotency_key", command.IdempotencyKey);
            AddParameter(insert, "business_id", command.BusinessId);
            AddParameter(insert, "process_instance_id", command.ProcessInstanceId);
            AddParameter(insert, "callback_activity_id", command.CallbackActivityId);
            AddParameter(insert, "callback_type", command.CallbackType);
            AddParameter(insert, "payload", command.Payload, DbType.String);
            AddParameter(insert, "status", CallbackEventStatus.Pending);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            if (command.CompleteBusinessFlow)
            {
                await using var updateBusiness = connection.CreateCommand();
                updateBusiness.Transaction = transaction;
                Configure(updateBusiness);
                updateBusiness.CommandText = $@"
UPDATE {_businessTable}
SET CALLBACK_STATE = 'pending',
    FLOW_STATE = 'completed',
    COMPLETED_AT = COALESCE(COMPLETED_AT, CURRENT_TIMESTAMP),
    UPDATED_AT = CURRENT_TIMESTAMP,
    ROW_VERSION = ROW_VERSION + 1,
    DATA_VERSION = DATA_VERSION + 1
WHERE BUSINESS_ID = :business_id
  AND PROCESS_INSTANCE_ID = :process_instance_id";
                AddParameter(
                    updateBusiness,
                    "business_id",
                    command.BusinessId);
                AddParameter(
                    updateBusiness,
                    "process_instance_id",
                    command.ProcessInstanceId);
                if (await updateBusiness.ExecuteNonQueryAsync(
                        cancellationToken) != 1)
                {
                    throw new DBConcurrencyException(
                        $"Callback business binding not found: {command.BusinessId}.");
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            var existing = await GetCallbackByIdempotencyKeyAsync(
                command.IdempotencyKey,
                cancellationToken);
            if (existing != null)
                return existing;
            throw;
        }

        return (await GetCallbackByIdempotencyKeyAsync(
            command.IdempotencyKey,
            cancellationToken))!;
    }

    public Task<WorkflowCallbackEvent?> GetCallbackByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => GetCallbackAsync(
            "IDEMPOTENCY_KEY",
            "idempotency_key",
            idempotencyKey,
            cancellationToken);

    public Task<WorkflowCallbackEvent?> GetCallbackByEventIdAsync(
        string eventId,
        CancellationToken cancellationToken = default)
        => GetCallbackAsync(
            "EVENT_ID",
            "event_id",
            eventId,
            cancellationToken);

    public async Task<(IReadOnlyList<WorkflowCallbackEvent> Items, int Total)>
        QueryCallbacksAsync(
            string? businessId,
            string? processInstanceId,
            string? status,
            int start,
            int size,
            CancellationToken cancellationToken = default)
    {
        start = Math.Max(0, start);
        size = Math.Clamp(size, 1, 100);
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(businessId))
            conditions.Add("BUSINESS_ID = :business_id");
        if (!string.IsNullOrWhiteSpace(processInstanceId))
            conditions.Add("PROCESS_INSTANCE_ID = :process_instance_id");
        if (!string.IsNullOrWhiteSpace(status))
            conditions.Add("STATUS = :status");
        var where = conditions.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", conditions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var count = connection.CreateCommand();
        Configure(count);
        count.CommandText = $"SELECT COUNT(*) FROM {_callbackTable} {where}";
        AddCallbackQueryParameters(
            count, businessId, processInstanceId, status);
        var total = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken));

        await using var query = connection.CreateCommand();
        Configure(query);
        query.CommandText = $@"
SELECT EVENT_ID, IDEMPOTENCY_KEY, BUSINESS_ID, PROCESS_INSTANCE_ID,
       CALLBACK_ACTIVITY_ID, CALLBACK_TYPE, PAYLOAD, STATUS,
       ATTEMPT_COUNT, NEXT_ATTEMPT_AT, LEASE_OWNER, LEASE_UNTIL,
       LAST_HTTP_STATUS, LAST_ERROR, CREATED_AT, UPDATED_AT,
       CONFIRMED_AT, COMPLETED_AT, ROW_VERSION
FROM {_callbackTable}
{where}
ORDER BY CREATED_AT DESC
OFFSET {start} ROWS FETCH NEXT {size} ROWS ONLY";
        AddCallbackQueryParameters(
            query, businessId, processInstanceId, status);
        var items = new List<WorkflowCallbackEvent>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(ReadCallback(reader));
        return (items, total);
    }

    public async Task<IReadOnlyList<WorkflowCallbackEvent>> LeaseCallbacksAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Worker id is required.", nameof(workerId));

        batchSize = Math.Clamp(batchSize, 1, 100);
        var candidates = new List<(string EventId, long RowVersion)>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var select = connection.CreateCommand();
        Configure(select);
        select.CommandText = $@"
SELECT EVENT_ID, ROW_VERSION
FROM {_callbackTable}
WHERE
(
    (
        STATUS IN ('pending', 'retry_waiting')
        AND NEXT_ATTEMPT_AT <= CURRENT_TIMESTAMP
        AND (LEASE_UNTIL IS NULL OR LEASE_UNTIL < CURRENT_TIMESTAMP)
    )
    OR
    (
        STATUS = 'processing'
        AND LEASE_UNTIL < CURRENT_TIMESTAMP
    )
)
ORDER BY CREATED_AT
FETCH FIRST {batchSize} ROWS ONLY";
        await using (var reader =
                     await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        var claimedIds = new List<string>();
        foreach (var candidate in candidates)
        {
            if (await TryClaimCallbackAsync(
                    candidate.EventId,
                    candidate.RowVersion,
                    workerId,
                    DateTime.Now.Add(leaseDuration),
                    cancellationToken))
            {
                claimedIds.Add(candidate.EventId);
            }
        }

        var leased = new List<WorkflowCallbackEvent>();
        foreach (var eventId in claimedIds)
        {
            var item = await GetCallbackByEventIdAsync(eventId, cancellationToken);
            if (item != null
                && string.Equals(item.LeaseOwner, workerId, StringComparison.Ordinal))
                leased.Add(item);
        }
        return leased;
    }

    private async Task<bool> TryClaimCallbackAsync(
        string eventId,
        long expectedRowVersion,
        string workerId,
        DateTime leaseUntil,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteExpired = connection.CreateCommand())
            {
                deleteExpired.Transaction = transaction;
                Configure(deleteExpired);
                deleteExpired.CommandText = $@"
DELETE FROM {_leaseTable}
WHERE EVENT_ID = :event_id
  AND LEASE_UNTIL < CURRENT_TIMESTAMP";
                AddParameter(deleteExpired, "event_id", eventId);
                await deleteExpired.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertLease = connection.CreateCommand())
            {
                insertLease.Transaction = transaction;
                Configure(insertLease);
                insertLease.CommandText = $@"
INSERT INTO {_leaseTable}
    (EVENT_ID, LEASE_OWNER, LEASE_UNTIL, CREATED_AT)
VALUES
    (:event_id, :lease_owner, :lease_until, CURRENT_TIMESTAMP)";
                AddParameter(insertLease, "event_id", eventId);
                AddParameter(insertLease, "lease_owner", workerId);
                AddParameter(insertLease, "lease_until", leaseUntil, DbType.DateTime);
                await insertLease.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            Configure(update);
            update.CommandText = $@"
UPDATE {_callbackTable}
SET STATUS = '{CallbackEventStatus.Processing}',
    LEASE_OWNER = :lease_owner,
    LEASE_UNTIL = :lease_until,
    UPDATED_AT = CURRENT_TIMESTAMP,
    ROW_VERSION = ROW_VERSION + 1
WHERE EVENT_ID = :event_id
  AND ROW_VERSION = :expected_row_version
  AND
  (
      (
          STATUS IN ('pending', 'retry_waiting')
          AND NEXT_ATTEMPT_AT <= CURRENT_TIMESTAMP
          AND (LEASE_UNTIL IS NULL OR LEASE_UNTIL < CURRENT_TIMESTAMP)
      )
      OR
      (
          STATUS = 'processing'
          AND LEASE_UNTIL < CURRENT_TIMESTAMP
      )
  )";
            AddParameter(update, "lease_owner", workerId);
            AddParameter(update, "lease_until", leaseUntil, DbType.DateTime);
            AddParameter(update, "event_id", eventId);
            AddParameter(update, "expected_row_version", expectedRowVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
    }

    public async Task MarkCallbackSucceededAsync(
        string eventId,
        string workerId,
        int httpStatus,
        CancellationToken cancellationToken = default)
    {
        var affected = await UpdateLeasedCallbackAsync(
            eventId,
            workerId,
            $@"STATUS = '{CallbackEventStatus.Succeeded}',
                ATTEMPT_COUNT = ATTEMPT_COUNT + 1,
                LAST_HTTP_STATUS = :http_status,
                LAST_ERROR = NULL,
                LEASE_OWNER = NULL,
                LEASE_UNTIL = NULL,
                COMPLETED_AT = CURRENT_TIMESTAMP",
            command => AddParameter(command, "http_status", httpStatus),
            cancellationToken);
        if (affected != 1)
            throw new DBConcurrencyException("Callback lease was lost.");
        await DeleteLeaseAsync(eventId, workerId, cancellationToken);
    }

    public async Task MarkCallbackFailedAsync(
        string eventId,
        string workerId,
        CallbackRetryDecision decision,
        int? httpStatus,
        string error,
        CancellationToken cancellationToken = default)
    {
        var affected = await UpdateLeasedCallbackAsync(
            eventId,
            workerId,
            @"STATUS = :next_status,
              ATTEMPT_COUNT = ATTEMPT_COUNT + 1,
              NEXT_ATTEMPT_AT = :next_attempt_at,
              LAST_HTTP_STATUS = :http_status,
              LAST_ERROR = :last_error,
              LEASE_OWNER = NULL,
              LEASE_UNTIL = NULL",
            command =>
            {
                AddParameter(command, "next_status", decision.Status);
                AddParameter(command, "next_attempt_at", decision.NextAttemptAt, DbType.DateTime);
                AddParameter(command, "http_status", httpStatus);
                AddParameter(command, "last_error", Truncate(error, 4000));
            },
            cancellationToken);
        if (affected != 1)
            throw new DBConcurrencyException("Callback lease was lost.");
        await DeleteLeaseAsync(eventId, workerId, cancellationToken);
    }

    public async Task<bool> RetryDeadLetterAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
UPDATE {_callbackTable}
SET STATUS = '{CallbackEventStatus.Pending}',
    ATTEMPT_COUNT = 0,
    NEXT_ATTEMPT_AT = CURRENT_TIMESTAMP,
    LEASE_OWNER = NULL,
    LEASE_UNTIL = NULL,
    LAST_ERROR = NULL,
    UPDATED_AT = CURRENT_TIMESTAMP,
    ROW_VERSION = ROW_VERSION + 1
WHERE EVENT_ID = :event_id
  AND STATUS = '{CallbackEventStatus.DeadLetter}'";
        AddParameter(command, "event_id", eventId);
        var retried = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (retried)
            await DeleteLeaseAsync(eventId, null, cancellationToken);
        return retried;
    }

    public async Task<WorkflowBusinessInstance?> GetBusinessByBusinessIdAsync(
        string businessId,
        CancellationToken cancellationToken)
        => await GetBusinessAsync(
            "BUSINESS_ID",
            "business_id",
            businessId,
            cancellationToken);

    private async Task<WorkflowTaskAction?> GetTaskActionAsync(
        string column,
        string parameterName,
        string value,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
SELECT ACTION_ID, IDEMPOTENCY_KEY, BUSINESS_ID, PROCESS_INSTANCE_ID,
       TASK_ID, TASK_DEFINITION_KEY, ACTION_TYPE, OPERATOR_ID,
       REQUEST_JSON, RESULT_STATE, FLOWABLE_RESULT, LAST_ERROR, DATA_VERSION
FROM {_actionTable}
WHERE {column} = :{parameterName}";
        AddParameter(command, parameterName, value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new WorkflowTaskAction
        {
            ActionId = reader.GetString(0),
            IdempotencyKey = reader.GetString(1),
            BusinessId = reader.GetString(2),
            ProcessInstanceId = reader.GetString(3),
            TaskId = GetNullableString(reader, 4),
            TaskDefinitionKey = GetNullableString(reader, 5),
            ActionType = reader.GetString(6),
            OperatorId = reader.GetString(7),
            RequestJson = GetNullableString(reader, 8),
            ResultState = reader.GetString(9),
            FlowableResult = GetNullableString(reader, 10),
            LastError = GetNullableString(reader, 11),
            DataVersion = reader.GetInt64(12)
        };
    }

    private async Task<WorkflowBusinessInstance?> GetBusinessAsync(
        string column,
        string parameterName,
        string value,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
SELECT ID, BUSINESS_ID, BUSINESS_TYPE, PROCESS_INSTANCE_ID,
       PROCESS_DEFINITION_KEY, PROCESS_DEFINITION_VERSION, FLOW_STATE,
       CALLBACK_STATE, CALLBACK_CONFIG_SNAPSHOT,
       RECOMMENDED_ASSIGNEES_SNAPSHOT, ROW_VERSION, DATA_VERSION,
       CREATED_AT, UPDATED_AT, COMPLETED_AT
FROM {_businessTable}
WHERE {column} = :{parameterName}";
        AddParameter(command, parameterName, value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadBusiness(reader)
            : null;
    }

    private async Task<WorkflowCallbackEvent?> GetCallbackAsync(
        string column,
        string parameterName,
        string value,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
SELECT EVENT_ID, IDEMPOTENCY_KEY, BUSINESS_ID, PROCESS_INSTANCE_ID,
       CALLBACK_ACTIVITY_ID, CALLBACK_TYPE, PAYLOAD, STATUS,
       ATTEMPT_COUNT, NEXT_ATTEMPT_AT, LEASE_OWNER, LEASE_UNTIL,
       LAST_HTTP_STATUS, LAST_ERROR, CREATED_AT, UPDATED_AT,
       CONFIRMED_AT, COMPLETED_AT, ROW_VERSION
FROM {_callbackTable}
WHERE {column} = :{parameterName}";
        AddParameter(command, parameterName, value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCallback(reader)
            : null;
    }

    private async Task<int> UpdateLeasedCallbackAsync(
        string eventId,
        string workerId,
        string setClause,
        Action<DbCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
UPDATE {_callbackTable}
SET {setClause},
    UPDATED_AT = CURRENT_TIMESTAMP,
    ROW_VERSION = ROW_VERSION + 1
WHERE EVENT_ID = :event_id
  AND STATUS = '{CallbackEventStatus.Processing}'
  AND LEASE_OWNER = :lease_owner";
        AddParameter(command, "event_id", eventId);
        AddParameter(command, "lease_owner", workerId);
        addParameters(command);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DmConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new InvalidOperationException("Dm8 connection string is required.");
        return new DmConnection(_options.ConnectionString);
    }

    private async Task DeleteLeaseAsync(
        string eventId,
        string? workerId,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        Configure(command);
        command.CommandText = $@"
DELETE FROM {_leaseTable}
WHERE EVENT_ID = :event_id
  AND (:lease_owner IS NULL OR LEASE_OWNER = :lease_owner)";
        AddParameter(command, "event_id", eventId);
        AddParameter(command, "lease_owner", workerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void Configure(DbCommand command)
        => command.CommandTimeout = _options.CommandTimeoutSeconds;

    private static void AddCallbackQueryParameters(
        DbCommand command,
        string? businessId,
        string? processInstanceId,
        string? status)
    {
        if (!string.IsNullOrWhiteSpace(businessId))
            AddParameter(command, "business_id", businessId);
        if (!string.IsNullOrWhiteSpace(processInstanceId))
            AddParameter(command, "process_instance_id", processInstanceId);
        if (!string.IsNullOrWhiteSpace(status))
            AddParameter(command, "status", status);
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value,
        DbType? dbType = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (dbType.HasValue)
            parameter.DbType = dbType.Value;
        command.Parameters.Add(parameter);
    }

    private static WorkflowCallbackEvent ReadCallback(DbDataReader reader)
        => new()
        {
            EventId = reader.GetString(0),
            IdempotencyKey = reader.GetString(1),
            BusinessId = reader.GetString(2),
            ProcessInstanceId = reader.GetString(3),
            CallbackActivityId = reader.GetString(4),
            CallbackType = reader.GetString(5),
            Payload = reader.GetString(6),
            Status = reader.GetString(7),
            AttemptCount = reader.GetInt32(8),
            NextAttemptAt = GetNullableDateTime(reader, 9),
            LeaseOwner = GetNullableString(reader, 10),
            LeaseUntil = GetNullableDateTime(reader, 11),
            LastHttpStatus = reader.IsDBNull(12) ? null : reader.GetInt32(12),
            LastError = GetNullableString(reader, 13),
            CreatedAt = reader.GetDateTime(14),
            UpdatedAt = reader.GetDateTime(15),
            ConfirmedAt = GetNullableDateTime(reader, 16),
            CompletedAt = GetNullableDateTime(reader, 17),
            RowVersion = reader.GetInt64(18)
        };

    private static WorkflowBusinessInstance ReadBusiness(DbDataReader reader)
        => new()
        {
            Id = reader.GetInt64(0),
            BusinessId = reader.GetString(1),
            BusinessType = reader.GetString(2),
            ProcessInstanceId = GetNullableString(reader, 3),
            ProcessDefinitionKey = reader.GetString(4),
            ProcessDefinitionVersion =
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
            FlowState = reader.GetString(6),
            CallbackState = reader.GetString(7),
            CallbackConfigSnapshot = GetNullableString(reader, 8),
            RecommendedAssigneesSnapshot = GetNullableString(reader, 9),
            RowVersion = reader.GetInt64(10),
            DataVersion = reader.GetInt64(11),
            CreatedAt = reader.GetDateTime(12),
            UpdatedAt = reader.GetDateTime(13),
            CompletedAt = GetNullableDateTime(reader, 14)
        };

    private static string? GetNullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? GetNullableDateTime(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static string ValidateIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)
            || identifier.Any(character =>
                !(char.IsLetterOrDigit(character) || character == '_')))
            throw new InvalidOperationException("Invalid DM8 schema name.");
        return identifier.ToUpperInvariant();
    }

    private static void Validate(EnqueueCallbackCommand command)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));
        if (string.IsNullOrWhiteSpace(command.EventId)
            || string.IsNullOrWhiteSpace(command.IdempotencyKey)
            || string.IsNullOrWhiteSpace(command.BusinessId)
            || string.IsNullOrWhiteSpace(command.ProcessInstanceId)
            || string.IsNullOrWhiteSpace(command.CallbackActivityId)
            || string.IsNullOrWhiteSpace(command.CallbackType))
            throw new ArgumentException("Callback command contains empty identity fields.");
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];
}
