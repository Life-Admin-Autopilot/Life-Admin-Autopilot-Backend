// Scenario loading + validation.
//
// Validation is deliberately strict: a step that claims an operationId it does
// not actually exercise would inflate the coverage number, and an inflated
// coverage number is the main way a harness like this lies.

import fs from 'node:fs';
import path from 'node:path';

import { loadYaml } from './yaml.mjs';
import { pathMatchesOperation } from './contract.mjs';
import { ALL_MASKS } from './normalize.mjs';

const BODY_KEYS = ['body', 'rawText', 'rawFixture', 'jsonPadding'];

function countParams(template) {
  return (template.match(/\{[A-Za-z0-9_]+\}/g) ?? []).length;
}

function concretize(pathTemplate) {
  return pathTemplate.replace(/\{\{[^}]+\}\}/g, 'x').split('?')[0];
}

export async function loadScenarios(dir, contract) {
  const files = fs
    .readdirSync(dir)
    .filter((f) => f.endsWith('.yaml') || f.endsWith('.yml'))
    .sort();

  const scenarios = [];
  const errors = [];
  const warnings = [];

  for (const file of files) {
    const full = path.join(dir, file);
    let doc;
    try {
      doc = await loadYaml(full);
    } catch (error) {
      errors.push(`${file}: unparseable YAML — ${error.message}`);
      continue;
    }
    if (!doc || typeof doc !== 'object') {
      errors.push(`${file}: empty scenario file`);
      continue;
    }
    const scenario = {
      file,
      name: doc.name ?? file.replace(/\.[^.]+$/, ''),
      description: doc.description ?? '',
      tags: doc.tags ?? [],
      user: doc.user ?? 'fresh',
      skip: doc.skip === true,
      skipReason: doc.skipReason ?? '',
      steps: Array.isArray(doc.steps) ? doc.steps : [],
    };
    if (!scenario.steps.length) errors.push(`${file}: no steps`);
    if (!['fresh', 'none'].includes(scenario.user)) {
      errors.push(`${file}: user must be "fresh" or "none"`);
    }

    const seenNames = new Set();
    scenario.steps.forEach((step, index) => {
      const where = `${file} step#${index + 1}${step?.name ? ` (${step.name})` : ''}`;
      if (!step || typeof step !== 'object') {
        errors.push(`${where}: step must be a mapping`);
        return;
      }
      if (!step.name) errors.push(`${where}: missing name`);
      else if (seenNames.has(step.name)) errors.push(`${where}: duplicate step name`);
      else seenNames.add(step.name);

      if (!step.method) errors.push(`${where}: missing method`);
      if (!step.path) errors.push(`${where}: missing path`);

      const bodyKeys = BODY_KEYS.filter((k) => step[k] !== undefined);
      if (bodyKeys.length > 1) errors.push(`${where}: pick one of ${bodyKeys.join(', ')}`);

      for (const mask of [...(step.maskOff ?? []), ...(step.maskOn ?? [])]) {
        if (!ALL_MASKS.includes(mask)) errors.push(`${where}: unknown mask "${mask}"`);
      }

      if (step.op === undefined || step.op === null) {
        if (step.frameworkProbe !== true) {
          errors.push(`${where}: needs an "op" (contract operationId) or "frameworkProbe: true"`);
        }
        return;
      }

      const operation = contract.get(step.op);
      if (!operation) {
        errors.push(`${where}: op "${step.op}" is not in the contract`);
        return;
      }
      const method = String(step.method).toUpperCase();
      if (operation.method !== method) {
        errors.push(`${where}: op ${step.op} is ${operation.method}, step is ${method}`);
      }
      const concrete = concretize(String(step.path));
      if (!pathMatchesOperation(operation, concrete)) {
        errors.push(`${where}: path "${concrete}" does not match ${step.op} template "${operation.pathTemplate}"`);
      } else {
        // Guard against claiming a parameterised op when a literal route also
        // matches, e.g. calling /me/tasks/trash "taskGet".
        const tighter = contract.operations.filter(
          (candidate) =>
            candidate.method === method &&
            candidate.operationId !== operation.operationId &&
            countParams(candidate.pathTemplate) < countParams(operation.pathTemplate) &&
            pathMatchesOperation(candidate, concrete),
        );
        if (tighter.length) {
          warnings.push(
            `${where}: path also matches more specific op(s) ${tighter.map((t) => t.operationId).join(', ')} — is "${step.op}" right?`,
          );
        }
      }
    });

    scenarios.push(scenario);
  }

  if (!scenarios.length) errors.push(`no scenario files found in ${dir}`);
  return { scenarios, errors, warnings };
}

/** operationIds referenced by the corpus, whether or not it runs. */
export function declaredCoverage(scenarios) {
  const map = new Map();
  for (const scenario of scenarios) {
    for (const step of scenario.steps) {
      if (!step?.op) continue;
      if (!map.has(step.op)) map.set(step.op, []);
      map.get(step.op).push({ scenario: scenario.name, step: step.name });
    }
  }
  return map;
}

/**
 * `expect: { status: 200 }` or `expect: { status: [200, 202] }`.
 *
 * A list is only for steps whose reference status legitimately depends on
 * whether a background worker has finished — see the comment in
 * compareScenario().
 */
export function expectedStatuses(step) {
  const expected = step?.expect?.status;
  if (expected === undefined) return null;
  return Array.isArray(expected) ? expected : [expected];
}
