// The reference server enforces per-IP auth rate limits unless it is started
// with NODE_ENV=test. Two facts make this the harness's sharpest operational
// edge:
//   * the bucket is keyed on the CLIENT ip, so both sides of a --self-test run
//     drain the SAME bucket;
//   * a fresh user per scenario costs one authLimiter slot each, and the
//     budget is only 20 per 15 minutes.
// So we budget explicitly and say so out loud, rather than letting a run turn
// mysteriously red halfway through.

export const AUTH_LIMITED = new Set([
  'authSignup',
  'authSignin',
  'authRefresh',
  'authVerifyEmailToken',
  'authMagicConsume',
]);

export const STRICT_LIMITED = new Set([
  'authForgotPassword',
  'authResetPassword',
  'authChangePassword',
  'authVerifyEmailSendCode',
  'authVerifyEmailConfirmCode',
  'authMagicLinkRequest',
  'authChangeEmailRequest',
  'authChangeEmailConfirm',
]);

export const AUTH_WINDOW = { max: 20, windowSeconds: 900 };
export const STRICT_WINDOW = { max: 5, windowSeconds: 3600 };

export function createBudget() {
  return { observed: new Map(), waits: [], hits429: 0 };
}

/** Record whatever the server told us about its own counters. */
export function noteRateLimit(budget, side, result) {
  const rl = result?.rateLimit;
  if (!rl || rl['ratelimit-limit'] === undefined) return;
  const policy = `${rl['ratelimit-limit']}`;
  budget.observed.set(`${side}:${policy}`, {
    side,
    limit: Number(rl['ratelimit-limit']),
    remaining: Number(rl['ratelimit-remaining']),
    resetSeconds: Number(rl['ratelimit-reset']),
    at: Date.now(),
  });
}

/**
 * Estimated authLimiter / strictAuthLimiter cost of a planned run, so the
 * preflight can warn before burning the budget rather than after.
 */
export function estimateCost(scenarios, sides) {
  let auth = 0;
  let strict = 0;
  for (const scenario of scenarios) {
    if (scenario.user === 'fresh') auth += 1; // provisioning signup
    for (const step of scenario.steps) {
      if (!step?.op) continue;
      if (AUTH_LIMITED.has(step.op)) auth += 1;
      // Every strictAuthLimiter route is declared `requireAuth,
      // strictAuthLimiter`, and authentication runs FIRST — so a deliberately
      // unauthenticated probe is rejected 401 without consuming a slot. The
      // generated auth sweep is entirely made of those, and must not be
      // charged for them.
      if (STRICT_LIMITED.has(step.op) && step.auth !== false) strict += 1;
    }
  }
  return { authPerSide: auth, strictPerSide: strict, auth: auth * sides, strict: strict * sides, sides };
}

export function isRateLimited(observation) {
  return observation?.reachable === true && observation.status === 429;
}

export function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * How long to wait before retrying a 429, or null when waiting is disabled or
 * the wait would exceed the caller's patience.
 */
export function retryDelayMs(result, maxWaitMs) {
  if (maxWaitMs <= 0) return null;
  const retryAfter = Number(result?.rateLimit?.['retry-after'] ?? result?.rateLimit?.['ratelimit-reset'] ?? NaN);
  if (!Number.isFinite(retryAfter)) return null;
  const ms = Math.max(1000, (retryAfter + 1) * 1000);
  return ms <= maxWaitMs ? ms : null;
}
