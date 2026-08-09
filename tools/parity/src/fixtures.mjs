// Deterministic byte payloads for the raw-body upload routes. Generated
// rather than checked in so the corpus stays text-only and both sides get
// byte-identical input.

const MINIMAL_PDF = [
  '%PDF-1.4',
  '1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj',
  '2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj',
  '3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj',
  'trailer<</Root 1 0 R>>',
  '%%EOF',
  '',
].join('\n');

const builders = {
  'pdf.min': () => Buffer.from(MINIMAL_PDF, 'utf8'),
  'audio.min': () => Buffer.from('kitto-parity-fake-audio', 'utf8'),
  empty: () => Buffer.alloc(0),
};

/**
 * `fixture` forms accepted in a scenario step:
 *   rawFixture: pdf.min
 *   rawFixture: { repeat: 'x', bytes: 307200 }   // oversized-body probes
 *   rawText: '{"a":'                              // malformed JSON probes
 */
export function buildRawBody(step) {
  if (step.rawText !== undefined) return Buffer.from(String(step.rawText), 'utf8');
  const spec = step.rawFixture;
  if (spec === undefined) return undefined;
  if (typeof spec === 'string') {
    const build = builders[spec];
    if (!build) throw new Error(`unknown rawFixture "${spec}" (have: ${Object.keys(builders).join(', ')})`);
    return build();
  }
  if (spec && typeof spec === 'object' && spec.bytes !== undefined) {
    const fill = String(spec.repeat ?? 'x');
    const bytes = Number(spec.bytes);
    if (!Number.isInteger(bytes) || bytes < 0) throw new Error('rawFixture.bytes must be a non-negative integer');
    return Buffer.from(fill.repeat(Math.ceil(bytes / fill.length)).slice(0, bytes), 'utf8');
  }
  throw new Error('rawFixture must be a name or { bytes, repeat? }');
}

/**
 * A JSON body that is intentionally huge — kept as a builder so the scenario
 * file does not have to embed 300kb of literal text.
 */
export function buildJsonPadding(step) {
  const spec = step.jsonPadding;
  if (!spec) return undefined;
  const { field = 'displayName', bytes = 300 * 1024 } = spec;
  return { [field]: 'x'.repeat(Number(bytes)) };
}
