// Terminal table + machine-readable JSON.

import { STATES } from './matrix.mjs';

const COLOURS = {
  PASS: '[32m',
  FAIL: '[31m',
  ERROR: '[35m',
  'RATE-LIMITED': '[33m',
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

export function renderSummary(matrix, { colour = true, elapsedMs } = {}) {
  const parts = STATES.filter((state) => matrix.summary[state] > 0).map(
    (state) => `${paint(state, state, colour)} ${matrix.summary[state]}`,
  );
  return [
    '',
    `${matrix.summary.total} contract operations  |  ${parts.join('   ')}`,
    `elapsed ${(elapsedMs / 1000).toFixed(1)}s`,
  ].join('\n');
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
