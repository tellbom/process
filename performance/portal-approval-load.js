import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import exec from 'k6/execution';

const baseUrl = (__ENV.BASE_URL || 'http://192.168.124.2:5012').replace(/\/$/, '');
const token = __ENV.ACCESS_TOKEN || '';
const runId = __ENV.RUN_ID || `portal-${Date.now()}`;
const employeeId = __ENV.EMPLOYEE_ID || '196045';
const slowDelayMs = Number(__ENV.SLOW_DELAY_MS || 15000);
const startOnlyIterations = Number(__ENV.START_ONLY_ITERATIONS || 40);
const lifecycleIterations = Number(__ENV.LIFECYCLE_ITERATIONS || 10);
const queryIterations = Number(__ENV.QUERY_ITERATIONS || 10);
const flowRenderIterations = Number(__ENV.FLOW_RENDER_ITERATIONS || 0);

const startFailures = new Counter('portal_start_failures');
const starterCompleteFailures = new Counter('portal_starter_complete_failures');
const finalCompleteFailures = new Counter('portal_final_complete_failures');
const queryFailures = new Counter('portal_query_failures');
const startDuration = new Trend('portal_start_duration', true);
const starterCompleteDuration = new Trend('portal_starter_complete_duration', true);
const pendingDuration = new Trend('portal_pending_duration', true);
const finalCompleteFast = new Trend('portal_final_complete_fast', true);
const finalCompleteSlow = new Trend('portal_final_complete_slow', true);
const flowRenderFailures = new Counter('portal_flow_render_failures');
const flowRenderDuration = new Trend('portal_flow_render_duration', true);

export const options = {
  discardResponseBodies: true,
  scenarios: {
    start_only: {
      executor: 'shared-iterations',
      exec: 'startOnly',
      vus: Number(__ENV.START_ONLY_VUS || Math.min(startOnlyIterations, 20)),
      iterations: startOnlyIterations,
      maxDuration: __ENV.START_MAX_DURATION || '30m',
    },
    lifecycle: {
      executor: 'shared-iterations',
      exec: 'fullLifecycle',
      vus: Number(__ENV.LIFECYCLE_VUS || Math.min(lifecycleIterations, 10)),
      iterations: lifecycleIterations,
      maxDuration: __ENV.LIFECYCLE_MAX_DURATION || '45m',
    },
    pending_query: {
      executor: 'shared-iterations',
      exec: 'queryPending',
      startTime: __ENV.QUERY_START_TIME || '20s',
      vus: Number(__ENV.QUERY_VUS || Math.min(queryIterations, 10)),
      iterations: queryIterations,
      maxDuration: __ENV.QUERY_MAX_DURATION || '20m',
    },
    ...(flowRenderIterations > 0
      ? {
          flow_render: {
            executor: 'shared-iterations',
            exec: 'queryFlowRender',
            startTime: __ENV.FLOW_RENDER_START_TIME || '20s',
            vus: Number(__ENV.FLOW_RENDER_VUS || Math.min(flowRenderIterations, 10)),
            iterations: flowRenderIterations,
            maxDuration: __ENV.FLOW_RENDER_MAX_DURATION || '20m',
          },
        }
      : {}),
  },
  thresholds: {
    checks: ['rate>0.95'],
  },
};

function headers() {
  return {
    Authorization: `Bearer ${token}`,
    'Content-Type': 'application/json',
  };
}

function groupFor(index) {
  return index % 2 === 0 ? 'A' : 'B';
}

function identity(prefix) {
  const index = exec.scenario.iterationInTest;
  return {
    index,
    groupIndex: index,
    businessId: `${runId}-${prefix}-${index}`,
    leaderId: `load-leader-${index}`,
  };
}

