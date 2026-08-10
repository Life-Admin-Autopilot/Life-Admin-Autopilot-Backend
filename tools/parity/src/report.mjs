// Terminal table + machine-readable JSON.

import { STATES } from './matrix.mjs';

const COLOURS = {
  PASS: '[32m',
  FAIL: '[31m',
  ERROR: '[35m',
  'RATE-LIMITED': '[33m',
  'SETUP-FAILED': '[35m',
  UNREACHABLE: '[36m',
  SKIPPED: '[90m',
  'NOT-COVERED': '[33m',
};
const RESET = '[0m';

function paint(text, state, useColour) {
  if (!useColour) return text;
  return (COLOURS[state] ?? '') + text + RESET;
}

function pad(text, width) {
  const value = String(text);
  return value.length >= width ? value.slice(0, width) : value + ' '.repeat(width - value.length);
}

export function renderMatrix(matrix, { colour = true } = {}) {
  const lines = [];
  const widths = { op: 28, route: 42, state: 13, codes: 40 };
  lines.push(
    pad('OPERATION', widths.op) +
      pad('ROUTE', widths.route) +
      pad('STATUS', widths.state) +
      pad('CODES SEEN / DECLARED', widths.codes),
  );
  lines.push('-'.repeat(widths.op + widths.route + widths.state + widths.codes));

  let domain = null;
  for (const row of matrix.rows) {
    if (row.domain !== domain) {
      domain = row.domain;
      lines.push('');
      lines.push(`## ${domain}`);
    }
    const codes = `${row.observedCodes.join(',') || '-'} / ${row.declaredCodes.join(',')}`;
    lines.push(
      pad(row.operationId, widths.op) +
        pad(`${row.method} ${row.pathTemplate}`, widths.route) +
        paint(pad(row.state, widths.state), row.state, colour) +
        pad(codes, widths.codes),
    );
  }
  return lines.join('\n');
}

export function renderCoverageGaps(matrix) {
  const lines = [];
  const notCovered = matrix.rows.filter((r) => r.state === 'NOT-COVERED');
  const skipped = matrix.rows.filter((r) => r.state === 'SKIPPED');

  lines.push('');
  lines.push(`NOT COVERED BY ANY SCENARIO (${notCovered.length}/${matrix.summary.total})`);
  if (!notCovered.length) lines.push('  (none)');
  for (const row of notCovered) lines.push(`  - ${row.operationId}  ${row.method} ${row.pathTemplate}`);

  if (skipped.length) {
    lines.push('');
    lines.push(`COVERED BUT NOT RUN THIS INVOCATION (${skipped.length})`);
    for (const row of skipped) {
      lines.push(`  - ${row.operationId}  ${row.method} ${row.pathTemplate}`);
      for (const note of row.notes) lines.push(`      ${note}`);
    }
  }

  const shallow = matrix.rows.filter((r) => r.state === 'PASS' && r.notes.some((n) => n.startsWith('deeper coverage')));
  if (shallow.length) {
    lines.push('');
    lines.push(`GREEN BUT ONLY SHALLOWLY EXERCISED (${shallow.length})`);
    for (const row of shallow) {
      lines.push(`  - ${row.operationId}: saw ${row.observedCodes.join(',')} only — ${row.notes.join('; ')}`);
    }
  }

  const partial = matrix.rows.filter((r) => r.observedCodes.length && r.uncoveredCodes.length);
  if (partial.length) {
    lines.push('');
    lines.push(`RESPONSE CODES DECLARED BY THE CONTRACT BUT NEVER EXERCISED (${partial.length} operations)`);
    for (const row of partial) {
      lines.push(`  - ${row.operationId}: missing ${row.uncoveredCodes.join(',')} (saw ${row.observedCodes.join(',')})`);
    }
  }
  return lines.join('\n');
}

export function renderFailures(stepResults, { colour = true } = {}) {
  const interesting = stepResults.filter((s) => s.state === 'FAIL' || s.state === 'ERROR');
  if (!interesting.length) return '';
  const lines = ['', paint('MISMATCHES', 'FAIL', colour), ''];
  for (const step of interesting) {
    lines.push(
      `${paint(step.state, step.state, colour)}  ${step.scenario} / ${step.name}` +
        (step.op ? `  [${step.op}]` : '  [framework probe]'),
    );
    if (step.request) lines.push(`       ${step.request.method} ${step.request.path}`);
    for (const note of step.notes) lines.push(`       note: ${note}`);
    for (const diff of step.diffs) {
      lines.push(`       ${diff.path}`);
      lines.push(`         reference: ${diff.reference}`);
      lines.push(`         candidate: ${diff.candidate}`);
    }
    if (step.diffsTruncated) lines.push('       ... more differences suppressed');
    lines.push('');
  }
  return lines.join('\n');
}

