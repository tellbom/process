# Feature Specification: Process Center

**Feature Branch**: `001-process-center-spec`  
**Created**: 2026-04-26  
**Status**: Draft  
**Input**: User description: "请读取当前项目 .agents/skills/speckit-specify/SKILL.md，并按这个 skill 为当前项目生成规格文档。"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Start And Track Business Process (Priority: P1)

As a business system or process initiator, I need to start a workflow for a business record, assign the required participants, and receive a process identifier so that the business record can enter a controlled approval lifecycle.

**Why this priority**: Starting a process is the minimum viable capability for the process center; no downstream task handling, progress tracking, or callback value exists without it.

**Independent Test**: Can be fully tested by submitting a valid business type, business id, participant selections, and business variables, then confirming that a running process is created and can be queried by business id.

**Acceptance Scenarios**:

1. **Given** a supported business type and a unique business id, **When** the initiator starts a process with required participant selections, **Then** the system creates a running process and returns the process id plus first task summary.
2. **Given** the initiator omits required business identity information, **When** they attempt to start a process, **Then** the system rejects the request with a clear validation message.
3. **Given** the same business id is submitted repeatedly at the same time, **When** start requests compete, **Then** only one active process start succeeds for that business id.

---

### User Story 2 - Complete Or Reassign Pending Work (Priority: P1)

As an approver, I need to view my pending work, approve or reject an assigned task, optionally provide comments and next participant selections, and reassign current work when responsibility changes.

**Why this priority**: Task execution is the core daily workflow for end users and determines whether business processes can move forward.

**Independent Test**: Can be tested by creating a process with an assigned approver, confirming the task appears in that approver's pending list, completing the task, and verifying that the process advances or ends according to the chosen action.

**Acceptance Scenarios**:

1. **Given** a user has assigned pending tasks, **When** they request their pending list, **Then** the system returns paged tasks with enough information to identify and open the work item.
2. **Given** an approver submits an approval action for a valid task, **When** all required comments, variables, and next participant selections are valid, **Then** the system completes the task and records the approval action.
3. **Given** an approver submits a rejection action without required rejection information, **When** the system validates the request, **Then** it rejects the operation and explains the missing information.
4. **Given** a current task must be handled by another person, **When** an authorized user reassigns the task with new assignee information, **Then** the pending work moves to the new assignee without changing completed history.

---

### User Story 3 - Monitor Process Progress And History (Priority: P2)

As a business user, process initiator, or support operator, I need to view the current state, active nodes, approval history, and process diagram data for a business record so that I can understand where the process is and what happened.

**Why this priority**: Visibility reduces support burden and lets business systems and users make decisions without manually inspecting workflow internals.

**Independent Test**: Can be tested by starting a process, completing one or more tasks, then querying progress, status, audit history, and visual flow data to confirm they reflect the current business state.

**Acceptance Scenarios**:

1. **Given** a business record has a running process, **When** a user queries progress, **Then** the system returns process summary, active nodes, and approval history.
2. **Given** a process has completed, **When** a user queries progress, **Then** the system returns completed status and an empty active-node list.
3. **Given** a user only needs lightweight status, **When** they query by business id, **Then** the system returns status and key timestamps without requiring full progress details.
4. **Given** a user opens a process diagram view, **When** render data is requested, **Then** the system returns enough node, edge, status, and history information to display the process path.

---

### User Story 4 - Manage Process Definitions (Priority: P2)

As a process administrator, I need to deploy workflow definitions, bind node semantics and participant selection contracts, inspect deployed node metadata, and delete obsolete deployments when appropriate.

**Why this priority**: The process center must support changing business workflows without hardcoding every business path.

**Independent Test**: Can be tested by deploying a valid process definition with node configuration, querying the deployed node metadata, starting a process using that definition, and deleting the deployment when no longer needed.

**Acceptance Scenarios**:

1. **Given** an administrator provides a valid workflow definition and node configuration, **When** they deploy it, **Then** the process definition becomes available for supported business types.
2. **Given** a deployed process definition exists, **When** an administrator queries its nodes, **Then** the system returns node semantics, page identifiers, rejection options, and participant selection requirements.
3. **Given** an obsolete deployment can be removed, **When** an administrator deletes it, **Then** the system removes the deployment according to the requested deletion mode.

---

### User Story 5 - Notify Business Systems On Completion (Priority: P3)

As an integrated business system, I need to receive completion notifications and query final approval history so that my local business record can stay synchronized with the workflow result.

**Why this priority**: Completion notification closes the integration loop, but core process execution remains useful even when callbacks are configured later.

**Independent Test**: Can be tested by configuring a callback target for a process, completing the process, and confirming the business system receives a completion notification or the process is marked for callback attention when delivery fails.

**Acceptance Scenarios**:

1. **Given** a process reaches its natural end and callback information is configured, **When** the completion event occurs, **Then** the system sends a completion notification with process and business identifiers.
2. **Given** callback delivery fails, **When** the system handles the failure, **Then** it records a callback failure state that can be monitored and retried or handled operationally.
3. **Given** no callback information is configured, **When** the process completes, **Then** the system still records completion and does not block the process.

### Edge Cases

