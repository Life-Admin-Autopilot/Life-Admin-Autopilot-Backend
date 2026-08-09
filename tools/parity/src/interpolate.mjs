// `{{var}}` substitution and `$.a.b[0].c` capture.

const PLACEHOLDER = /\{\{\s*([A-Za-z0-9_.]+)\s*\}\}/g;

export class MissingVariableError extends Error {}

export function interpolateString(text, vars) {
  return text.replace(PLACEHOLDER, (_match, name) => {
    if (!(name in vars)) throw new MissingVariableError(`unknown variable {{${name}}}`);
    const value = vars[name];
    return value === null || value === undefined ? '' : String(value);
  });
}

export function interpolate(node, vars) {
  if (typeof node === 'string') {
    // A string that is EXACTLY one placeholder keeps the variable's own type,
    // so `{{limit}}` can stay a number and `{{maybeNull}}` can stay null.
    const solo = /^\{\{\s*([A-Za-z0-9_.]+)\s*\}\}$/.exec(node);
    if (solo) {
      if (!(solo[1] in vars)) throw new MissingVariableError(`unknown variable {{${solo[1]}}}`);
      return vars[solo[1]];
    }
    return interpolateString(node, vars);
  }
  if (Array.isArray(node)) return node.map((item) => interpolate(item, vars));
  if (node !== null && typeof node === 'object') {
    const out = {};
    for (const [key, value] of Object.entries(node)) out[key] = interpolate(value, vars);
    return out;
  }
  return node;
}

/** Minimal JSONPath: `$`, `.key`, `[0]`. Returns undefined when absent. */
export function readPath(root, expression) {
  const trimmed = expression.startsWith('$') ? expression.slice(1) : expression;
  const tokens = trimmed.match(/\.[A-Za-z0-9_]+|\[\d+\]/g) ?? [];
  let cursor = root;
  for (const token of tokens) {
    if (cursor === null || cursor === undefined) return undefined;
    if (token.startsWith('[')) cursor = cursor[Number(token.slice(1, -1))];
    else cursor = cursor[token.slice(1)];
  }
  return cursor;
}

/** Every string literal in a raw (uninterpolated) step, for the preserve set. */
export function collectLiterals(node, into = new Set()) {
  if (typeof node === 'string') {
    if (!node.includes('{{')) into.add(node);
    return into;
  }
  if (Array.isArray(node)) {
    for (const item of node) collectLiterals(item, into);
    return into;
  }
  if (node !== null && typeof node === 'object') {
    for (const value of Object.values(node)) collectLiterals(value, into);
  }
  return into;
}
