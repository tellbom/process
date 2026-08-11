import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';

const baseUrl = (__ENV.BASE_URL || 'http://127.0.0.1:5012').replace(/\/$/, '');
const token = __ENV.ACCESS_TOKEN || '';
const runId = __ENV.RUN_ID || `problem-zero-branches-${Date.now()}`;
const iterations = Number(__ENV.ITERATIONS || 60);
const slowDelayMs = Number(__ENV.SLOW_DELAY_MS || 15000);

const failures = new Counter('problem_zero_failures');
const solvedA = new Counter('problem_zero_solved_a_completed');
const solvedB = new Counter('problem_zero_solved_b_completed');
const specialA = new Counter('problem_zero_special_a_completed');
const specialB = new Counter('problem_zero_special_b_completed');
const nonspecialA = new Counter('problem_zero_nonspecial_a_completed');
const nonspecialB = new Counter('problem_zero_nonspecial_b_completed');
const startDuration = new Trend('problem_zero_start_duration', true);
const completeDuration = new Trend('problem_zero_complete_duration', true);
const solvedAFinal = new Trend('problem_zero_solved_a_final_duration', true);
const solvedBFinal = new Trend('problem_zero_solved_b_final_duration', true);
const specialAFinal = new Trend('problem_zero_special_a_final_duration', true);
const specialBFinal = new Trend('problem_zero_special_b_final_duration', true);
const nonspecialAFinal = new Trend('problem_zero_nonspecial_a_final_duration', true);
const nonspecialBFinal = new Trend('problem_zero_nonspecial_b_final_duration', true);

export const options = {
  discardResponseBodies: true,
  scenarios: {
    all_branches_ab: {
      executor: 'shared-iterations',
      exec: 'allBranchesAb',
      vus: Number(__ENV.VUS || Math.min(iterations, 100)),
      iterations,
      maxDuration: __ENV.MAX_DURATION || '60m',
    },
  },
  thresholds: {
    checks: ['rate==1'],
    http_req_failed: ['rate==0'],
    problem_zero_failures: ['count==0'],
  },
};

function headers() {
  return {
    Authorization: `Bearer ${token}`,
    'Content-Type': 'application/json',
  };
}

function cohort(index) {
  const branch = ['solved', 'special', 'nonspecial'][index % 3];
  const group = Math.floor(index / 3) % 2 === 0 ? 'A' : 'B';
  return { branch, group };
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

function fail(ids, branch, group, stage, response) {
  failures.add(1, { branch, group, stage });
  console.error(JSON.stringify({
    kind: 'problem_zero_branch_failure',
    businessId: ids.businessId,
    branch,
    group,
    stage,
    status: response?.status,
  }));
}

function start(ids, branch, group) {
  const response = http.post(
    `${baseUrl}/api/processes/start`,
    JSON.stringify({
      businessType: 'problem_zero',
      businessId: ids.businessId,
      requestId: ids.businessId,
      initialSlotSelections: [
        { slotKey: 'team_leader', users: [ids.team] },
      ],
      businessVariables: { starterAssignee: ids.starter },
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
        url: `${baseUrl}/api/test/process-callback/${group}?delayMs=${group === 'B' ? slowDelayMs : 0}`,
        timeoutSeconds: Math.max(10, Math.ceil(slowDelayMs / 1000) + 5),
        retryCount: 0,
      },
    }),
    {
      headers: headers(),
      responseType: 'text',
      tags: { operation: 'problem-zero-start', branch, group },
    },
  );
  startDuration.add(response.timings.duration, { branch, group });
  const ok = check(response, {
    'problem zero start returned 200': (r) => r.status === 200,
  });
  if (!ok) fail(ids, branch, group, 'start', response);
  return ok;
}

function complete(ids, employeeId, branch, group, stage, nextSlotSelections = [], businessVariables = {}, final = false) {
  const response = http.post(
    `${baseUrl}/api/tasks/complete`,
    JSON.stringify({
      businessId: ids.businessId,
      employeeId,
      action: 1,
      comment: `problem zero ${branch} ${group} ${stage}`,
      nextSlotSelections,
      businessVariables,
    }),
    { headers: headers(), tags: { operation: 'problem-zero-complete', branch, group, stage } },
  );
  completeDuration.add(response.timings.duration, { branch, group, stage });
  if (final) finalTrend(branch, group).add(response.timings.duration);
  const ok = check(response, {
    [`${stage} complete returned 200`]: (r) => r.status === 200,
  });
  if (!ok) fail(ids, branch, group, stage, response);
  return ok;
}

function finalTrend(branch, group) {
  if (branch === 'solved') return group === 'A' ? solvedAFinal : solvedBFinal;
  if (branch === 'special') return group === 'A' ? specialAFinal : specialBFinal;
  return group === 'A' ? nonspecialAFinal : nonspecialBFinal;
}

function markCompleted(branch, group) {
  if (branch === 'solved') (group === 'A' ? solvedA : solvedB).add(1);
  else if (branch === 'special') (group === 'A' ? specialA : specialB).add(1);
  else (group === 'A' ? nonspecialA : nonspecialB).add(1);
}

export function allBranchesAb() {
  const index = exec.scenario.iterationInTest;
  const ids = idsFor(index);
  const { branch, group } = cohort(index);

  if (!start(ids, branch, group)) return;
  if (!complete(ids, ids.starter, branch, group, 'starter', [
    { slotKey: 'team_leader', users: [ids.team] },
  ])) return;
  if (!complete(ids, ids.team, branch, group, 'team', [
    { slotKey: 'quality_group', users: [ids.quality] },
  ])) return;

  if (branch === 'solved') {
    if (!complete(ids, ids.quality, branch, group, 'quality-solved', [], { IS_SOLVED: true }, true)) return;
    markCompleted(branch, group);
    return;
  }

  if (!complete(ids, ids.quality, branch, group, 'quality-unsolved', [
    { slotKey: 'responsible_person', users: [ids.responsible] },
  ], { IS_SOLVED: false })) return;

  if (branch === 'special') {
    if (!complete(ids, ids.responsible, branch, group, 'responsible-special', [
      { slotKey: 'counterpart_leader', users: [ids.counterpart] },
    ], { PROBLEM_ATTRIBUTE: true })) return;
    if (!complete(ids, ids.counterpart, branch, group, 'counterpart', [
      { slotKey: 'discoverer_after_counterpart', users: [ids.discoverer] },
    ])) return;
  } else {
    if (!complete(ids, ids.responsible, branch, group, 'responsible-nonspecial', [
      { slotKey: 'discoverer_direct', users: [ids.discoverer] },
    ], { PROBLEM_ATTRIBUTE: false })) return;
  }

  if (!complete(ids, ids.discoverer, branch, group, 'discoverer', [], {}, true)) return;
  markCompleted(branch, group);
}
