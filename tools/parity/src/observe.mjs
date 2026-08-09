// Turns a raw HTTP result into the normalized observation the differ compares.
import crypto from 'node:crypto';

import { normalizeBody, normalizeText } from './normalize.mjs';

function isJson(contentType) {
  return typeof contentType === 'string' && contentType.toLowerCase().includes('json');
}

function isText(contentType) {
  if (typeof contentType !== 'string') return false;
  const lower = contentType.toLowerCase();
  return lower.startsWith('text/') || lower.includes('event-stream') || lower.includes('xml');
}

export function observe(result, { ctx, masks, literalPaths = [], compareHeaders = [] }) {
  if (!result.ok) {
    return { reachable: false, error: result.error, durationMs: result.durationMs };
  }

  const extraHeaders = {};
  for (const name of compareHeaders) extraHeaders[name.toLowerCase()] = result.headers[name.toLowerCase()] ?? null;

  const observation = {
    reachable: true,
    status: result.status,
    contentType: result.contentType,
    extraHeaders,
    bytes: result.bytes.length,
    durationMs: result.durationMs,
    rateLimit: result.rateLimit,
    rawJson: null,
    pretty: false,
    keyOrder: null,
    body: null,
    bodyKind: 'empty',
  };

  if (result.bytes.length === 0) {
    observation.body = null;
    observation.bodyKind = 'empty';
    return observation;
  }

  const text = result.bytes.toString('utf8');

  if (isJson(result.contentType)) {
    try {
      const parsed = JSON.parse(text);
      observation.rawJson = parsed;
      observation.bodyKind = 'json';
      observation.pretty = /\n\s+/.test(text);
      observation.keyOrder =
        parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? Object.keys(parsed).join(',') : null;
      observation.body = normalizeBody(parsed, { ctx, masks, literalPaths });
      return observation;
    } catch {
      // A body that claims JSON and is not JSON is itself a difference; fall
      // through to the text branch so the two sides can still be compared.
      observation.bodyKind = 'json-unparseable';
    }
  }

  if (isText(result.contentType) || observation.bodyKind === 'json-unparseable') {
    if (observation.bodyKind !== 'json-unparseable') observation.bodyKind = 'text';
    observation.body = normalizeText(text, { ctx, masks });
    return observation;
  }

  observation.bodyKind = 'binary';
  observation.body = {
    __binary: {
      bytes: result.bytes.length,
      sha256: crypto.createHash('sha256').update(result.bytes).digest('hex'),
    },
  };
  return observation;
}
