#!/usr/bin/env node
// Negative control for the harness itself.
//
// `--self-test` proves the harness does not report false FAILures. This proves
// the opposite direction: that it does not report false PASSes. It serves the
// `framework` scenario as a CORRECT port would — same status codes, same
// bodies, same security headers — except for exactly one planted defect per
// response.
//
//   # one command: start, run the harness, assert every defect was caught
//   node tools/parity/selfcheck/bad-candidate.mjs --verify
//
//   # or the manual flow the README has always documented
//   node tools/parity/selfcheck/bad-candidate.mjs &
//   node tools/parity/run.mjs --only framework --candidate http://localhost:5199   # must exit 1
//   kill %1
//
// SIX planted defects, one per step of the `framework` scenario:
//
//   1 GET  /health   `uptime` typed as a string, plus an extra `version` property
//   2 GET  /nope     a JSON error envelope where Express serves an HTML page
//   3 PUT  /health   the same
//   4 GET  /me/tasks 401 with the right code but the wrong literal message
//   5 GET  /auth/me  (lowercase `bearer`) invalid_token where the reference says missing_token
//   6 GET  /auth/me  (correct `Bearer`) a byte-correct body with a LEAKED
//                    `X-Powered-By` header the reference does not emit
//
// Defect 6 is the one that matters most here: everything about that response is
// right except a header only the candidate sends. The differ used to iterate
// the REFERENCE's headers alone, so it could not see it and the step passed.
// It is now diffed on the union of both header sets — see src/headers.mjs.
//
// Defects 2 and 3 were also silently un-armed for a while: the two 404 steps are
// `statusOnly` (the harness deliberately does not require Express's XSS-bearing
// HTML body) and `statusOnly` used to drop the whole body comparison, so a JSON
// envelope there went unnoticed. `statusOnly` now still asserts "neither side is
// a JSON error envelope", which is exactly the property the scenario comment
// claims to be protecting.
//
// EVERY OTHER HEADER MUST BE RIGHT. If this server omitted the helmet set, every
// step would fail on the missing headers and "all six were caught" would be
// true for the wrong reason.

import http from 'node:http';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HARNESS = path.join(HERE, '..', 'run.mjs');

const argv = process.argv.slice(2);
const flag = (name, fallback) => {
  const at = argv.indexOf(name);
  return at === -1 ? fallback : argv[at + 1];
};
const PORT = Number(flag('--port', process.env.PORT ?? 5199));
const REFERENCE = flag('--reference', 'http://localhost:4200');
const VERIFY = argv.includes('--verify');

// helmet 8.1.0 defaults, exactly as the reference emits them, plus the CORS
// pair a no-Origin request gets. Measured live against :4200.
const HELMET = {
  'Content-Security-Policy':
    "default-src 'self';base-uri 'self';font-src 'self' https: data:;form-action 'self';" +
    "frame-ancestors 'self';img-src 'self' data:;object-src 'none';script-src 'self';" +
    "script-src-attr 'none';style-src 'self' https: 'unsafe-inline';upgrade-insecure-requests",
  'Cross-Origin-Opener-Policy': 'same-origin',
  'Cross-Origin-Resource-Policy': 'same-origin',
  'Origin-Agent-Cluster': '?1',
  'Referrer-Policy': 'no-referrer',
  'Strict-Transport-Security': 'max-age=31536000; includeSubDomains',
  'X-Content-Type-Options': 'nosniff',
  'X-DNS-Prefetch-Control': 'off',
  'X-Download-Options': 'noopen',
  'X-Frame-Options': 'SAMEORIGIN',
  'X-Permitted-Cross-Domain-Policies': 'none',
  'X-XSS-Protection': '0',
  Vary: 'Origin',
  'Access-Control-Allow-Credentials': 'true',
};

// finalhandler serves its HTML 404 under its own, tighter CSP.
const FALLTHROUGH_404 = { ...HELMET, 'Content-Security-Policy': "default-src 'none'" };