function startProcess(id, group) {
  const callbackUrl = `${baseUrl}/api/test/process-callback/${group}?delayMs=${slowDelayMs}`;
  const payload = {
    businessType: 'portal_content_approval',
    businessId: id.businessId,
    initialSlotSelections: [
      { slotKey: 'portal_leader', users: [id.leaderId] },
    ],
    businessVariables: {
      starterAssignee: employeeId,
    },
    assigneeContract: {
      roles: [
        {
          roleKey: 'portal_content_starter',
          mode: 'single',
          users: [employeeId],
        },
        {
          roleKey: 'portal_content_leader',
          mode: 'multiple',
          users: [id.leaderId],
        },
      ],
    },
    callback: {
      url: callbackUrl,
      timeoutSeconds: Math.max(1, Math.ceil(slowDelayMs / 1000) + 5),
      retryCount: 0,
    },
  };

  const response = http.post(
    `${baseUrl}/api/processes/start`,
    JSON.stringify(payload),
    { headers: headers(), tags: { operation: 'start', group } },
  );
  startDuration.add(response.timings.duration);

  const ok = check(response, {
    'start returned 200': (r) => r.status === 200,
  });
  if (!ok) {
    startFailures.add(1);
    return null;
  }

  return response;
}

function completeTask(businessId, employee, operation, group) {
  const payload = {
    businessId,
    employeeId: employee,
    action: 1,
    comment: `load test ${operation}`,
    nextSlotSelections:
      operation === 'starter-complete'
        ? [{ slotKey: 'portal_leader', users: [`load-leader-${businessId.split('-').pop()}`] }]
        : [],
    businessVariables: {},
  };

  return http.post(
    `${baseUrl}/api/tasks/complete`,
    JSON.stringify(payload),
    { headers: headers(), tags: { operation, group } },
  );
}

export function startOnly() {
  const id = identity('start');
  const response = startProcess(id, groupFor(id.groupIndex));
  if (!response)
    sleep(0.05);
}

export function fullLifecycle() {
  const id = identity('life');
  const group = groupFor(id.groupIndex);
  if (!startProcess(id, group))
    return;

  const starterResponse = completeTask(
    id.businessId,
    employeeId,
    'starter-complete',
    group,
  );
  starterCompleteDuration.add(starterResponse.timings.duration);
  const starterOk = check(starterResponse, {
    'starter complete returned 200': (r) => r.status === 200,
  });
  if (!starterOk) {
    starterCompleteFailures.add(1);
    return;
  }

  const started = Date.now();
  const finalResponse = completeTask(
    id.businessId,
    id.leaderId,
    'final-complete',
    group,
  );
  const duration = Date.now() - started;
  if (group === 'B')
    finalCompleteSlow.add(duration);
  else
    finalCompleteFast.add(duration);

  const finalOk = check(finalResponse, {
    'final complete returned 200': (r) => r.status === 200,
  });
  if (!finalOk)
    finalCompleteFailures.add(1);
}

export function queryPending() {
  const response = http.get(
    `${baseUrl}/api/tasks/pending?employeeId=${encodeURIComponent(employeeId)}&pageIndex=1&pageSize=20`,
    { headers: headers(), tags: { operation: 'pending-query' } },
  );
  pendingDuration.add(response.timings.duration);
  const ok = check(response, {
    'pending query returned 200': (r) => r.status === 200,
  });
  if (!ok)
    queryFailures.add(1);
}

export function queryFlowRender() {
  if (startOnlyIterations <= 0)
    return;

  const index = exec.scenario.iterationInTest % startOnlyIterations;
  const businessId = `${runId}-start-${index}`;
  const started = Date.now();
  const response = http.get(
    `${baseUrl}/api/processes/${encodeURIComponent(businessId)}/flow-render`,
    { headers: headers(), tags: { operation: 'flow-render' } },
  );
  flowRenderDuration.add(Date.now() - started);
  const ok = check(response, {
    'flow render returned 200': (r) => r.status === 200,
  });
  if (!ok)
  {
    flowRenderFailures.add(1);
    console.error(JSON.stringify({
      kind: 'portal_flow_render_failure',
      businessId,
      status: response.status,
    }));
  }
}
