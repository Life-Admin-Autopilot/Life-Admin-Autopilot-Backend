#!/usr/bin/env node
// Differential ("parity") test harness.
//
//   node tools/parity/run.mjs --self-test        # normalizer sanity check
//   node tools/parity/run.mjs                    # Node vs .NET
//
// A route may not be cut over until its row here is green.

import fs from 'node:fs';
import path from 'node:path';

import { parseArgs, findContractDir, HELP } from './src/args.mjs';
import { loadContract } from './src/contract.mjs';
import { loadScenarios, declaredCoverage } from './src/scenarios.mjs';
import { runScenario } from './src/runner.mjs';
import { compareScenario, buildMatrix } from './src/matrix.mjs';
import { renderMatrix, renderCoverageGaps, renderFailures, renderSummary, buildJsonReport } from './src/report.mjs';
import { DEFAULT_MASKS } from './src/normalize.mjs';
import { buildAuthSweepScenario } from './src/authSweep.mjs';
import { createBudget, estimateCost, AUTH_WINDOW, STRICT_WINDOW } from './src/ratelimit.mjs';
import { probe } from './src/http.mjs';

const EXIT_OK = 0;
const EXIT_FAIL = 1;
const EXIT_HARNESS = 2;

function selects(scenario, options) {
  if (scenario.skip) return { run: false, reason: scenario.skipReason || 'skip: true in scenario file' };
  if (options.only.length && !options.only.some((needle) => scenario.name.includes(needle))) {
    return { run: false, reason: `does not match --only ${options.only.join(', ')}` };
  }
  if (options.tags.length && !options.tags.some((tag) => scenario.tags.includes(tag))) {
    return { run: false, reason: `does not carry --tag ${options.tags.join(', ')}` };
  }
  const blocked = options.skipTags.find((tag) => scenario.tags.includes(tag));
  if (blocked) return { run: false, reason: `carries skipped tag "${blocked}"` };
  return { run: true, reason: null };
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    process.stdout.write(HELP);
    return EXIT_OK;
  }

  const contractDir = findContractDir(options.contract);
  const contract = await loadContract(contractDir);
  const loaded = await loadScenarios(options.scenarios, contract);
  const { errors, warnings } = loaded;
  const scenarios = options.authSweep
    ? [buildAuthSweepScenario(contract), ...loaded.scenarios]
    : loaded.scenarios;

  if (errors.length) {
    process.stderr.write('Scenario corpus is invalid:\n');
    for (const error of errors) process.stderr.write(`  - ${error}\n`);
    return EXIT_HARNESS;
  }
  for (const warning of warnings) process.stderr.write(`warning: ${warning}\n`);

  const declared = declaredCoverage(scenarios);
  const selections = scenarios.map((scenario) => ({ scenario, ...selects(scenario, options) }));
  const ranScenarios = selections.filter((s) => s.run).map((s) => s.scenario);
  const skippedScenarios = selections
    .filter((s) => !s.run)
    .map((s) => ({ name: s.scenario.name, file: s.scenario.file, reason: s.reason }));

  if (options.list) {
    process.stdout.write(describeCorpus(contract, scenarios, declared, selections));
    return EXIT_OK;
  }

  const baseMasks = options.maskIp ? [...DEFAULT_MASKS, 'IP'] : [...DEFAULT_MASKS];
  const runId = Date.now().toString(36).slice(-6) + Math.random().toString(36).slice(2, 5);
  const budget = createBudget();

  const referenceUp = await probe(options.reference);
  const candidateUp = options.selfTest ? referenceUp : await probe(options.candidate);

  printHeader({ options, contract, contractDir, ranScenarios, skippedScenarios, referenceUp, candidateUp, runId });
  if (!referenceUp) {
    process.stderr.write(
      `\nThe reference server at ${options.reference} is not answering /health. Start it first — see tools/parity/README.md.\n`,
    );
    return EXIT_HARNESS;
  }

  const runOptions = {
    runId,
    baseMasks,
    timeoutMs: options.timeoutMs,
    maxWaitMs: options.maxWaitSeconds * 1000,
    budget,
    onWait: (delay) => process.stdout.write(`  ...rate limited; waiting ${Math.round(delay / 1000)}s\n`),
  };

  const startedAt = Date.now();
  const stepResults = [];
  for (const scenario of ranScenarios) {
    process.stdout.write(`\n▸ ${scenario.name}`);

    // When the candidate is absent there is nothing to compare against, so we
    // do not replay the reference either — that would burn auth rate-limit
    // budget to produce a report that can only say UNREACHABLE anyway.
    if (!candidateUp) {
      for (const step of scenario.steps) {
        stepResults.push(
          setupFailureStep(scenario, step, `candidate not reachable at ${options.candidate}`, 'UNREACHABLE'),
        );
      }
      process.stdout.write('  candidate absent, reference not replayed\n');
      continue;
    }

    const referenceRun = await runScenario(options.reference, scenario, 'ref', runOptions);
    const candidateRun = await runScenario(options.candidate, scenario, 'cand', runOptions);

    if (referenceRun.setupError) {
      const state = referenceRun.setupRateLimited ? 'RATE-LIMITED' : 'ERROR';
      process.stdout.write(`  SETUP FAILED (reference): ${referenceRun.setupError}\n`);
      for (const step of scenario.steps) {
        stepResults.push(setupFailureStep(scenario, step, `reference setup failed: ${referenceRun.setupError}`, state));
      }
      continue;
    }
    if (candidateRun.setupError) {
      const state = candidateRun.setupRateLimited ? 'RATE-LIMITED' : 'UNREACHABLE';
      for (const step of scenario.steps) {
        stepResults.push(
          setupFailureStep(scenario, step, `candidate setup failed: ${candidateRun.setupError}`, state),
        );
      }
      process.stdout.write(`  candidate side ${candidateRun.setupRateLimited ? 'rate limited' : 'unavailable'}\n`);
      continue;
    }
    const compared = compareScenario(scenario, referenceRun, candidateRun);
    stepResults.push(...compared);
    const bad = compared.filter((s) => s.state === 'FAIL' || s.state === 'ERROR').length;
    process.stdout.write(`  ${compared.length} steps, ${bad} problem(s)\n`);
  }

  const matrix = buildMatrix(contract, { stepResults, declared, ranScenarios });
  const elapsedMs = Date.now() - startedAt;

  process.stdout.write('\n' + renderMatrix(matrix, { colour: options.colour }) + '\n');
  process.stdout.write(renderCoverageGaps(matrix) + '\n');
  process.stdout.write(renderFailures(stepResults, { colour: options.colour }));
  process.stdout.write(renderSummary(matrix, { colour: options.colour, elapsedMs }) + '\n');

  const report = buildJsonReport(
    {
      runId,
      mode: options.selfTest ? 'self-test' : 'reference-vs-candidate',
      referenceBaseUrl: options.reference,
      candidateBaseUrl: options.candidate,
      contractDir,
      contractPathCount: contract.pathCount,
      baseMasks,
      ranScenarios,
      skippedScenarios,
      loadWarnings: warnings,
    },
    matrix,
    stepResults,
  );
  fs.mkdirSync(path.dirname(options.out), { recursive: true });
  fs.writeFileSync(options.out, JSON.stringify(report, null, 2));
  process.stdout.write(`\nJSON report: ${options.out}\n`);

  if (matrix.summary['RATE-LIMITED'] > 0) {
    process.stdout.write(
      `\n${matrix.summary['RATE-LIMITED']} row(s) are RATE-LIMITED — those results are not trustworthy.\n` +
        'Re-run them with --max-wait 900, or shard with --only. See tools/parity/README.md section 7.\n',
    );
  }

  if (options.selfTest) {
    const impossible = matrix.rows.filter((r) => ['FAIL', 'UNREACHABLE', 'ERROR'].includes(r.state));
    if (impossible.length) {
      process.stdout.write(
        `\nSELF-TEST FAILED: ${impossible.length} row(s) are not green while comparing a server with itself. ` +
          'That is a harness bug (normalizer or corpus), not a port bug.\n',
      );
    } else if (matrix.summary['RATE-LIMITED'] > 0) {
      process.stdout.write(
        '\nSELF-TEST INCOMPLETE: nothing disagreed, but some rows never ran because of the rate limit.\n',
      );
    } else {
      process.stdout.write('\nSELF-TEST PASSED: every covered row is green against itself.\n');
    }
  }

  if (matrix.summary.ERROR > 0 || matrix.summary['RATE-LIMITED'] > 0) return EXIT_HARNESS;
  return matrix.summary.FAIL > 0 ? EXIT_FAIL : EXIT_OK;
}

