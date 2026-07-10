# Problem Zero End-to-End Test Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deploy and exercise every `problem_zero` BPMN branch through the API, verify every node-level and process-level callback over the host LAN address, and produce an evidence-backed Chinese test report.

**Architecture:** The existing ASP.NET Core API is exposed on `0.0.0.0:5012` and accessed as `192.168.124.5:5012`. Flowable, Elasticsearch, and Redis remain the configured external dependencies. Three process instances cover the solved, unsolved/special, and unsolved/non-special branches; callback records are captured by `Api/Controllers/TestController.cs`.

**Tech Stack:** .NET 6 ASP.NET Core, Flowable 7 REST API, Elasticsearch 7, Redis, PowerShell `Invoke-RestMethod`.

## Global Constraints

- Every `callbackUrl` in `bpmn/问题归零/problem_zero_slot.json` must be `http://192.168.124.5:5012/api/test/node-callback`.
- Every process start callback must be `http://192.168.124.5:5012/api/test/process-callback`.
- Do not use `localhost` for API health checks, workflow calls, or callbacks.
- Preserve the user's unrelated untracked `bpmn.zip`.
- Cover `IS_SOLVED=true`, `IS_SOLVED=false + PROBLEM_ATTRIBUTE=true`, and `IS_SOLVED=false + PROBLEM_ATTRIBUTE=false`.

---

### Task 1: Prepare deployable configuration

**Files:**
- Modify: `bpmn/问题归零/problem_zero_slot.json`
- Modify: `appsettings.json`

**Interfaces:**
- Consumes: WLAN address `192.168.124.5`, TestController route `/api/test/node-callback`.
- Produces: deployable slot configuration and `problem_zero -> problem_zero` business mapping.

- [ ] **Step 1: Verify all six callback URLs are currently disabled**

Run: parse `problem_zero_slot.json` and list `taskDefinitionKey` plus `callbackUrl`.
Expected: six nodes and six null callback URLs.

- [ ] **Step 2: Set all six node callback URLs**

Set each `callbackUrl` to `http://192.168.124.5:5012/api/test/node-callback` without changing slot semantics.

- [ ] **Step 3: Add the missing business mapping**

Add `"problem_zero": "problem_zero"` under `BusinessTypeProcessMapping.Mappings` while retaining existing mappings.

- [ ] **Step 4: Validate JSON structure**

Run: `Get-Content -Raw -Encoding utf8 <file> | ConvertFrom-Json` for both JSON files.
Expected: parsing succeeds; six callback URLs match the LAN endpoint; mapping is present.

### Task 2: Start dependencies and deploy BPMN

**Files:**
- Read: `README.md`
- Read: `bpmn/问题归零/problem_zero.bpmn`
- Evidence: `docs/test-reports/evidence/problem-zero/`

**Interfaces:**
- Consumes: configured Flowable/Elasticsearch/Redis endpoints and prepared BPMN/slot JSON.
- Produces: active API process and deployed Flowable definition key `problem_zero`.

- [ ] **Step 1: Probe external dependencies**

Check TCP connectivity to `192.168.124.2:18080`, `:19200`, and `:16379`.
Expected: all three ports accept connections.

- [ ] **Step 2: Build and test the application**

Run: `dotnet build process.csproj` and `dotnet test Test/FlowableWrapper.Test.csproj`.
Expected: exit code 0 for both commands.

- [ ] **Step 3: Run the API on all interfaces**

Run the application with `--urls http://0.0.0.0:5012` and save logs.
Expected: `GET http://192.168.124.5:5012/api/test` returns `{ "ok": true }`.

- [ ] **Step 4: Clear callback evidence and deploy**

Call `DELETE /api/test/callbacks`, then multipart `POST /api/flowable/bpmn/deploy` with the BPMN file and slot JSON string.
Expected: deployment succeeds and `GET /api/flowable/bpmn/problem_zero/nodes` returns all six node configurations with LAN callback URLs.

### Task 3: Execute every branch

**Files:**
- Evidence: `docs/test-reports/evidence/problem-zero/*.json`

**Interfaces:**
- Consumes: `/api/processes/start`, `/api/tasks/pending`, `/api/tasks/complete`, and process query APIs.
- Produces: three completed process instances with complete audit and callback records.

- [ ] **Step 1: Execute solved/direct-end path**

Start a unique business ID with `starterAssignee`, initial `team_leader`, assignee role pools, and LAN process callback. Complete UT-01 and UT-02, then complete UT-03 with `IS_SOLVED=true` and the no-op direct-end slot.
Expected: completed after UT-03; node callbacks exist for UT-01..UT-03; one process callback exists.

- [ ] **Step 2: Execute unsolved/special path**

Complete UT-01..UT-03 with `IS_SOLVED=false`, UT-04 with `PROBLEM_ATTRIBUTE=true`, then UT-05 and UT-06.
Expected: path is UT-01,02,03,04,05,06; six node callbacks and one process callback exist.

- [ ] **Step 3: Execute unsolved/non-special path**

Complete UT-01..UT-03 with `IS_SOLVED=false`, UT-04 with `PROBLEM_ATTRIBUTE=false`, then UT-06.
Expected: UT-05 is absent; five node callbacks and one process callback exist.

- [ ] **Step 4: Capture API evidence**

For each business ID save progress, status, audit-history, flow-render, and callback-query responses.
Expected: status is completed, currentNodes is empty, audit nodes match the branch, and callback counts/types match expectations.

### Task 4: Report and final verification

**Files:**
- Create: `docs/test-reports/2026-07-10-problem-zero-e2e-test-report.md`

**Interfaces:**
- Consumes: saved command logs and JSON evidence.
- Produces: Chinese acceptance report with configuration, environment, cases, actual results, callback matrix, defects, and conclusion.

- [ ] **Step 1: Reconcile evidence with BPMN paths**

Compare audit node sequences to each BPMN gateway condition and callback record sequence.
Expected: every BPMN branch edge and all six user tasks are covered at least once.

- [ ] **Step 2: Write the report**

Include exact time, host IP, dependency endpoints, deployment ID/version, business IDs, request decisions, results, callback payload evidence, CORS/network observation, and any unresolved defect.

- [ ] **Step 3: Run final verification**

Re-run JSON parsing, build/tests, API LAN health, completed statuses, and callback count assertions.
Expected: all checks pass or the report clearly marks the specific blocker/failure with evidence.

- [ ] **Step 4: Review repository changes**

Run: `git diff --check` and `git status --short`.
Expected: no whitespace errors; only intended configuration, plan, evidence, and report files changed; `bpmn.zip` remains untouched.
