# Document Agent

`document-agent.json` — a Langflow flow that reads a scanned document and returns **structured
task candidates for review**. It does not write prose and it does not create tasks.

> **Status: authored, not executed.** Langflow is not installed on the machine this flow was
> written on, so it has never been imported or run. The JSON parses and every node/edge reference
> resolves; the deterministic logic inside it has been unit-tested outside Langflow. Extraction
> quality — whether the model actually reads a crumpled bill correctly — is entirely unverified.
> See [What is unverified](#what-is-unverified).

---

## Why candidates and not tasks

An earlier proposal had the Document Agent hand a prose prompt to the planning agent, which would
then create tasks directly. That was rejected.

`docs/features.md` names OCR misreading as this product's single biggest risk, with the canonical
example of `1/12` read as Jan 12 instead of Dec 1, and states plainly: *"one wrong reminder loses
user trust permanently."* The mitigation it names is a CitationChip and a confidence indicator on
every extracted value.

**Prose cannot carry a citation.** A sentence like "your insurance renews on January 12th" has
nowhere to hang a page reference and no way to say "I am not sure about this". A structured
candidate has both.

So the chain is:

```
document → structured candidates → existing review UI → user accepts → planning agent
                                                       → user discards → gone
```

The planning agent never sees a candidate the user did not accept.

---

## Flow structure

Four nodes, a straight chain. Node ids are the keys you use in `tweaks`.

| Node id | Type | Role |
|---|---|---|
| `CustomComponent-Dci01` | `DocumentScanInput` | Owns every host input; composes the prompt |
| `CustomComponent-Dvx01` | `DocumentVisionExtractor` | Calls the model gateway |
| `CustomComponent-Dch01` | `CandidateHardener` | Validates, calibrates, assigns keys, emits JSON |
| `ChatOutput-Dco01` | `ChatOutput` | Flow output (lifted verbatim from the planning-agent baseline) |

```
Document Scan Input ──Data──> Document Vision Extractor ──Data──> Candidate Hardener ──Message──> Chat Output
```

**The split is deliberate: the model proposes, the flow decides.** Everything the review UI depends
on — enum membership, date validity, citation integrity, confidence — is enforced in the hardener
in code, not trusted from the model. This mirrors the posture of the Node reference implementation
at `server/src/modules/ai/documentCore/`.

> ⚠️ **Langflow renames nodes on import.** `langflow/README.md` on the sibling branch records this
> the hard way. After importing, open the flow and confirm the ids above still match; if a run
> returns `Vertex … not found`, the ids changed and your `tweaks` keys need updating.

### Why the model call is a custom component

The gateway this product talks to uses its own multimodal request shape — `text` plus
`images[{format, data_base64}]` per message, **not** Anthropic's or OpenAI's native content blocks.
See `Life-Admin-Autopilot.DAL/Claude/Models/Internal/ClaudeMultimodalWireRequest.cs`, whose comment
says so explicitly. No stock Langflow model component emits that shape, so wrapping the call keeps
the flow honest about what it actually sends. To point this at a different provider, replace
`DocumentVisionExtractor` and leave the other three nodes alone.

---

## Configuration

**No credentials, no hosts, and no model names are stored in the flow JSON.** (The planning-agent
baseline embeds a live Mistral key and a JWT; this flow deliberately does not repeat that.)

| Setting | Field on `CustomComponent-Dvx01` | Resolution order |
|---|---|---|
| API key | `api_key` | Langflow global variable named in the field (default `DOCUMENT_AGENT_API_KEY`), then the `DOCUMENT_AGENT_API_KEY` env var |
| Base URL | `api_base_url` | Field value, then the `DOCUMENT_AGENT_BASE_URL` env var |
| Model id | `model_id` | Field value, then the `DOCUMENT_AGENT_MODEL_ID` env var |

The component **raises rather than falling back to a default host**. A misconfigured deployment
fails loudly instead of quietly calling somewhere unintended.

Endpoint paths (`chat_path`, `multimodal_path`) default to `/api/v1/student/chat` and
`/api/v1/student/multimodal-chat`, matching `ClaudeOptions`. They are editable advanced fields.

---

## Input contract

Invoke as the planning agent is invoked: `POST /api/v1/run/{flowId}?stream=false`, with everything
supplied through `tweaks` on `CustomComponent-Dci01`.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `document_text` | string | one of these two | Extracted / OCR'd text. Prefix pages with `Page N` markers so `sourcePage` can be grounded. |
| `page_images_json` | string | one of these two | JSON array, in page order: `[{"page": 1, "format": "png", "data_base64": "…"}]`. `page` defaults to array index + 1. |
| `page_count` | int | no | Real page count. Used to reject an invented `sourcePage`. `0` derives it from the images. |
| `current_date` | string | **yes** | Today, from the host, e.g. `2026-08-10`. |
| `timezone` | string | no | IANA name, e.g. `Africa/Cairo`. Default `UTC`. |
| `locale` | string | no | Language for candidate copy. Default `en`. `issuer` is never translated. |
| `document_id` | string | strongly recommended | The host's scanned-document id. Mixed into every candidate key. |
| `max_candidates` | int | no | Default and hard ceiling 15. |
| `extraction_instructions` | string | no | The prompt itself, editable on the canvas. |

`current_date` is required and has no clock fallback on purpose: a deadline like *"within 30 days
of this notice"* resolved against the server's day instead of the user's is exactly the class of
quiet off-by-one this agent exists to avoid.

Supplying neither text nor images is an error, not an empty result.

### Example

```jsonc
{
  "input_value": "",
  "input_type": "chat",
  "output_type": "chat",
  "tweaks": {
    "CustomComponent-Dci01": {
      "page_images_json": "[{\"page\":1,\"format\":\"png\",\"data_base64\":\"…\"}]",
      "page_count": 2,
      "current_date": "2026-08-10",
      "timezone": "Africa/Cairo",
      "locale": "en",
      "document_id": "507f1f77bcf86cd799439011"
    }
  }
}
```

---

## Output schema

The Chat Output message text is **bare JSON** — no markdown, no fences, no preamble. It parses
directly. (The baseline planning agent had to strip fences on the .NET side; that is a smell this
flow does not repeat. The hardener still strips fences defensively, but if that path ever fires in
production it means the prompt stopped holding and the prompt is what should be fixed.)

```jsonc
{
  "documentSummary":  string | null,   // one-to-two sentences, detail surfaces
  "documentType":     string | null,   // bill statement letter form receipt insurance
                                       // medical legal identity tax other
  "documentTitle":    string | null,   // short noun phrase, ≤60 chars
  "documentSubtitle": string | null,   // one line, ≤120 chars, most important fact first
  "issuer":           string | null,   // verbatim as printed, ≤80 chars
  "candidates": [
    {
      "key":        string,            // stable id; accept/discard and idempotent re-runs
      "title":      string,
      "domain":     "health" | "home" | "car" | "finance" | "family" | "pets",
      "priority":   "low" | "normal" | "high" | "urgent",
      "confidence": "high" | "medium" | "low",
      "dueAt":      string,            // optional, "2026-07-30T09:00:00.000Z"
      "notes":      string,            // optional
      "sourcePage": integer            // optional, 1-based — what the CitationChip points at
    }
  ]
}
```

Optional candidate fields are **omitted when absent**, never emitted as `null`. Document-level
fields are emitted as `null` when unknown. This matches `queries/documentScans.ts`.

`dueAt` is serialized as `YYYY-MM-DDTHH:MM:SS.000Z`, which satisfies the review endpoint's
`^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$` binder pattern.

### Keys

Assigned by the hardener, not the model, using the same formula as both existing workers:

```
sha256(f"{documentId}|{index}|{title.strip().lower() with runs of whitespace collapsed}")[:24]
```

`index` is the position in the final emitted array. This is byte-identical to
`makeCandidateKey` in `server/src/lib/documentScanWorker.ts` and to `ToDocument` in
`Life-Admin-Autopilot-Backend/Features/DocumentScans/DocumentScanWorker.cs`, so re-running
extraction on the same document produces the same keys and a re-run stays idempotent against
already-reviewed candidates.

**This matters more than it looks.** The review endpoint skips unknown keys silently
(`if (!held) continue`) — it does not error. If keys drift between runs, an accept becomes a
no-op with a `200 OK` and the user watches their tap do nothing.

If the host reassigns keys itself, pass `document_id` anyway and discard the flow's keys; do not
let the two formulas diverge.

---

## Confidence calibration

`confidence` is load-bearing. A `low` value renders as a warning and requires confirmation before
it can become a reminder. Getting this honest matters more than extracting more fields.

Two failures cost the same, and the rules below are tuned against both:

- **Under-warning** — presenting a guess as certain. This is the failure the field exists for.
- **Over-warning** — flagging everything. A review screen where every row is yellow teaches the
  user to tap through without reading, which destroys the warning just as thoroughly. `low` has to
  stay rare enough to still mean something.

### Layer 1 — the model's rubric

Confidence is set for the candidate as a whole, governed by its **weakest load-bearing value** (the
action, and the date if it has one).

| Bucket | Meaning |
|---|---|
| `high` | Every load-bearing value is printed on the page in a form with exactly one reading, and the model could point at the characters. |
| `medium` | Read correctly, but something took judgment: the date was derived rather than printed, the action is implied rather than stated, a figure is legible but its label is not, or the page could not be identified. |
| `low` | Any one of: more than one plausible reading; glyphs unclear, cut off, skewed, glared, creased or handwritten; a value supplied that is not printed anywhere; visibly garbled OCR around the value. |

The model also returns three **evidence fields** — `dueAtRaw` (the date exactly as printed),
`dueAtSource` (`printed` / `derived` / `inferred` / `none`), and `dueAtOrderEvidence` (what on the
page settled a digits-only date's day/month order). These are read by the hardener and **stripped
before anything reaches the user**. They exist so the pipeline can check the model's reading
instead of taking its word for it.

### Layer 2 — mechanical caps the model cannot talk past

Asking a model to be well-calibrated is a hope. These are enforced in `CandidateHardener`:

| Trigger | Effect |
|---|---|
| `dueAtRaw` is an ambiguous numeric date, no order evidence | **forced to `low`** |
| `dueAtRaw` is ambiguous, model cites page evidence for the order | capped at `medium` |
| `dueAtSource: "inferred"` | **forced to `low`** |
| `dueAtSource: "derived"` | capped at `medium` |
| `dueAtSource: "printed"` but no `dueAtRaw` given | capped at `medium` |
| `dueAt` present but not strict ISO 8601 | date dropped, capped at `medium` |
| `sourcePage` absent | capped at `medium` |
| `sourcePage` outside `1..page_count` | page dropped, capped at `medium` |
| `domain` outside the six | reassigned to `home`, **forced to `low`** |

Caps only ever lower confidence. A model that returns `low` keeps `low`.

### The date-order rule

A date is **ambiguous** when it is written in digits with no month name, both components are ≤ 12,
and they differ. `1/12` is 1 December or 12 January and the page does not say which.

Deliberately **not** ambiguous, because the page itself settles it:

| Form | Why |
|---|---|
| `25/12/2026` | 25 cannot be a month |
| `12/12/2026` | identical either way |
| `2026-07-30` | ISO ordered |
| `30 July 2026`, `Jul 30 2026` | month spelled out |
| `within 30 days` | not a numeric date; caught by `dueAtSource: derived` instead |

This narrowness is the calibration. A blanket "all slash dates are low" rule would be miscalibrated
in the other direction and would train users to ignore the flag.

**The practical guarantee: a digits-only day/month date can never come back `high`.** With no
supporting evidence it is `low`; with evidence the model claims but nothing here can verify, it is
`medium`.

### What the user sees

Every downgrade appends a plain-language explanation to `notes`, quoting the printed form:

> The date is printed as "1/12", which can be read two ways (day/month or month/day). Check the
> page before accepting.

> The page this was read from could not be determined, so there is no citation to check.

> The page reference returned for this item did not exist in the document, so it has been removed
> and there is no citation to check.

An uncitable candidate **says so** rather than carrying an invented page number. A CitationChip
that opens the wrong page is worse than an absent one: it looks like corroboration.

---

## Grounding `sourcePage`

`sourcePage` is only as good as the page information the model is given.

- **Page images** (`page_images_json`) — best. Pages arrive numbered and in order, and the prompt
  names them explicitly: *"the page images are attached in this order: page 1, page 2. sourcePage
  refers to these numbers."*
- **Text with `Page N` markers** — workable. The prompt points the model at the markers.
- **Text with no markers, or a single opaque blob** — not groundable. The prompt instructs the
  model to return `null`, and the hardener adds the explanatory note.

> **This is currently a gap on the .NET side.** `DocumentExtractionRequest(byte[] Bytes, string
> MimeType, …)` passes the whole document as one opaque byte array, and `PageCount` is computed at
> upload purely as a rejection gate — it never travels with the extraction request. Nothing in the
> repo assigns `sourcePage` a non-null value today. Populating it requires the host to split pages
> and populate `page_images_json`; this flow is ready for that input, and the host is not yet ready
> to produce it.

---

## Handoff to the planning agent

The Document Agent's output is **not** an input to the planning agent. The user is.

1. Flow returns the JSON above; the worker stores the candidates on the `ScannedDocument` and sets
   its status to `ready_for_review`.
2. The review UI renders each candidate with its CitationChip and confidence indicator. Low
   confidence renders as a warning and requires confirmation.
3. The user accepts (optionally editing `title`, `domain`, `priority`, `dueAt`, `notes`) or
   discards each one, and the client posts `{ accepts, discards }` to
   `POST /me/document-scans/{id}/review`.
4. That endpoint turns accepted candidates into Tasks, keyed on
   `(userId, sourceDocumentId, sourceTaskKey)`.
5. From there they are ordinary Tasks. The planning agent sees them the way it sees any other task
   — through `pendingTasks` — with no knowledge that they came from a document.

Note what the endpoint will and will not take from the user: `title`, `domain`, `priority`,
`dueAt` and `notes` are editable on accept; **`confidence`, `sourcePage` and `estimate` are carried
from the stored candidate and cannot be supplied by the caller.** The values this flow assigns to
those three are final. That is another reason the calibration has to be right here rather than
somewhere downstream.

---

## What is unverified

Authored and checked by machine:

- The JSON parses; every edge source, target, handle and field reference resolves; the graph is a
  single chain with one root and one leaf; encoded handle strings round-trip to their structured
  form. *(84 structural checks.)*
- Every embedded Python component compiles; each template field matches a declared input in the
  code, `field_order` matches input order, every declared output method exists, and no component
  reads a `self.<field>` that is not a declared input.
- No embedded JWT, API key or bearer token; no hardcoded host; secret fields hold a variable name
  and set `load_from_db`.
- The hardening logic, driven directly out of the shipped JSON with Langflow stubbed: the ambiguity
  predicate, every confidence cap in the table above, citation integrity, enum coercion, length
  clamps, the 15-candidate slice, date serialization and timezone conversion, key determinism
  against the Node and .NET formulas, and recovery from fenced, prose-wrapped and unparseable model
  output. *(95 assertions.)*

**Not verified, and not verifiable without a running Langflow and real scans:**

- That Langflow 1.10 imports this file without complaint. The node and template shapes are modelled
  on a known-good export, but Langflow re-derives custom-component frontend nodes from their `code`
  at import and may reshape them.
- That the gateway accepts the request. The wire shapes mirror the C# models, but no call was made.
- **Extraction quality — the whole point.** Whether the model finds the right candidates, reads
  dates correctly, and assigns page numbers accurately is completely untested. The mechanical caps
  guarantee that a date the model *reports* as uncertain comes back `low`; nothing here can
  guarantee the model notices it is uncertain in the first place.

The calibration rules are the part most worth testing against real scans first — specifically the
rate of `low` on documents a human would call clean. If that rate is high, the warning is being
diluted and the rubric needs tightening, not loosening.

---

## Divergences from the existing pipeline

1. **No `estimate`.** The .NET `ExtractedTaskCandidateDto` and the Node `DraftCandidate` both carry
   an estimate (`estimateMinMinutes` / `estimateMaxMinutes`, snapped to a bucket ladder), and
   `ToDocument` only builds one when both are present. This flow emits the candidate contract as
   specified in `queries/documentScans.ts`, which has no estimate, so tasks created from this path
   will have none. Adding it means two extra enum-constrained fields in the prompt and a bucket
   snap in the hardener — a contained change, but a contract expansion that should be decided
   rather than slipped in.

2. **The .NET worker does not validate vocabularies.** `DocumentScanWorker.ToDocument` copies
   `Domain`, `Priority` and `Confidence` straight through with no membership check, so a bad enum
   would be persisted and shipped to the client verbatim. The hardener is currently the *only*
   thing stopping that. Worth a defensive check in `ToDocument` regardless of what feeds it.

3. **Three incompatible taxonomies exist in the tree.** The document-scan slice uses
   `health/home/car/finance/family/pets` and `low/normal/high/urgent`; the planning agent's prompt
   uses `Financial/Work/University/Health/Vehicle/Home/Personal/General` and
   `normal/important/urgent`; the older Claude document prompt uses a third set. This flow targets
   the document-scan vocabulary, and the hardener maps anything else to `home` at `low` confidence
   rather than dropping the candidate. If a candidate ever needs to reach the planning agent
   directly, someone has to reconcile these.

4. **Candidates left unhandled block `reviewedAt`.** The review endpoint stamps `reviewedAt` only
   when every candidate has a `taskId`, and it never changes the document's status. A user who
   accepts some and ignores the rest leaves the document in `ready_for_review` indefinitely.
   Pre-existing behaviour, unrelated to this flow, but it interacts with it: emitting more
   candidates makes a partially-reviewed document more likely.

5. **The planning-agent baseline embeds live credentials.** `planning-agent.v3.baseline.json`
   contains a 32-character Mistral API key and a signed JWT in plaintext, and `SaveTaskTool` posts
   to a hardcoded `https://localhost:7276` with `verify=False`. Already tracked in
   `docs/RESUME.md`; noted here because this flow was written to avoid all three and the contrast
   is the reason for the configuration design above.
