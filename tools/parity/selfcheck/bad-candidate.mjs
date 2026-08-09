#!/usr/bin/env node
// Negative control for the harness itself.
//
// `--self-test` proves the harness does not report false FAILures. This proves
// the opposite direction: that it does not report false PASSes. It serves the
// `framework` scenario with one planted defect per response; running the
// harness against it must produce exactly five failing steps and exit 1.
//
//   node tools/parity/selfcheck/bad-candidate.mjs &
//   node tools/parity/run.mjs --only framework --candidate http://localhost:5199
//   kill %1
//
// Expected findings:
//   /health   uptime typed as a string, plus an extra `version` property
//   /nope     the JSON error envelope where Express serves an HTML page
//   PUT /health  same
//   /me/tasks 401 with the right code but the wrong literal message
//   /auth/me  invalid_token where the reference says missing_token

import http from 'node:http';

const PORT = Number(process.env.PORT ?? 5199);

const server = http.createServer((req, res) => {
  const path = new URL(req.url, 'http://placeholder').pathname;
  const json = (status, body) => {
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(body));
  };

  if (path === '/health' && req.method === 'GET') {
    json(200, { status: 'ok', db: 'connected', uptime: '123.4', version: '1.0.0' });
    return;
  }
  if (path === '/nope' || path === '/health') {
    json(404, { error: { code: 'not_found', message: 'Not found' } });
    return;
  }
  if (path === '/me/tasks') {
    json(401, { error: { code: 'missing_token', message: 'Missing token.' } });
    return;
  }
  if (path === '/auth/me') {
    json(401, { error: { code: 'invalid_token', message: 'Invalid or expired access token' } });
    return;
  }
  json(500, { error: { code: 'internal_error', message: 'Internal server error' } });
});

server.listen(PORT, () => process.stdout.write(`parity negative control listening on ${PORT}\n`));