/**
 * Steps whose `poll` never reached a terminal state.
 *
 * These are the corpus's timing-sensitive rows: the response that got compared
 * is a snapshot of work still in progress, so the verdict depends on which
 * side's background worker got further before the deadline. Printed whatever
 * the verdict — a row that passes today because both sides were equally
 * unfinished is exactly the row that flips tomorrow.
 */
export function renderUnsettledPolls(stepResults) {
  const unsettled = stepResults.filter((step) => (step.pollUnsettled ?? []).length);
  if (!unsettled.length) return '';
  const lines = ['', `POLLS THAT NEVER SETTLED — TIMING-SENSITIVE COMPARISONS (${unsettled.length})`];
  for (const step of unsettled) {
    lines.push(
      `  - ${step.scenario} / ${step.name} [${step.state}] — not settled on: ${step.pollUnsettled.join(', ')}`,
    );
  }
  lines.push(
    '    A worker that had not finished is not a result. Re-run against the dev-mode',
    '    reference (:4100, workers ON) before trusting these rows either way.',
  );
  return lines.join('\n') + '\n';
}

/**
 * What the header policy in headers.mjs let through.
 *
 * A header divergence that is NOT failed still has to be visible, or the
 * allowlist becomes the same kind of blind spot it was written to remove. Both
 * sections are printed whenever they are non-empty, on a green run as well as a
 * red one.
 */
export function renderHeaderPolicy(stepResults) {
  const buckets = new Map();
  for (const step of stepResults) {
    for (const entry of step.headerPolicy ?? []) {
      const key = [entry.kind, entry.header, entry.direction].join('\u0000');
      if (!buckets.has(key)) buckets.set(key, { ...entry, steps: [] });
      buckets.get(key).steps.push(`${step.scenario} / ${step.name}`);
    }
  }
  if (!buckets.size) return '';

  const lines = [];
  const byKind = (kind) => [...buckets.values()].filter((b) => b.kind === kind);

  const runMode = byKind('run-mode');
  if (runMode.length) {
    lines.push('');
    lines.push(`HEADER DIFFERENCES ALLOWED BY THE RUN-MODE ALLOWLIST (${runMode.length})`);
    for (const bucket of runMode) {
      lines.push(`  - ${bucket.header} (${bucket.direction}) on ${bucket.steps.length} step(s)`);
      lines.push(`      ${bucket.reason}`);
      lines.push(`      first: ${bucket.steps[0]}`);
    }
  }

  const declared = byKind('declared-exception');
  if (declared.length) {
    lines.push('');
    lines.push(`DECLARED HEADER EXCEPTIONS — NOT FAILED, STILL DIVERGENT (${declared.length})`);
    for (const bucket of declared) {
      lines.push(`  - ${bucket.header} (${bucket.direction}) on ${bucket.steps.length} step(s)`);
      lines.push(`      why not failed: ${bucket.reason}`);
      lines.push(`      remove when:    ${bucket.removeWhen}`);
    }
  }
  return lines.join('\n') + '\n';
}

export function renderSummary(matrix, { colour = true, elapsedMs } = {}) {
  const parts = STATES.filter((state) => matrix.summary[state] > 0).map(
    (state) => `${paint(state, state, colour)} ${matrix.summary[state]}`,
  );
  const lines = ['', `${matrix.summary.total} contract operations  |  ${parts.join('   ')}`];
  if (matrix.summary.frameworkProbeFailures > 0) {
    lines.push(
      `${paint('plus ' + matrix.summary.frameworkProbeFailures + ' failing framework probe(s)', 'FAIL', colour)}` +
        ' — steps with no contract operation, so they fold into no row above',
    );
  }
  lines.push(`elapsed ${(elapsedMs / 1000).toFixed(1)}s`);
  return lines.join('\n');
}

export function buildJsonReport(context, matrix, stepResults) {
  return {
    schemaVersion: 1,
    generatedAt: new Date().toISOString(),
    runId: context.runId,
    mode: context.mode,
    referenceBaseUrl: context.referenceBaseUrl,
    candidateBaseUrl: context.candidateBaseUrl,
    contractDir: context.contractDir,
    contractPathCount: context.contractPathCount,
    masks: context.baseMasks,
    scenariosRun: context.ranScenarios.map((s) => ({ name: s.name, file: s.file, tags: s.tags })),
    scenariosSkipped: context.skippedScenarios,
    loadWarnings: context.loadWarnings,
    summary: matrix.summary,
    operations: matrix.rows,
    steps: stepResults,
  };
}
