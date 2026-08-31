# V3 Failed Delivery Management Design

## Goal

Use Flowable 7.2 native async HTTP jobs, retries, and dead-letter jobs for V3
process-end callbacks, and expose a neutral backend Failed Delivery management
contract that can later be adapted to the V4 Outbox implementation.

## Scope

This change applies only to the V3 backend and its BPMN resources.

It includes:

- process-end HTTP ServiceTasks running as Flowable async jobs;
- Flowable-native retry and dead-letter behavior;
- neutral Failed Delivery query, detail, retry, and terminate APIs;
- Flowable runtime state as the process lifecycle source of truth;
- structured operation audit logging;
- real API verification against the local V3 service and the existing remote
  Flowable instance.

It does not include:

- a frontend page;
- V4 changes;
- Outbox, Redis, a callback worker, or another message queue;
- a callback-failure table;
- direct writes to Flowable runtime tables;
- business interpretation or classification of callback failures;
- automatic skip, rollback, process restart, or business-data repair;
- a Failed Delivery-specific role or authorization policy.

The endpoints inherit the application's existing authenticated-user policy.
The user's existing RBAC controls access to the management interface.

## Architecture

The V3 failure path remains owned by Flowable:

```text
last UserTask
    -> async HTTP ServiceTask
    -> Flowable executable job
    -> Flowable retry policy
    -> Flowable dead-letter job
```

The process center adds a read/action facade:

```text
Flowable Management REST API
    -> FlowableFailedDeliveryProvider
    -> FailedDeliveryAppService
    -> /api/admin/failed-deliveries
```

The provider maps Flowable data into a neutral DTO. It does not create a second
failure state store. Flowable dead-letter jobs remain the failure source of
truth, and Flowable runtime/historic APIs remain the process-state source of
truth.

## Components

### Flowable management client

Add an interface and HTTP implementation for the official Flowable REST
management resources. It will support:

- paged dead-letter job queries;
- one dead-letter job lookup;
- moving one dead-letter job back to the executable queue with an explicit
  retry count;
- executable-job lookup needed to verify a successful move.

The client must preserve Flowable HTTP status semantics. A missing or moved job
is a state conflict, not an empty successful result. It must not catch failures
and return fabricated records.

### Failed Delivery provider

The V3 provider exposes only Flowable dead-letter jobs whose current activity
is a process-end framework callback. The accepted activity identifier contract
is the existing `*_framework_callback` suffix. Other Flowable async jobs are
not Failed Delivery records.

For each accepted job it resolves:

- Flowable job identity and exception data;
- process instance and activity identity;
- business ID and callback target from the existing process metadata;
- live Flowable process state;
- available actions from current state.

Missing information is returned as `null`. No unrelated field is substituted
as an approximation.

### Neutral contract

`FailedDeliveryDto` contains:

```text
deliveryId
source
sourceId
deliveryType
businessId
processInstanceId
processState
activityId
target
status
attemptCount
lastHttpStatus
lastError
createdAt
lastFailedAt
availableActions[]
```

V3 values are:

- `source = flowable_async_job`;
- `deliveryType = process_completed`;
- `status = dead_letter` for records returned by the first implementation;
- `lastHttpStatus = null` unless Flowable exposes an exact value;
- `lastFailedAt = null` unless Flowable exposes an exact failure timestamp.

`deliveryId` is an opaque management identifier. `sourceId` is the real
Flowable dead-letter job ID.

### API

Expose:

```text
GET  /api/admin/failed-deliveries
GET  /api/admin/failed-deliveries/{deliveryId}
POST /api/admin/failed-deliveries/{deliveryId}/retry
POST /api/admin/failed-deliveries/{deliveryId}/terminate-process
```

The list supports pagination, business ID, process instance ID, source,
status, and delivery type. Unsupported source/status/type values return a
contract error rather than broadening the query.

### Retry delivery

Retry is allowed only when:

- the source record is still a Flowable dead-letter job;
- it is the process-end framework callback activity;
- the associated process instance is active.

The provider moves the original job to the executable queue and sets the
configured manual retry count. It does not start a process or recreate a job.
Flowable's state transition arbitrates concurrent retries: one move succeeds;
subsequent attempts receive a state-conflict response.

### Terminate process

Terminate is allowed only after re-reading and matching:

- the dead-letter job;
- its process instance ID;
- its framework callback activity;
- the live active process instance.

The operation deletes the entire runtime process instance with an explicit
administrative reason, then marks the existing ES projection as `terminated`.
It never deletes only the dead-letter job and never marks the process
`completed`.

Flowable state transitions arbitrate Retry/Terminate races. The API verifies
the final Flowable state and must not report a fabricated success when the
underlying state changed concurrently.

## Process and delivery state boundary

Callback failure does not change the process lifecycle projection to
`callback_failed`. While the async callback is retrying or dead-lettered, the
Flowable process remains active at the async ServiceTask and has no active user
task.

Failed Delivery queries resolve metadata by `processInstanceId`, not through
the existing business-ID query that filters ES status to `running`. Business
ID lookup for this management use case must continue to find the active
Flowable instance even if an ES delivery-related projection is stale.

## BPMN changes

Every V3 process-end framework HTTP ServiceTask will declare:

```xml
flowable:async="true"
<flowable:failedJobRetryTimeCycle>R5/PT10M</flowable:failedJobRetryTimeCycle>
```

The HTTP task configuration will treat every 4xx and 5xx response as job
failure. Timeouts and transport exceptions must propagate as job failures.
There is no status-specific retry or business explanation.

Only newly deployed process-definition versions receive this behavior.
Running instances bound to older definitions keep their original behavior.
Existing dead-letter jobs can be managed if they match the provider contract,
but old synchronous instances do not become asynchronous retroactively.

The existing uncommitted serial midterm-approval edits are preserved. Its
current `st05_framework_callback` is updated in place without reverting its
node order or slot changes.

## Audit

Retry and terminate write one structured application log event containing:

```text
operator
operation
deliveryId
sourceId
businessId
processInstanceId
beforeStatus
afterStatus
reason
createdAt
```

No second audit database or callback-failure table is introduced.

## Testing

Unit tests are written before implementation for mapping, filtering,
available-actions computation, state conflicts, and controller contracts.

Real API verification uses:

- the V3 application running locally;
- the existing remote Flowable 7.2 service;
- a dedicated test BPMN with a short retry cycle;
- an independently controlled HTTP stub reachable by Flowable.

The production BPMN retry cycle remains `R5/PT10M`; only the dedicated test
definition uses a short cycle.

Required real scenarios:

1. callback 200 -> process completed, no dead-letter;
2. callback 401 -> retry exhaustion -> dead-letter;
3. callback 500 -> retry exhaustion -> dead-letter;
4. callback timeout -> retry exhaustion -> dead-letter;
5. dead-letter Retry after target recovery -> same process completes;
6. dead-letter Retry while target still fails -> same job returns to
   dead-letter, without a new process or user task;
7. dead-letter Terminate -> no active process and ES state `terminated`;
8. concurrent Retry -> exactly one successful state transition;
9. Retry/Terminate race -> no surviving job for a terminated process;
10. business-ID management query finds an active process with zero user tasks
    and one dead-letter callback job.

Positive regression coverage includes normal user-task completion, reject,
normal process completion, existing terminate behavior, and exclusion of
non-callback async dead-letter jobs.

## Acceptance boundary

The process center guarantees only that its Flowable process can run, fail,
be inspected, be retried, and be administratively terminated. It reports the
technical Flowable/HTTP exception without classifying what the failure means
to the receiving business system.
