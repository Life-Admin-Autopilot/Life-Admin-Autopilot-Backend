// Folds step-level results into the per-operation verdict matrix and
// cross-references it against the contract's full operation inventory.

import { diffObservations } from './diff.mjs';

export const STATES = ['ERROR', 'FAIL', 'RATE-LIMITED', 'UNREACHABLE', 'PASS', 'SKIPPED', 'NOT-COVERED'];
const PRECEDENCE = new Map(STATES.map((state, index) => [state, index]));

function worse(a, b) {
  if (a === null) return b;
  return PRECEDENCE.get(a) <= PRECEDENCE.get(b) ? a : b;
}

/** Compare one scenario's two side-runs, step by step. */
export function compareScenario(scenario, referenceRun, candidateRun) {
  const steps = [];
  const count = Math.max(referenceRun.steps.length, candidateRun.steps.length);

  for (let i = 0; i < count; i += 1) {
    const definition = scenario.steps[i] ?? {};
    const reference = referenceRun.steps[i];
    const candidate = candidateRun.steps[i];
    const entry = {
      scenario: scenario.name,
      name: definition.name ?? reference?.name ?? candidate?.name ?? `step#${i + 1}`,
      op: definition.op ?? null,
      frameworkProbe: definition.frameworkProbe === true,
      request: reference?.request ?? candidate?.request ?? null,
      referenceStatus: reference?.observation?.reachable ? reference.observation.status : null,
      candidateStatus: candidate?.observation?.reachable ? candidate.observation.status : null,
      diffs: [],
      notes: [],
      state: 'PASS',
    };

    if (reference?.harnessError || candidate?.harnessError) {
      entry.state = 'ERROR';
      entry.notes.push(reference?.harnessError ?? candidate?.harnessError);
      steps.push(entry);
      continue;
    }
    if (reference?.captureError) entry.notes.push(`reference: ${reference.captureError}`);
    if (reference?.pollSettled === false) entry.notes.push('reference: poll timed out before the resource settled');
    if (candidate?.pollSettled === false) entry.notes.push('candidate: poll timed out before the resource settled');

    if (!reference || !reference.observation.reachable) {
      entry.state = 'ERROR';
      entry.notes.push(`reference server unreachable: ${reference?.observation?.error ?? 'no result'}`);
      steps.push(entry);
      continue;
    }

    // A reference response that contradicts the scenario's own expectation
    // means the corpus is stale, not that the port is wrong.
    const expected = definition.expect?.status;
    if (expected !== undefined && reference.observation.status !== expected) {
      entry.state = 'ERROR';
      entry.notes.push(`reference returned ${reference.observation.status}, scenario expects ${expected}`);
    } else if (reference.observation.status === 429 && expected !== 429) {
      entry.state = 'RATE-LIMITED';
      entry.notes.push('reference returned 429 — auth rate limit budget exhausted');
    }

    if (!candidate || !candidate.observation.reachable) {
      if (entry.state === 'PASS') entry.state = 'UNREACHABLE';
      entry.notes.push(`candidate unreachable: ${candidate?.observation?.error ?? 'no result'}`);
      steps.push(entry);
      continue;
    }

    const comparison = diffObservations(reference.observation, candidate.observation, {
      statusOnly: definition.statusOnly === true,
    });
    entry.diffs = comparison.diffs;
    entry.diffsTruncated = comparison.truncated;
    entry.notes.push(...comparison.notes);
    if (comparison.diffs.length && entry.state === 'PASS') entry.state = 'FAIL';
    steps.push(entry);
  }

  return steps;
}

export function buildMatrix(contract, { stepResults, declared, ranScenarios }) {
  const byOp = new Map();
  for (const operation of contract.operations) {
    byOp.set(operation.operationId, {
      operationId: operation.operationId,
      domain: operation.domain,
      method: operation.method,
      pathTemplate: operation.pathTemplate,
      declaredCodes: operation.declaredCodes,
      observedCodes: new Set(),
      state: null,
      steps: [],
      notes: [],
    });
  }

  for (const step of stepResults) {
    if (!step.op) continue;
    const row = byOp.get(step.op);
    if (!row) continue;
    row.steps.push(step);
    if (step.referenceStatus !== null) row.observedCodes.add(String(step.referenceStatus));
    row.state = worse(row.state, step.state);
  }

  const ranNames = new Set(ranScenarios.map((s) => s.name));
  for (const row of byOp.values()) {
    const references = declared.get(row.operationId) ?? [];

    if (row.state !== null) {
      // An operation can be green on the strength of a shallow scenario (the
      // generated 401 sweep) while its deep scenario was skipped. Say so, or
      // the row reads as fully verified when it is not.
      const missed = [...new Set(references.map((r) => r.scenario))].filter((name) => !ranNames.has(name));
      if (missed.length) row.notes.push(`deeper coverage not run this invocation: ${missed.join(', ')}`);
      continue;
    }

    if (!references.length) {
      row.state = 'NOT-COVERED';
      continue;
    }
    const owners = [...new Set(references.map((r) => r.scenario))];
    row.state = 'SKIPPED';
    row.notes.push(`covered only by scenario(s) not run: ${owners.filter((o) => !ranNames.has(o)).join(', ')}`);
  }

  const rows = [...byOp.values()].map((row) => ({
    ...row,
    observedCodes: [...row.observedCodes].sort(),
    uncoveredCodes: row.declaredCodes.filter((code) => !row.observedCodes.has(code)),
    steps: row.steps.map((s) => ({ scenario: s.scenario, name: s.name, state: s.state })),
  }));

  const summary = Object.fromEntries(STATES.map((state) => [state, rows.filter((r) => r.state === state).length]));
  summary.total = rows.length;
  return { rows, summary };
}
