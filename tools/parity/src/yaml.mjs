// Resolves js-yaml without adding a package.json dependency tree.
//
// Search order:
//   1. $PARITY_JS_YAML  (absolute path to a js-yaml package dir or entry file)
//   2. an upward walk for node_modules/js-yaml from this file
//   3. the known Steward checkout
//
// Kept in its own module so every other file can just `import { loadYaml }`.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const FALLBACKS = ['/Users/mina/Documents/Mina/Steward/node_modules/js-yaml'];

function candidates() {
  const out = [];
  if (process.env.PARITY_JS_YAML) out.push(process.env.PARITY_JS_YAML);
  let dir = HERE;
  for (let i = 0; i < 12; i += 1) {
    out.push(path.join(dir, 'node_modules', 'js-yaml'));
    const up = path.dirname(dir);
    if (up === dir) break;
    dir = up;
  }
  out.push(...FALLBACKS);
  return out;
}

function entryFor(candidate) {
  if (!fs.existsSync(candidate)) return null;
  if (fs.statSync(candidate).isFile()) return candidate;
  for (const rel of ['index.js', 'dist/js-yaml.mjs', 'lib/js-yaml.js']) {
    const full = path.join(candidate, rel);
    if (fs.existsSync(full)) return full;
  }
  return null;
}

let cached = null;

async function getYaml() {
  if (cached) return cached;
  const tried = [];
  for (const candidate of candidates()) {
    tried.push(candidate);
    const entry = entryFor(candidate);
    if (!entry) continue;
    const mod = await import(pathToFileURL(entry).href);
    cached = mod.default ?? mod;
    return cached;
  }
  throw new Error(
    'js-yaml not found. Set PARITY_JS_YAML to its package directory.\nLooked in:\n  ' +
      tried.join('\n  '),
  );
}

export async function loadYaml(filePath) {
  const yaml = await getYaml();
  return yaml.load(fs.readFileSync(filePath, 'utf8'));
}

export async function loadYamlString(text) {
  const yaml = await getYaml();
  return yaml.load(text);
}
