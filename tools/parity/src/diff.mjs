// Structural differ. Reports the JSON path of each disagreement rather than
// dumping two blobs and leaving the reader to spot the delta.

import { diffHeaders } from './headers.mjs';

const MAX_DIFFS = 25;

function kindOf(value) {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value;
}

function preview(value) {
  if (typeof value === 'string') return JSON.stringify(value.length > 120 ? value.slice(0, 117) + '...' : value);
  if (value === undefined) return '(absent)';
  const text = JSON.stringify(value);
  if (text === undefined) return String(value);
  return text.length > 160 ? text.slice(0, 157) + '...' : text;
}

function walk(a, b, jsonPath, out) {
  if (out.length >= MAX_DIFFS) return;

  const ka = kindOf(a);
  const kb = kindOf(b);

  if (a === undefined || b === undefined) {
    out.push({
      path: jsonPath,
      kind: a === undefined ? 'missing-on-reference' : 'missing-on-candidate',
      reference: preview(a),
      candidate: preview(b),
    });
    return;
  }

  if (ka !== kb) {
    out.push({ path: jsonPath, kind: 'type', reference: `${ka} ${preview(a)}`, candidate: `${kb} ${preview(b)}` });
    return;
  }

  if (ka === 'array') {
    if (a.length !== b.length) {
      out.push({ path: jsonPath, kind: 'array-length', reference: String(a.length), candidate: String(b.length) });
    }
    const n = Math.min(a.length, b.length);
    for (let i = 0; i < n; i += 1) walk(a[i], b[i], `${jsonPath}[${i}]`, out);
    return;
  }

  if (ka === 'object') {
    const keys = [...new Set([...Object.keys(a), ...Object.keys(b)])].sort();
    for (const key of keys) {
      const childPath = `${jsonPath}.${key}`;
      if (!(key in a)) {
        out.push({ path: childPath, kind: 'extra-key-on-candidate', reference: '(absent)', candidate: preview(b[key]) });
        continue;
      }
      if (!(key in b)) {
        out.push({ path: childPath, kind: 'missing-key-on-candidate', reference: preview(a[key]), candidate: '(absent)' });
        continue;
      }
      walk(a[key], b[key], childPath, out);
    }
    return;
  }

  if (a !== b) {
    out.push({ path: jsonPath, kind: 'value', reference: preview(a), candidate: preview(b) });
  }
}

function errorEnvelope(raw) {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) return null;
  const err = raw.error;
  if (err === null || typeof err !== 'object' || Array.isArray(err)) return null;
  return err;
}

/**
 * Compare one step's two observations.
 *
 * @param {object} reference normalized observation from the reference server
 * @param {object} candidate normalized observation from the port
 * @returns {{diffs: Array, notes: Array, headerPolicy: Array}} `headerPolicy`
 *          records every header divergence the policy chose NOT to fail, so the
 *          report can print it rather than swallow it.
 */
/**
 * `options.statusOnly` drops the BODY and CONTENT-TYPE comparison. It does not
 * drop anything else: status, the full header set, and "is this an error
 * envelope" are all still compared.
 *
 * This exists for ONE declared exception and should stay that way: Express's
 * fall-through 404 body. We deliberately do not reproduce it — it interpolates the
 * attacker-controlled request path, so porting it creates reflected XSS on every
 * unknown route of an API that also serves authenticated JSON. Nothing parses a 404
 * body, so the status is the only difference a client can observe. See
 * `docs/KERNEL.md` §2.2.1 and `docs/RESUME.md` for the arbitration.
 *
 * The envelope check is what stops the exception from being a hole. The scenario
 * comment on those two steps says a JSON error envelope there "would be a
 * DIFFERENT kind of wrong" — so assert exactly that and nothing more. Dropping
 * the whole body comparison silently un-armed the negative control's two
 * HTML-404 defects; this is the narrowest assertion that arms them again without
 * requiring the XSS-bearing body.
 *
 * Reach for this only when a diff is unfixable AND unobservable by a client.
 * Anything else is a real failure and belongs red.
 */
export function diffObservations(reference, candidate, options = {}) {
  const diffs = [];
  const notes = [];
  const statusOnly = options.statusOnly === true;

  if (reference.status !== candidate.status) {
    diffs.push({
      path: 'status',
      kind: 'status',
      reference: String(reference.status),
      candidate: String(candidate.status),
    });
  }

  // Headers are diffed on the UNION of both sides under the policy in
  // headers.mjs, so a header only the CANDIDATE emits is visible.
  const headerComparison = diffHeaders(
    reference.headers ?? {},
    candidate.headers ?? {},
    reference.forcedHeaders ?? candidate.forcedHeaders ?? [],
  );
  diffs.push(...headerComparison.diffs);
  const headerPolicy = headerComparison.policy;

  const refErr = errorEnvelope(reference.rawJson);
  const candErr = errorEnvelope(candidate.rawJson);

  if (statusOnly) {
    notes.push(
      'statusOnly: body and content-type deliberately not compared (declared exception — see docs/KERNEL.md §2.2.1). ' +
        'Status, headers and "is this a JSON error envelope" ARE still compared.',
    );
    if (Boolean(refErr) !== Boolean(candErr)) {
      diffs.push({
        path: '$.error',
        kind: 'error-envelope',
        reference: refErr ? 'error envelope' : 'no error envelope',
        candidate: candErr ? 'error envelope' : 'no error envelope',
      });
    }
    return { diffs: diffs.slice(0, MAX_DIFFS), truncated: diffs.length >= MAX_DIFFS, notes, headerPolicy };
  }

  const refCt = reference.contentType ?? null;
  const candCt = candidate.contentType ?? null;
  if (refCt !== candCt) {
    diffs.push({ path: 'header.content-type', kind: 'header', reference: preview(refCt), candidate: preview(candCt) });
  }

  // The error envelope is compared on the RAW text: literal `code` and
  // `message` are part of this contract, punctuation and all.
  if (refErr || candErr) {
    if (!refErr || !candErr) {
      diffs.push({
        path: '$.error',
        kind: 'error-envelope',
        reference: refErr ? 'error envelope' : 'no error envelope',
        candidate: candErr ? 'error envelope' : 'no error envelope',
      });
    } else {
      for (const field of ['code', 'message']) {
        if (refErr[field] !== candErr[field]) {
          diffs.push({
            path: `$.error.${field}`,
            kind: 'error-literal',
            reference: preview(refErr[field]),
            candidate: preview(candErr[field]),
          });
        }
      }
    }
  }

  // The structural walk would rediscover error.code / error.message that the
  // literal check above already reported; keep the literal version, which
  // compares the unmasked text.
  const alreadyReported = new Set(diffs.filter((d) => d.kind === 'error-literal').map((d) => d.path));
  const structural = [];
  walk(reference.body, candidate.body, '$', structural);
  diffs.push(...structural.filter((d) => !alreadyReported.has(d.path)));

  if (reference.keyOrder && candidate.keyOrder && reference.keyOrder !== candidate.keyOrder) {
    notes.push('JSON key order differs (informational; object key order is not part of the contract)');
  }
  if (reference.pretty !== candidate.pretty) {
    notes.push(
      `response body whitespace differs: reference ${reference.pretty ? 'pretty-printed' : 'compact'}, candidate ${candidate.pretty ? 'pretty-printed' : 'compact'}`,
    );
  }

  const truncated = diffs.length >= MAX_DIFFS;
  return { diffs: diffs.slice(0, MAX_DIFFS), truncated, notes, headerPolicy };
}
