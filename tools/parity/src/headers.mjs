// RESPONSE-HEADER COMPARISON POLICY.
//
// The differ compares the UNION of both sides' response headers. Iterating the
// reference's headers alone — which is what it used to do — makes every
// candidate-ONLY header invisible: a leaked `Server` / `X-Powered-By` /
// `X-AspNet-Version`, a debug or trace header, a duplicated CORS header, a
// stray `Set-Cookie`. Each of those is a real, client-observable divergence and
// each would have passed silently.
//
// Comparing every header naively is not usable either: a handful are pure
// transport or pure clock, and a handful more legitimately differ between two
// servers that are dialled seconds apart. So there are exactly four buckets,
// and every entry in them is named and justified here rather than buried in a
// conditional somewhere.
//
//   1. TRANSPORT_HEADERS      never compared
//   2. VOLATILE_HEADER_VALUES presence compared, value normalised
//   3. RUN_MODE_OPTIONAL      candidate-only presence tolerated, and NOTED
//   4. DECLARED_EXCEPTIONS    a known, open divergence — reported, not failed
//
// A step's `compareHeaders:` list overrides all four: those headers are always
// compared strictly, on their raw values.

/**
 * 1. TRANSPORT / CLOCK — never compared.
 *
 * These say nothing about whether the port is response-compatible.
 *
 *   date                Wall clock. Always differs.
 *   content-length      Body framing. The body itself is compared structurally,
 *   transfer-encoding   and Node sets a length where Kestrel chunks. Both are
 *                       valid HTTP/1.1 framings of the same response, and no
 *                       client behaves differently.
 *   connection          Hop-by-hop.
 *   keep-alive          Hop-by-hop.
 *   alt-svc             Hop-by-hop advertisement.
 *   content-type        Compared separately by the differ, so that the
 *                       `statusOnly` exception can drop it on its own.
 */
export const TRANSPORT_HEADERS = new Set([
  'date',
  'content-length',
  'transfer-encoding',
  'connection',
  'keep-alive',
  'alt-svc',
  'content-type',
]);

/**
 * 2. VOLATILE VALUES — presence IS compared, the value is replaced.
 *
 * Absence on one side is still a difference; only the literal value is
 * unstable. Keeping presence checkable is the whole point: an `ETag` that
 * disappears is a real change, an `ETag` whose hex differs is not.
 *
 *   etag                 Weak ETag over the body. The two sides mint different
 *                        ids, emails and clock readings, so the bodies — and
 *                        therefore the hashes — legitimately differ even when
 *                        the responses are equivalent.
 *   last-modified        Clock-derived, second granularity.
 *   ratelimit-remaining  Per-side counters. Each server keeps its own bucket
 *   ratelimit-reset      and the two sides are dialled seconds apart, so these
 *   retry-after          count down independently. `ratelimit-limit` and
 *                        `ratelimit-policy` are NOT here: they are static
 *                        configuration and a wrong limit is a real defect.
 */
export const VOLATILE_HEADER_VALUES = new Map([
  ['etag', '<ETAG>'],
  ['last-modified', '<HTTP-DATE>'],
  ['ratelimit-remaining', '<COUNTER>'],
  ['ratelimit-reset', '<COUNTER>'],
  ['retry-after', '<COUNTER>'],
]);

/**
 * 3. RUN-MODE ALLOWLIST — candidate-only presence is tolerated.
 *
 * ONLY the direction "candidate has it, reference does not" is allowed, and it
 * is reported in its own section of the run. The reverse — the reference emits
 * it and the candidate does not — stays a FAIL, because a MISSING RateLimit
 * header on a route that should carry one is exactly the defect this harness
 * is here to catch. Values are compared whenever both sides have the header.
 *
 *   RateLimit-Policy     express-rate-limit 7.5.1 runs with
 *   RateLimit-Limit      `standardHeaders: true`, so every response a limiter
 *   RateLimit-Remaining  touches carries these four, and a 429 adds
 *   RateLimit-Reset      Retry-After. But the reference is normally run at
 *   Retry-After          :4200 with NODE_ENV=test, where rate limiting is
 *                        DISABLED ENTIRELY (contract preamble,
 *                        docs/contract/paths.auth.yaml), so it emits none of
 *                        them. The same reference at :4100 in development mode
 *                        emits all four with the same values the candidate
 *                        does (measured: `RateLimit-Policy: 20;w=900`). So a
 *                        candidate-only RateLimit header is a property of how
 *                        the reference was started, not of the port.
 *
 * Re-run against :4100 to compare these for real; this allowlist exists so a
 * :4200 run does not report a false failure, not so the headers stop mattering.
 */
export const RUN_MODE_OPTIONAL = [/^ratelimit-/, /^retry-after$/];