function setupFailureStep(scenario, step, note, state) {
  return {
    scenario: scenario.name,
    name: step.name,
    op: step.op ?? null,
    frameworkProbe: step.frameworkProbe === true,
    request: { method: String(step.method).toUpperCase(), path: String(step.path) },
    referenceStatus: null,
    candidateStatus: null,
    diffs: [],
    notes: [note],
    state,
  };
}

function printHeader(ctx) {
  const { options, contract, contractDir, ranScenarios, skippedScenarios, referenceUp, candidateUp, runId } = ctx;
  const cost = estimateCost(ranScenarios, options.selfTest ? 2 : 1);
  const lines = [
    'kitto parity harness',
    `  run id      ${runId}`,
    `  mode        ${options.selfTest ? 'SELF-TEST (both sides = reference)' : 'reference vs candidate'}`,
    `  reference   ${options.reference}  ${referenceUp ? 'up' : 'DOWN'}`,
    `  candidate   ${options.candidate}  ${candidateUp ? 'up' : 'DOWN (rows will report UNREACHABLE)'}`,
    `  contract    ${contractDir}  (${contract.pathCount} paths, ${contract.operations.length} operations)`,
    `  scenarios   ${ranScenarios.length} running, ${skippedScenarios.length} skipped`,
    `  rate-limit  needs ~${cost.auth} authLimiter slot(s) of ${AUTH_WINDOW.max}/${AUTH_WINDOW.windowSeconds}s` +
      (cost.strict ? ` and ~${cost.strict} strict slot(s) of ${STRICT_WINDOW.max}/${STRICT_WINDOW.windowSeconds}s` : ''),
  ];
  if (cost.auth > AUTH_WINDOW.max) {
    lines.push(
      `  WARNING     that exceeds the reference server's per-IP budget, so this run will stall on a 429.`,
      `              Re-run with --max-wait 950 to ride out the window, or shard it with --only.`,
      `              Do NOT "fix" this with NODE_ENV=test — that also disables the background workers`,
      `              and the scan / voice scenarios would then never settle. See README section 7.`,
    );
  }
  process.stdout.write(lines.join('\n') + '\n');
}

function describeCorpus(contract, scenarios, declared, selections) {
  const lines = [`${contract.operations.length} contract operations across ${contract.pathCount} paths`, ''];
  for (const { scenario, run, reason } of selections) {
    lines.push(`${run ? '[run] ' : '[skip]'} ${scenario.name}  (${scenario.file})${run ? '' : ` — ${reason}`}`);
    lines.push(`        tags: ${scenario.tags.join(', ') || '-'}   steps: ${scenario.steps.length}   user: ${scenario.user}`);
  }
  const uncovered = contract.operations.filter((op) => !declared.has(op.operationId));
  lines.push('', `operations referenced by the corpus: ${declared.size}/${contract.operations.length}`);
  lines.push(`operations with no scenario at all: ${uncovered.length}`);
  for (const op of uncovered) lines.push(`  - ${op.operationId}  ${op.method} ${op.pathTemplate}`);
  return lines.join('\n') + '\n';
}

main()
  .then((code) => process.exit(code))
  .catch((error) => {
    process.stderr.write(`parity harness crashed: ${error.stack ?? error.message}\n`);
    process.exit(EXIT_HARNESS);
  });