function createServer() {
  return http.createServer((req, res) => {
    const requestPath = new URL(req.url, 'http://placeholder').pathname;
    const authorization = req.headers.authorization ?? '';
    const json = (status, body, extra = {}, base = HELMET) => {
      res.writeHead(status, { ...base, ...extra, 'Content-Type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify(body));
    };

    if (requestPath === '/health' && req.method === 'GET') {
      // DEFECT 1: uptime is a string, and `version` is not in the contract.
      json(200, { status: 'ok', db: 'connected', uptime: '123.4', version: '1.0.0' });
      return;
    }
    if (requestPath === '/nope' || requestPath === '/health') {
      // DEFECTS 2 and 3: the JSON envelope where Express serves HTML.
      json(404, { error: { code: 'not_found', message: 'Not found' } }, {}, FALLTHROUGH_404);
      return;
    }
    if (requestPath === '/me/tasks') {
      // DEFECT 4: right code, drifted literal message.
      json(401, { error: { code: 'missing_token', message: 'Missing token.' } });
      return;
    }
    if (requestPath === '/auth/me') {
      if (authorization.startsWith('Bearer ')) {
        // DEFECT 6: the body and every other header are CORRECT. The only
        // divergence is a header the reference does not emit at all — helmet
        // strips X-Powered-By on the reference side.
        json(
          401,
          { error: { code: 'invalid_token', message: 'Invalid or expired access token' } },
          { 'X-Powered-By': 'bad-candidate' },
        );
        return;
      }
      // DEFECT 5: a lowercase `bearer` never reaches token verification on the
      // reference, so it is missing_token there, not invalid_token.
      json(401, { error: { code: 'invalid_token', message: 'Invalid or expired access token' } });
      return;
    }
    json(500, { error: { code: 'internal_error', message: 'Internal server error' } });
  });
}

// ---------------------------------------------------------------------------
// --verify: run the harness against this server and assert every planted defect
// was detected, at the step and on the diff path it was planted on.
// ---------------------------------------------------------------------------

const EXPECTED = [
  { step: 'health', paths: ['$.uptime', '$.version'], what: 'wrong type + extra property' },
  { step: 'unknown-route-is-html-404', paths: ['$.error'], what: 'JSON envelope where Express serves HTML' },
  { step: 'unknown-method-on-known-path-is-html-404', paths: ['$.error'], what: 'same, on a method mismatch' },
  { step: 'unauthenticated-is-missing-token', paths: ['$.error.message'], what: 'drifted literal message' },
  { step: 'lowercase-bearer-is-missing-token', paths: ['$.error.code'], what: 'wrong error code' },
  { step: 'garbage-bearer-is-invalid-token', paths: ['header.x-powered-by'], what: 'CANDIDATE-ONLY header' },
];

async function verify(server) {
  const reportPath = path.join(HERE, '..', 'out', 'negative-control.json');
  const args = [
    HARNESS,
    '--only', 'framework',
    '--reference', REFERENCE,
    '--candidate', `http://localhost:${PORT}`,
    '--no-auth-sweep',
    '--no-colour',
    '--out', reportPath,
  ];
  const code = await new Promise((resolve) => {
    const child = spawn(process.execPath, args, { stdio: ['ignore', 'ignore', 'inherit'] });
    child.on('close', resolve);
  });

  const report = JSON.parse(await (await import('node:fs/promises')).readFile(reportPath, 'utf8'));
  const failing = new Map(
    report.steps.filter((s) => s.state === 'FAIL' || s.state === 'ERROR').map((s) => [s.name, s]),
  );

  const lines = [];
  let missed = 0;
  for (const expectation of EXPECTED) {
    const step = failing.get(expectation.step);
    const seen = new Set((step?.diffs ?? []).map((d) => d.path));
    const absent = expectation.paths.filter((p) => !seen.has(p));
    const ok = Boolean(step) && absent.length === 0;
    if (!ok) missed += 1;
    lines.push(
      `  ${ok ? 'CAUGHT ' : 'MISSED '} ${expectation.step}\n` +
        `            ${expectation.what}\n` +
        `            expected diff at ${expectation.paths.join(', ')}` +
        (ok ? '' : `  -- NOT REPORTED: ${absent.join(', ') || 'the step did not fail at all'}`),
    );
  }

  const unexpected = [...failing.keys()].filter((name) => !EXPECTED.some((e) => e.step === name));

  process.stdout.write(`\nNEGATIVE CONTROL — ${EXPECTED.length} planted defects\n\n${lines.join('\n')}\n`);
  if (unexpected.length) {
    process.stdout.write(
      `\n  ${unexpected.length} step(s) failed that were NOT planted: ${unexpected.join(', ')}\n` +
        '  Either this server drifted from the reference, or the harness gained a false FAILure.\n',
    );
  }
  if (code === 0) {
    process.stdout.write('\n  run.mjs exited 0 — a harness that finds planted defects must exit non-zero.\n');
  }

  const good = missed === 0 && unexpected.length === 0 && code !== 0;
  process.stdout.write(
    good
      ? `\nNEGATIVE CONTROL PASSED: all ${EXPECTED.length} defects caught, nothing else failed, run.mjs exited ${code}.\n`
      : `\nNEGATIVE CONTROL FAILED: ${missed} defect(s) missed, ${unexpected.length} unexpected failure(s).\n`,
  );
  server.close();
  return good ? 0 : 1;
}

const server = createServer();
server.listen(PORT, async () => {
  if (!VERIFY) {
    process.stdout.write(`parity negative control listening on ${PORT}\n`);
    return;
  }
  process.exit(await verify(server));
});
