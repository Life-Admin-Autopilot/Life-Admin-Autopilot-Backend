// Replays one scenario against ONE base URL. The two sides are replayed
// independently — no shared state, no shared user, no shared ids — and only
// the normalized observations are compared afterwards.

import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

import { request } from './http.mjs';
import { observe } from './observe.mjs';
import { createMaskContext, resolveMasks } from './normalize.mjs';
import { interpolate, interpolateString, readPath, collectLiterals, MissingVariableError } from './interpolate.mjs';
import { buildRawBody, buildJsonPadding } from './fixtures.mjs';
import { noteRateLimit, retryDelayMs, sleep } from './ratelimit.mjs';
import { expectedStatuses } from './scenarios.mjs';

const DEFAULT_USER_AGENT = 'kitto-parity/1';
const execFileAsync = promisify(execFile);

function buildQuery(query) {
  if (!query || !Object.keys(query).length) return '';
  const parts = [];
  for (const [key, value] of Object.entries(query)) {
    if (value === null || value === undefined) parts.push(encodeURIComponent(key));
    else parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`);
  }
  return '?' + parts.join('&');
}

/** Everything written literally in the YAML is identical on both sides. */
function preserveSetFor(scenario) {
  const set = new Set();
  for (const step of scenario.steps) {
    collectLiterals(step.body, set);
    collectLiterals(step.headers, set);
    collectLiterals(step.query, set);
    // Literal path segments too: a hard-coded "does not exist" ObjectId is an
    // input, so if a response echoes it we want the echo compared, not masked.
    for (const segment of String(step.path ?? '').split('/')) {
      if (segment && !segment.includes('{{')) set.add(segment);
    }
  }
  return set;
}

function seedVariables(scenario, side, runId) {
  // Deterministic per (scenario, side) so a re-run is reproducible, unique so
  // a --self-test never collides two sides in one database.
  const slug = scenario.name.replace(/[^a-z0-9]+/gi, '-').toLowerCase();
  const email = `parity+${runId}-${slug}-${side}@example.com`;
  const email2 = `parity+${runId}-${slug}-${side}-alt@example.com`;
  return {
    vars: { email, email2, password: 'CorrectHorse9!', newPassword: 'Tr0ubador-Stapler!' },
    volatileInputs: new Set([email, email2]),
  };
}

async function send(baseUrl, plan, options) {
  let result = await request(baseUrl, plan);
  noteRateLimit(options.budget, options.side, result);
  let waited = 0;
  while (result.ok && result.status === 429 && !plan.expects429) {
    const delay = retryDelayMs(result, options.maxWaitMs - waited);
    if (delay === null) break;
    options.onWait?.(delay, plan);
    await sleep(delay);
    waited += delay;
    result = await request(baseUrl, plan);
    noteRateLimit(options.budget, options.side, result);
  }
  return result;
}

function planFor(step, vars, defaults) {
  const headers = { 'User-Agent': DEFAULT_USER_AGENT, ...interpolate(step.headers ?? {}, vars) };
  if (step.auth !== false && (step.auth === true || step.auth === undefined) && vars.accessToken) {
    headers.Authorization = `Bearer ${vars.accessToken}`;
  }
  if (typeof step.auth === 'string') headers.Authorization = interpolateString(step.auth, vars);

  let body;
  const raw = buildRawBody(step);
  const padded = buildJsonPadding(step);
  if (raw !== undefined) {
    body = raw;
    headers['Content-Type'] = step.contentType ?? headers['Content-Type'] ?? 'application/octet-stream';
  } else if (padded !== undefined) {
    body = JSON.stringify(padded);
    headers['Content-Type'] = step.contentType ?? 'application/json';
  } else if (step.body !== undefined) {
    body = JSON.stringify(interpolate(step.body, vars));
    headers['Content-Type'] = step.contentType ?? 'application/json';
  } else if (step.contentType) {
    headers['Content-Type'] = step.contentType;
  }

  const path = interpolateString(String(step.path), vars) + buildQuery(interpolate(step.query, vars));
  return {
    method: String(step.method).toUpperCase(),
    path,
    headers,
    body,
    timeoutMs: step.timeoutMs ?? defaults.timeoutMs,
    expects429: (expectedStatuses(step) ?? []).includes(429),
  };
}

/** Poll a step until its response settles, so background workers cannot race. */
async function runPoll(baseUrl, step, vars, options) {
  const { until, in: settled = [], timeoutMs = 45000, intervalMs = 1500 } = step.poll;
  const deadline = Date.now() + timeoutMs;
  let last = null;
  let attempts = 0;
  for (;;) {
    attempts += 1;
    last = await send(baseUrl, planFor(step, vars, options), options);
    if (!last.ok) return { result: last, attempts, settled: false };
    let value;
    try {
      value = readPath(JSON.parse(last.bytes.toString('utf8')), until);
    } catch {
      value = undefined;
    }
    if (settled.includes(value)) return { result: last, attempts, settled: true, value };
    if (Date.now() >= deadline) return { result: last, attempts, settled: false, value };
    await sleep(intervalMs);
  }
}

/**
 * PROVISIONING IS THE ONE PLACE EVERY SCENARIO TOUCHES THE SAME ROUTE.
 *
 * A `user: fresh` scenario gets its account from `POST /auth/signup` on the
 * side being replayed — which on the candidate means every authenticated row in
 * the corpus depends on candidate signup working. That coupling is unavoidable
 * for a black-box harness (there is no other way to obtain a token the
 * candidate will accept), but it must not be reported as if each row were
 * individually broken.
 *
 * So the failure is classified rather than flattened into "unreachable":
 *
 *   'unreachable'    the server did not answer at all — nothing to say about
 *                    the rows, and the same verdict as before.
 *   'rate-limited'   the authLimiter budget ran out; the rows never ran.
 *   'signup-broken'  the server ANSWERED and got signup wrong. That is a
 *                    candidate defect in exactly one route, and it is reported
 *                    as SETUP-FAILED naming that route, not as N unreachable
 *                    rows. Exit code 2: the run is not trustworthy.
 *
 * `--seed-candidate-user CMD` breaks the coupling entirely: see seedViaCommand.
 */
async function provisionUser(baseUrl, vars, options) {
  if (options.side === 'cand' && options.seedCandidateUser) {
    return seedViaCommand(baseUrl, vars, options);
  }

  const plan = {
    method: 'POST',
    path: '/auth/signup',
    headers: { 'User-Agent': DEFAULT_USER_AGENT, 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: vars.email, password: vars.password }),
    timeoutMs: options.timeoutMs,
  };
  const result = await send(baseUrl, plan, options);
  if (!result.ok) return { ok: false, kind: 'unreachable', error: `signup unreachable: ${result.error}` };
  if (result.status === 429) {
    return {
      ok: false,
      kind: 'rate-limited',
      error:
        'provisioning signup was rate limited (authLimiter is 20 per 15 minutes per IP). ' +
        'Re-run with --max-wait 900, or shard with --only. See tools/parity/README.md section 7.',
    };
  }
  if (result.status !== 201) {
    return {
      ok: false,
      kind: 'signup-broken',
      error:
        `POST /auth/signup returned ${result.status}, expected 201: ` +
        `${result.bytes.toString('utf8').slice(0, 200)}`,
    };
  }

  let parsed;
  try {
    parsed = JSON.parse(result.bytes.toString('utf8'));
  } catch {
    return { ok: false, kind: 'signup-broken', error: 'POST /auth/signup returned 201 with a non-JSON body' };
  }
  const accessToken = parsed?.tokens?.accessToken;
  if (typeof accessToken !== 'string' || !accessToken) {
    return {
      ok: false,
      kind: 'signup-broken',
      error: 'POST /auth/signup returned 201 but no $.tokens.accessToken, so nothing downstream can authenticate',
    };
  }
  vars.accessToken = accessToken;
  vars.refreshToken = parsed.tokens.refreshToken;
  vars.userId = parsed.user?.id;
  return { ok: true };
}

/**
 * Provision the candidate user WITHOUT calling candidate signup.
 *
 * The command is run with `PARITY_BASE_URL`, `PARITY_EMAIL` and
 * `PARITY_PASSWORD` in its environment and must print one JSON object to
 * stdout: `{"accessToken": "...", "refreshToken": "...", "userId": "..."}`
 * (only `accessToken` is required). Anything it writes to stderr is surfaced
 * when it fails.
 *
 * This is the escape hatch for the coupling described above: it lets the rest
 * of the corpus be measured while `/auth/signup` is broken, unimplemented, or
 * deliberately excluded, instead of blanking 60 rows on one route's regression.
 */
async function seedViaCommand(baseUrl, vars, options) {
  const command = options.seedCandidateUser;
  try {
    const { stdout } = await execFileAsync('/bin/sh', ['-c', command], {
      env: {
        ...process.env,
        PARITY_BASE_URL: baseUrl,
        PARITY_EMAIL: vars.email,
        PARITY_PASSWORD: vars.password,
      },
      timeout: options.timeoutMs,
      maxBuffer: 1024 * 1024,
    });
    const parsed = JSON.parse(stdout.trim().split('\n').at(-1));
    if (typeof parsed?.accessToken !== 'string' || !parsed.accessToken) {
      return {
        ok: false,
        kind: 'seed-command-broken',
        error: `--seed-candidate-user printed no accessToken: ${stdout.slice(0, 200)}`,
      };
    }
    vars.accessToken = parsed.accessToken;
    if (parsed.refreshToken !== undefined) vars.refreshToken = parsed.refreshToken;
    if (parsed.userId !== undefined) vars.userId = parsed.userId;
    return { ok: true, seeded: true };
  } catch (error) {
    return {
      ok: false,
      kind: 'seed-command-broken',
      error: `--seed-candidate-user failed: ${error.message}${error.stderr ? ` — ${String(error.stderr).slice(0, 200)}` : ''}`,
    };
  }
}

/**
 * @returns {{ setupError: string|null, setupErrorKind?: string, steps: Array }}
 *          `setupErrorKind` is one of the provisionUser() classifications above;
 *          run.mjs maps it to the row state.
 */
export async function runScenario(baseUrl, scenario, side, options) {
  const { vars, volatileInputs } = seedVariables(scenario, side, options.runId);
  const pinned = new Map();
  const ctx = createMaskContext({ preserve: preserveSetFor(scenario), volatileInputs, pinned });
  const sideOptions = { ...options, side, budget: options.budget };

  if (scenario.user === 'fresh') {
    const provisioned = await provisionUser(baseUrl, vars, sideOptions);
    if (!provisioned.ok) {
      return { setupError: provisioned.error, setupErrorKind: provisioned.kind, steps: [] };
    }
    // The account's own id is an INPUT the harness holds for both sides, not a
    // relationship to be discovered, so give it a side-independent label. See
    // createMaskContext(). Without this, an early step that succeeds on one
    // side and 404s on the other makes every later `userId` echo disagree on
    // its label alone — measured on document-scans, whose opening PATCH /me is
    // owned by an unimplemented slice.
    if (typeof vars.userId === 'string' && vars.userId) pinned.set(vars.userId, '<ID:@user>');
  }

  const steps = [];
  for (const [index, step] of scenario.steps.entries()) {
    const { masks } = resolveMasks(options.baseMasks, step);
    // Masked values are labelled by WHERE they first appear, and the step
    // number is the first half of that location. See normalize.mjs.
    const stepKey = `s${index + 1}`;
    let entry;
    try {
      const outcome = step.poll
        ? await runPoll(baseUrl, step, vars, sideOptions)
        : { result: await send(baseUrl, planFor(step, vars, sideOptions), sideOptions), attempts: 1 };
      const observation = observe(outcome.result, {
        ctx,
        masks,
        literalPaths: step.literalPaths ?? [],
        compareHeaders: step.compareHeaders ?? [],
        stepKey,
      });
      entry = {
        name: step.name,
        op: step.op ?? null,
        request: { method: String(step.method).toUpperCase(), path: String(step.path) },
        pollAttempts: outcome.attempts,
        pollSettled: step.poll ? outcome.settled : undefined,
        observation,
      };
      if (observation.reachable) captureInto(vars, step, outcome.result, entry);
    } catch (error) {
      entry = {
        name: step.name,
        op: step.op ?? null,
        request: { method: String(step.method).toUpperCase(), path: String(step.path) },
        observation: { reachable: false, error: `harness error: ${error.message}` },
        harnessError: error instanceof MissingVariableError ? error.message : `${error.name}: ${error.message}`,
      };
    }
    steps.push(entry);
  }

  return { setupError: null, steps };
}

function captureInto(vars, step, result, entry) {
  if (!step.capture) return;
  let parsed;
  try {
    parsed = JSON.parse(result.bytes.toString('utf8'));
  } catch {
    entry.captureError = 'response was not JSON, nothing captured';
    return;
  }
  const missing = [];
  for (const [name, expression] of Object.entries(step.capture)) {
    const value = readPath(parsed, expression);
    if (value === undefined) missing.push(`${name} <- ${expression}`);
    vars[name] = value;
  }
  if (missing.length) entry.captureError = `capture missed: ${missing.join(', ')}`;
}
