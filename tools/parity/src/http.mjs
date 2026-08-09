// Thin fetch wrapper. Two behaviours matter for parity:
//   * redirect: 'manual' — the Google OAuth callback 302s to a `kitto://`
//     deep link, and an auto-following client throws "URL scheme must be a
//     HTTP(S) scheme" instead of letting us compare the Location header.
//   * bodies are read as bytes, so binary and text responses are both exact.

export const UNREACHABLE = 'unreachable';

const RATE_LIMIT_HEADERS = ['ratelimit-limit', 'ratelimit-remaining', 'ratelimit-reset', 'retry-after'];

export async function request(baseUrl, { method, path, headers = {}, body, timeoutMs = 20000 }) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  const startedAt = Date.now();
  try {
    const response = await fetch(baseUrl + path, {
      method,
      headers,
      body,
      redirect: 'manual',
      signal: controller.signal,
    });
    const bytes = Buffer.from(await response.arrayBuffer());
    const headerMap = {};
    for (const [name, value] of response.headers) headerMap[name.toLowerCase()] = value;
    const rateLimit = {};
    for (const name of RATE_LIMIT_HEADERS) {
      if (headerMap[name] !== undefined) rateLimit[name] = headerMap[name];
    }
    return {
      ok: true,
      status: response.status,
      headers: headerMap,
      contentType: headerMap['content-type'] ?? null,
      bytes,
      rateLimit,
      durationMs: Date.now() - startedAt,
    };
  } catch (error) {
    return {
      ok: false,
      reason: UNREACHABLE,
      error: describe(error),
      durationMs: Date.now() - startedAt,
    };
  } finally {
    clearTimeout(timer);
  }
}

function describe(error) {
  if (error?.name === 'AbortError') return 'timeout';
  const cause = error?.cause;
  const parts = [error?.message ?? String(error)];
  if (cause?.code) parts.push(cause.code);
  else if (cause?.message) parts.push(cause.message);
  return parts.join(': ');
}

/** Cheap liveness probe used by the preflight. */
export async function probe(baseUrl, timeoutMs = 4000) {
  const result = await request(baseUrl, { method: 'GET', path: '/health', timeoutMs });
  return result.ok;
}
