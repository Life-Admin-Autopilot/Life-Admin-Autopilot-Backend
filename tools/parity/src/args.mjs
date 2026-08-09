import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const HARNESS_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

const DEFAULTS = {
  reference: 'http://localhost:4100',
  candidate: 'http://localhost:5100',
  out: path.join(HARNESS_DIR, 'out', 'parity-report.json'),
  scenarios: path.join(HARNESS_DIR, 'scenarios'),
  timeoutMs: 20000,
  maxWaitSeconds: 0,
};

const CONTRACT_MARKER = path.join('docs', 'contract', 'paths.auth.yaml');
const CONTRACT_FALLBACKS = ['/Users/mina/Documents/Mina/Life-Admin-Autopilot-Backend/docs/contract'];

export function findContractDir(explicit) {
  const tried = [];
  const consider = (dir) => {
    tried.push(dir);
    return fs.existsSync(path.join(dir, 'paths.auth.yaml')) ? dir : null;
  };
  if (explicit) {
    const hit = consider(path.resolve(explicit));
    if (hit) return hit;
    throw new Error(`--contract ${explicit} does not contain paths.auth.yaml`);
  }
  if (process.env.PARITY_CONTRACT_DIR) {
    const hit = consider(path.resolve(process.env.PARITY_CONTRACT_DIR));
    if (hit) return hit;
  }
  let dir = HARNESS_DIR;
  for (let i = 0; i < 8; i += 1) {
    for (const candidate of [
      path.join(dir, CONTRACT_MARKER),
      path.join(dir, 'Life-Admin-Autopilot-Backend', CONTRACT_MARKER),
    ]) {
      if (fs.existsSync(candidate)) return path.dirname(candidate);
      tried.push(path.dirname(candidate));
    }
    const up = path.dirname(dir);
    if (up === dir) break;
    dir = up;
  }
  for (const fallback of CONTRACT_FALLBACKS) {
    const hit = consider(fallback);
    if (hit) return hit;
  }
  throw new Error(`Could not locate the frozen contract. Set PARITY_CONTRACT_DIR.\nLooked in:\n  ${tried.join('\n  ')}`);
}

export function parseArgs(argv) {
  const options = {
    reference: DEFAULTS.reference,
    candidate: DEFAULTS.candidate,
    contract: null,
    scenarios: DEFAULTS.scenarios,
    out: DEFAULTS.out,
    selfTest: false,
    only: [],
    tags: [],
    skipTags: ['strict-ratelimit'],
    maskIp: false,
    authSweep: true,
    colour: process.stdout.isTTY === true && !process.env.NO_COLOR,
    timeoutMs: DEFAULTS.timeoutMs,
    maxWaitSeconds: DEFAULTS.maxWaitSeconds,
    list: false,
    help: false,
  };

  const args = [...argv];
  while (args.length) {
    const arg = args.shift();
    const next = () => {
      const value = args.shift();
      if (value === undefined) throw new Error(`${arg} needs a value`);
      return value;
    };
    switch (arg) {
      case '--reference': options.reference = next().replace(/\/$/, ''); break;
      case '--candidate': options.candidate = next().replace(/\/$/, ''); break;
      case '--contract': options.contract = next(); break;
      case '--scenarios': options.scenarios = path.resolve(next()); break;
      case '--out': options.out = path.resolve(next()); break;
      case '--self-test': options.selfTest = true; break;
      case '--only': options.only.push(next()); break;
      case '--tag': options.tags.push(next()); break;
      case '--skip-tag': options.skipTags.push(next()); break;
      case '--include-strict': options.skipTags = options.skipTags.filter((t) => t !== 'strict-ratelimit'); break;
      case '--mask-ip': options.maskIp = true; break;
      case '--no-auth-sweep': options.authSweep = false; break;
      case '--no-colour':
      case '--no-color': options.colour = false; break;
      case '--colour':
      case '--color': options.colour = true; break;
      case '--timeout': options.timeoutMs = Number(next()); break;
      case '--max-wait': options.maxWaitSeconds = Number(next()); break;
      case '--list': options.list = true; break;
      case '-h':
      case '--help': options.help = true; break;
      default:
        throw new Error(`unknown flag ${arg}`);
    }
  }

  if (options.selfTest) options.candidate = options.reference;
  return options;
}

export const HELP = `
kitto parity harness — differential test of the .NET port against the Node reference

  node tools/parity/run.mjs [flags]

  --reference URL     reference (Node) base URL          default ${DEFAULTS.reference}
  --candidate URL     candidate (.NET) base URL          default ${DEFAULTS.candidate}
  --self-test         point BOTH sides at --reference; every covered row must PASS
  --contract DIR      frozen contract directory          default: auto-discovered
  --scenarios DIR     scenario corpus directory          default tools/parity/scenarios
  --out FILE          JSON report path                   default tools/parity/out/parity-report.json
  --only SUBSTR       run scenarios whose name contains SUBSTR (repeatable)
  --tag TAG           run only scenarios carrying TAG (repeatable)
  --skip-tag TAG      skip scenarios carrying TAG (repeatable; 'strict-ratelimit' by default)
  --include-strict    also run the strictAuthLimiter scenario (5 slots/side against a 5-per-hour budget)
  --mask-ip           additionally mask client IPs in session listings
  --no-auth-sweep     skip the generated "every authed route returns 401" scenario
  --timeout MS        per-request timeout                default ${DEFAULTS.timeoutMs}
  --max-wait SECONDS  on an unexpected 429, wait this long for the window to reset (default 0 = never)
  --list              print the corpus and its coverage, run nothing
  --no-colour         plain output

exit codes: 0 all covered rows pass, 1 at least one FAIL, 2 harness/corpus error
`;
