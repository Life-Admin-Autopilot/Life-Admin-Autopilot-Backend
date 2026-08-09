// Loads the frozen contract and exposes the operation inventory that the
// coverage matrix is cross-referenced against.
import fs from 'node:fs';
import path from 'node:path';

import { loadYaml } from './yaml.mjs';

export const CONTRACT_FILES = ['paths.auth.yaml', 'paths.tasks.yaml', 'paths.integrations.yaml'];
const HTTP_METHODS = ['get', 'post', 'put', 'patch', 'delete', 'head', 'options'];

/**
 * Turn `/me/tasks/{id}/subtasks/{subId}` into a matcher that accepts a
 * concrete path. Used to prove a scenario step really exercises the operation
 * it claims to — the single most important integrity check in the harness,
 * because a mistyped path would silently inflate the coverage number.
 */
function templateToRegex(template) {
  const escaped = template.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const withParams = escaped.replace(/\\\{[A-Za-z0-9_]+\\\}/g, '[^/]+');
  return new RegExp('^' + withParams + '$');
}

export async function loadContract(contractDir) {
  const missing = CONTRACT_FILES.filter((f) => !fs.existsSync(path.join(contractDir, f)));
  if (missing.length) {
    throw new Error(`Contract files missing from ${contractDir}: ${missing.join(', ')}`);
  }

  const operations = [];
  const byId = new Map();
  let pathCount = 0;

  for (const file of CONTRACT_FILES) {
    const doc = await loadYaml(path.join(contractDir, file));
    const domain = file.replace(/^paths\./, '').replace(/\.yaml$/, '');
    for (const [pathTemplate, item] of Object.entries(doc.paths ?? {})) {
      pathCount += 1;
      for (const [method, op] of Object.entries(item)) {
        if (!HTTP_METHODS.includes(method)) continue;
        const operationId = op.operationId;
        if (!operationId) {
          throw new Error(`Contract operation without operationId: ${method} ${pathTemplate}`);
        }
        if (byId.has(operationId)) {
          throw new Error(`Duplicate operationId in contract: ${operationId}`);
        }
        const entry = {
          operationId,
          domain,
          method: method.toUpperCase(),
          pathTemplate,
          summary: op.summary ?? '',
          declaredCodes: Object.keys(op.responses ?? {}).sort(),
          requiresAuth: Array.isArray(op.security) ? op.security.length > 0 : true,
          matcher: templateToRegex(pathTemplate),
        };
        operations.push(entry);
        byId.set(operationId, entry);
      }
    }
  }

  return {
    dir: contractDir,
    pathCount,
    operations,
    byId,
    get(operationId) {
      return byId.get(operationId);
    },
  };
}

/**
 * True when a concrete request path (query string already stripped) is an
 * instance of the operation's path template.
 */
export function pathMatchesOperation(operation, concretePath) {
  return operation.matcher.test(concretePath);
}