- A business type is not mapped to any available process definition.
- Required participant selections are missing, malformed, duplicated, or incompatible with the node contract.
- The same user has multiple active tasks in parallel branches and must specify the exact task to complete or reassign.
- A process is completed or terminated while a user is viewing stale pending-task information.
- A rejection target is not valid for the current node.
- Process metadata, audit records, or callback delivery temporarily fail after the workflow state changes.
- A deployed process definition has node metadata that does not match the participant selection contract expected by the business system.
- A workflow reaches completion before the business system queries progress again.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST allow authorized callers to start a process by providing business type, business id, optional business variables, optional callback information, and required participant selection data.
- **FR-002**: The system MUST reject process start requests when the business type is unsupported, the business id is missing, the current user cannot be determined, or required participant data is absent.
- **FR-003**: The system MUST prevent duplicate concurrent process starts for the same business id.
- **FR-004**: The system MUST retain process metadata including process identity, business identity, creator, status, timestamps, callback configuration, and node semantic information.
- **FR-005**: The system MUST allow users to query pending tasks assigned or available to a specified user, with pagination and task identifiers suitable for later task operations.
- **FR-006**: The system MUST allow an approver to complete a task with approval or rejection, comments, business variables, and next participant selections when required by the current workflow path.
- **FR-007**: The system MUST validate rejection requests against the current node's allowed rejection options before completing the task.
- **FR-008**: The system MUST record an audit entry for each completed task, including operator, action, comment, rejection information when present, node identity, page identity, timestamp, and participant selections captured during the action.
- **FR-009**: The system MUST allow current active work to be reassigned to one or more new assignees without rewriting prior audit history.
- **FR-010**: The system MUST allow administrators to terminate a process by business id with a required termination reason.
- **FR-011**: The system MUST provide process progress by business id, including process summary, active nodes, and approval history.
- **FR-012**: The system MUST provide lightweight process status by business id for callers that do not need active-node or history details.
- **FR-013**: The system MUST provide approval history by business id for callers that need final decision context.
- **FR-014**: The system MUST provide process flow render data that lets a client display the current path, completed nodes, active nodes, and historical actions.
- **FR-015**: The system MUST allow administrators to deploy workflow definitions with node semantics, page identifiers, rejection rules, and participant selection contracts.
- **FR-016**: The system MUST allow administrators to query deployed node definitions for a process definition.
- **FR-017**: The system MUST allow administrators to delete workflow deployments according to a requested deletion mode.
- **FR-018**: The system MUST handle workflow completion callbacks by updating process completion status and notifying the configured business system when a callback target exists.
- **FR-019**: The system MUST expose callback failure as a visible process state when business notification cannot be delivered successfully.
- **FR-020**: The system MUST return consistent success and error envelopes so callers can distinguish validation failures, business-rule failures, and operational failures.

### Key Entities

- **Process Instance**: A workflow execution tied to one business record. Key attributes include process id, business id, business type, definition key, status, creator, created time, completed time, callback configuration, and node semantic map.
- **Process Definition**: A deployable workflow template mapped to a business type. It contains node definitions and determines possible paths, tasks, rejection behavior, and completion behavior.
- **Node Semantic Information**: Business-facing metadata for a workflow node, including node id, semantic code, page code, convergence marker, starter marker, rejection capability, rejection options, and participant selection slots.
- **Participant Selection Slot**: A contract describing which users must be selected for a future task or multi-person step, including slot key, label, mode, variable name, requirement flag, and optional activation condition.
- **Task**: A unit of pending work assigned to or claimable by a user. It includes task id, node identity, assignee information, creation time, and process identity.
- **Audit Record**: A historical record of a task operation. It captures who acted, what action they took, comments, rejection information, selected participants, and when the action occurred.
- **Callback Configuration**: Business-system notification settings tied to a process instance, including target address, timeout preference, retry preference, and optional headers.
- **Flow Render View**: A read model for visualizing process state, including nodes, edges, active markers, completed markers, and history-derived annotations.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A valid process start request creates a queryable running process and returns first-task summary information in 95% of normal attempts within 3 seconds.
- **SC-002**: Users can find a pending task and complete an approval or rejection path in under 2 minutes when all required information is available.
- **SC-003**: Progress, status, and history queries reflect the latest completed user action for 95% of requests within 5 seconds of task completion.
- **SC-004**: At least 95% of validation failures return a clear business-readable message that identifies the missing or invalid input.
- **SC-005**: Process administrators can deploy a new valid workflow definition and confirm its node metadata in under 5 minutes.
- **SC-006**: Completion notification failures are visible to operators within 1 minute of detection.
- **SC-007**: Users can distinguish running, completed, terminated, and callback-failed process states without support-team intervention.
- **SC-008**: The process center supports at least 100 concurrent business users performing start, pending-list, complete, and progress-query operations without user-visible degradation.

## Assumptions

- The project scope is the current process center backend capability, not a separate end-user portal frontend.
- Users are identified by a trusted internal auth token or equivalent internal user identity source.
- Business systems own their domain records and use the process center to manage workflow state, task execution, and audit visibility.
- Business type to process definition mapping is configured before a process is started.
- Workflow definitions and node configuration are maintained by administrators or implementation teams with process-modeling knowledge.
- The workflow engine remains the source of truth for live task state, while process metadata and audit history are maintained for search, display, and integration needs.
- Existing per-step participant selection remains supported for compatibility; future fully automatic participant assignment can be specified separately.
- Operational recovery for rare metadata or callback failures may require monitoring and manual or follow-up automated repair outside this base specification.