/**
 * 4. DECLARED EXCEPTIONS — a known, open divergence.
 *
 * Reported in its own always-printed section with the number of steps it
 * touched, never silently dropped, and never counted as a pass of the header
 * it names. This is the same mechanism as the `statusOnly` body exception in
 * `diff.mjs`: a divergence that is real, already understood, tracked
 * elsewhere, and whose per-row repetition would bury every other signal.
 *
 * The bar for adding an entry: the divergence must be systemic (it is one
 * defect appearing on most rows, not a per-route bug), already written down
 * somewhere an owner will see it, and recorded here with the exact condition
 * that removes it again.
 *
 * `direction`:
 *   'reference-only' — reference emits it, candidate does not.
 *   'candidate-only' — candidate emits it, reference does not.
 * Any OTHER divergence in the same header (both sides present with different
 * values, or the opposite direction) is compared and failed normally.
 */
export const DECLARED_EXCEPTIONS = [
  {
    header: 'etag',
    direction: 'reference-only',
    reason:
      'Express emits a weak ETag on every res.json() body (frozen contract, ' +
      'docs/contract/paths.auth.yaml line 21: "with a weak ETag and Content-Length"). ' +
      'Kestrel emits none. This is ONE kernel-level gap that shows up on nearly every ' +
      'JSON row; failing all of them would bury every slice-level signal.',
    removeWhen:
      'the kernel emits a weak ETag on JSON responses — then delete this entry and the ' +
      'rows go green on their own. Until then this is an open contract violation, not a pass.',
  },
];

const HEADER_LABEL = new Map([
  ['reference-only', 'reference emits it, candidate does not'],
  ['candidate-only', 'candidate emits it, reference does not'],
]);

function matchesRunMode(name) {
  return RUN_MODE_OPTIONAL.some((pattern) => pattern.test(name));
}

function findException(name, direction) {
  return DECLARED_EXCEPTIONS.find((entry) => entry.header === name && entry.direction === direction) ?? null;
}

function normaliseValue(name, value) {
  if (value === undefined) return undefined;
  const replacement = VOLATILE_HEADER_VALUES.get(name);
  return replacement === undefined ? value : replacement;
}

function directionOf(a, b) {
  if (a !== undefined && b === undefined) return 'reference-only';
  if (a === undefined && b !== undefined) return 'candidate-only';
  return null;
}

/**
 * Diff the UNION of two header maps under the policy above.
 *
 * @param {Record<string,string>} referenceHeaders lowercased name -> value
 * @param {Record<string,string>} candidateHeaders lowercased name -> value
 * @param {string[]} forced header names the step asked to compare strictly
 * @returns {{diffs: Array, policy: Array}} `policy` records every allowance
 *          that was applied, so the report can show what was NOT failed.
 */
export function diffHeaders(referenceHeaders = {}, candidateHeaders = {}, forced = []) {
  const diffs = [];
  const policy = [];
  const forcedNames = new Set(forced.map((name) => name.toLowerCase()));
  const names = [...new Set([...Object.keys(referenceHeaders), ...Object.keys(candidateHeaders)])].sort();

  for (const name of names) {
    const rawA = referenceHeaders[name];
    const rawB = candidateHeaders[name];

    // An explicit `compareHeaders:` entry beats every allowance below.
    if (forcedNames.has(name)) {
      if (rawA !== rawB) {
        diffs.push({
          path: `header.${name}`,
          kind: 'header',
          reference: rawA === undefined ? '(absent)' : JSON.stringify(rawA),
          candidate: rawB === undefined ? '(absent)' : JSON.stringify(rawB),
        });
      }
      continue;
    }

    if (TRANSPORT_HEADERS.has(name)) continue;

    const direction = directionOf(rawA, rawB);

    if (direction === 'candidate-only' && matchesRunMode(name)) {
      policy.push({
        kind: 'run-mode',
        header: name,
        direction,
        value: rawB,
        reason:
          'run-mode allowlist: the reference was started with rate limiting off ' +
          '(NODE_ENV=test). Re-run against :4100 to compare this header for real.',
      });
      continue;
    }

    if (direction) {
      const exception = findException(name, direction);
      if (exception) {
        policy.push({
          kind: 'declared-exception',
          header: name,
          direction,
          reason: exception.reason,
          removeWhen: exception.removeWhen,
        });
        continue;
      }
    }

    const a = normaliseValue(name, rawA);
    const b = normaliseValue(name, rawB);
    if (a === b) continue;

    diffs.push({
      path: `header.${name}`,
      kind: 'header',
      reference: a === undefined ? '(absent)' : JSON.stringify(a),
      candidate: b === undefined ? '(absent)' : JSON.stringify(b),
    });
  }

  return { diffs, policy };
}

export function describeDirection(direction) {
  return HEADER_LABEL.get(direction) ?? direction;
}
