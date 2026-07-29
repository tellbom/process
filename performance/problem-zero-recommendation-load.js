import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';

const baseUrl = (__ENV.BASE_URL || 'http://127.0.0.1:5012').replace(/\/$/, '');
const esUrl = (__ENV.ES_URL || 'http://127.0.0.1:19200').replace(/\/$/, '');
const token = __ENV.ACCESS_TOKEN || '';
const runId = __ENV.RUN_ID || `problem-zero-${Date.now()}`;
const iterations = Number(__ENV.ITERATIONS || 10);
const verifyFlowRender = (__ENV.VERIFY_FLOW_RENDER || 'true').toLowerCase() === 'true';

const startFailures = new Counter('problem_zero_start_failures');
const esSnapshotFailures = new Counter('problem_zero_es_snapshot_failures');
const pendingFailures = new Counter('problem_zero_pending_failures');
const recommendationFailures = new Counter('problem_zero_recommendation_failures');
const completeFailures = new Counter('problem_zero_complete_failures');
const flowRenderFailures = new Counter('problem_zero_flow_render_failures');
const pendingDuration = new Trend('problem_zero_pending_duration', true);
const flowRenderDuration = new Trend('problem_zero_flow_render_duration', true);

export const options = {
  discardResponseBodies: false,
  scenarios: {
    recommendation_consistency: {
      executor: 'shared-iterations',
      exec: 'recommendationConsistency',
      vus: Number(__ENV.VUS || Math.min(iterations, 10)),
      iterations,
      maxDuration: __ENV.MAX_DURATION || '60m',
    },
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

function unwrap(response) {
  if (response.status !== 200)
    return null;

  try {
    const body = response.json();
    return body && Object.prototype.hasOwnProperty.call(body, 'data')
      ? body.data
      : body;
  } catch {
    return null;
  }
}

function reportFailure(layer, ids, stage, details = {}) {
  console.error(JSON.stringify({
    kind: 'problem_zero_consistency_failure',
    layer,
    stage,
    businessId: ids?.businessId,
    ...details,
  }));
}

function idsFor(index) {
  return {
    businessId: `${runId}-${index}`,
    starter: `pz-starter-${index}`,
    team: `pz-team-${index}`,
    quality: `pz-quality-${index}`,
    responsible: `pz-responsible-${index}`,
    counterpart: `pz-counterpart-${index}`,
    discoverer: `pz-discoverer-${index}`,
  };
}

function startWorkflow(ids) {
  const payload = {
    businessType: 'problem_zero',
    businessId: ids.businessId,
    initialSlotSelections: [
      { slotKey: 'team_leader', users: [ids.team] },
    ],
    businessVariables: {
      starterAssignee: ids.starter,
    },
    assigneeContract: {
      roles: [
        { roleKey: 'problem_zero_starter', mode: 'single', users: [ids.starter] },
        { roleKey: 'problem_zero_team_leader', mode: 'multiple', users: [ids.team] },
        { roleKey: 'problem_zero_quality_member', mode: 'multiple', users: [ids.quality] },
        { roleKey: 'problem_zero_responsible_person', mode: 'multiple', users: [ids.responsible] },
        { roleKey: 'problem_zero_counterpart_leader', mode: 'multiple', users: [ids.counterpart] },
        { roleKey: 'problem_zero_discoverer', mode: 'multiple', users: [ids.discoverer] },
      ],
    },
    callback: {
      url: `${baseUrl}/api/test/process-callback/A`,
      timeoutSeconds: 10,
      retryCount: 0,
    },
  };

  const response = http.post(
    `${baseUrl}/api/processes/start`,
    JSON.stringify(payload),
    { headers: headers(), tags: { operation: 'problem-zero-start' } },
  );
  const data = unwrap(response);
  const ok = check(response, {
    'problem zero start returned 200': (r) => r.status === 200,
    'problem zero start returned process instance id': () => !!data?.processInstanceId,
  });
  if (!ok)
  {
    startFailures.add(1);
    reportFailure('start-api', ids, 'start', { status: response.status });
  }

  return data;
}

function verifyEsSnapshot(processInstanceId, ids) {
  const response = http.get(
    `${esUrl}/flowable-process-metadata/_doc/${encodeURIComponent(processInstanceId)}`,
    { tags: { operation: 'es-recommendation-snapshot' } },
  );
  let snapshot = null;
  try {
    snapshot = response.json('_source.recommendedAssigneesSnapshot');
  } catch {
    snapshot = null;
  }

  const expected = {
    problem_zero_starter: ids.starter,
    problem_zero_team_leader: ids.team,
    problem_zero_quality_member: ids.quality,
    problem_zero_responsible_person: ids.responsible,
    problem_zero_counterpart_leader: ids.counterpart,
    problem_zero_discoverer: ids.discoverer,
  };
  const ok = check(response, {
    'ES metadata snapshot returned 200': (r) => r.status === 200,
    'ES metadata contains all recommended roles': () =>
      snapshot != null
      && Object.entries(expected).every(([role, user]) =>
        Array.isArray(snapshot[role]) && snapshot[role].includes(user)),
  });
  if (!ok)
  {
    esSnapshotFailures.add(1);
    reportFailure('es-snapshot', ids, 'start', {
      status: response.status,
      snapshotRoleKeys: snapshot ? Object.keys(snapshot) : [],
    });
  }

  return ok;
}

function pendingTask(ids, employeeId, stage, slotKey, expectedUser) {
  const started = Date.now();
  const response = http.get(
    `${baseUrl}/api/tasks/pending?employeeId=${encodeURIComponent(employeeId)}&pageIndex=1&pageSize=20`,
    { headers: headers(), tags: { operation: 'problem-zero-pending', stage } },
  );
  pendingDuration.add(Date.now() - started, { stage });
  const page = unwrap(response);
  const task = page?.items?.find(item => item.businessId === ids.businessId);
  const pendingOk = check(response, {
    [`${stage} pending returned 200`]: (r) => r.status === 200,
    [`${stage} pending contains workflow task`]: () => !!task,
  });
  if (!pendingOk)
  {
    pendingFailures.add(1, { stage });
    reportFailure('pending-response', ids, stage, {
      status: response.status,
      taskFound: !!task,
      returnedBusinessIds: page?.items?.map(item => item.businessId) ?? [],
    });
  }

  if (slotKey) {
    const candidates = task?.slotRecommendedUsers?.[slotKey];
    const recommendationOk = check(response, {
      [`${stage} returns recommended user`]: () =>
        Array.isArray(candidates) && candidates.includes(expectedUser),
      [`${stage} returns recommendation lock`]: () =>
        task?.restrictToRecommended?.[slotKey] === true,
    });
    if (!recommendationOk)
    {
      recommendationFailures.add(1, { stage });
      reportFailure('pending-recommendation', ids, stage, {
        slotKey,
        expectedUser,
        returnedUsers: candidates ?? null,
        returnedLock: task?.restrictToRecommended?.[slotKey] ?? null,
      });
    }
  }

  return task;
}

function complete(ids, employeeId, stage, nextSlotSelections = [], businessVariables = {}) {
  const response = http.post(
    `${baseUrl}/api/tasks/complete`,
    JSON.stringify({
      businessId: ids.businessId,
      employeeId,
      action: 1,
      comment: `recommendation consistency ${stage}`,
      nextSlotSelections,
      businessVariables,
    }),
    { headers: headers(), tags: { operation: 'problem-zero-complete', stage } },
  );
  const ok = check(response, {
    [`${stage} complete returned 200`]: (r) => r.status === 200,
  });
  if (!ok)
  {
    completeFailures.add(1, { stage });
    reportFailure('complete-api', ids, stage, { status: response.status });
  }

  return ok;
}

function verifyRender(ids, stage) {
  if (!verifyFlowRender)
    return;

  const started = Date.now();
  const response = http.get(
    `${baseUrl}/api/processes/${encodeURIComponent(ids.businessId)}/flow-render`,
    { headers: headers(), tags: { operation: 'problem-zero-flow-render', stage } },
  );
  flowRenderDuration.add(Date.now() - started, { stage });
  const ok = check(response, {
    [`${stage} flow render returned 200`]: (r) => r.status === 200,
  });
  if (!ok)
  {
    flowRenderFailures.add(1, { stage });
    reportFailure('flow-render', ids, stage, { status: response.status });
  }
}

export function recommendationConsistency() {
  const ids = idsFor(exec.scenario.iterationInTest);
  const start = startWorkflow(ids);
  if (!start?.processInstanceId)
    return;

  verifyEsSnapshot(start.processInstanceId, ids);
  pendingTask(ids, ids.starter, 'starter', 'team_leader', ids.team);
  verifyRender(ids, 'starter');
  if (!complete(ids, ids.starter, 'starter', [
    { slotKey: 'team_leader', users: [ids.team] },
  ])) return;

  pendingTask(ids, ids.team, 'team', 'quality_group', ids.quality);
  verifyRender(ids, 'team');
  if (!complete(ids, ids.team, 'team', [
    { slotKey: 'quality_group', users: [ids.quality] },
  ])) return;

  pendingTask(ids, ids.quality, 'quality', 'responsible_person', ids.responsible);
  verifyRender(ids, 'quality');
  if (!complete(ids, ids.quality, 'quality', [
    { slotKey: 'responsible_person', users: [ids.responsible] },
  ], { IS_SOLVED: false })) return;

  pendingTask(ids, ids.responsible, 'responsible', 'counterpart_leader', ids.counterpart);
  verifyRender(ids, 'responsible');
  if (!complete(ids, ids.responsible, 'responsible', [
    { slotKey: 'counterpart_leader', users: [ids.counterpart] },
  ], { PROBLEM_ATTRIBUTE: true })) return;

  pendingTask(ids, ids.counterpart, 'counterpart', 'discoverer_after_counterpart', ids.discoverer);
  verifyRender(ids, 'counterpart');
  if (!complete(ids, ids.counterpart, 'counterpart', [
    { slotKey: 'discoverer_after_counterpart', users: [ids.discoverer] },
  ])) return;

  pendingTask(ids, ids.discoverer, 'discoverer', null, null);
  verifyRender(ids, 'discoverer');
  complete(ids, ids.discoverer, 'discoverer');
}
