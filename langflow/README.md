# Langflow flow

The Planning & Confirmation Agent, wired to this backend's real endpoints. Voice and
documents now enter the flow instead of only typed text.

```
Transcribe Audio ─┐
Stage Document  ──┼─► Prompt ─► Agent (Mistral) ─► Chat Output
Chat Input      ──┘             │
                                ├─ save_task
                                └─ get_file_url
```

## Import it

1. Langflow → **Import** → `Life Admin Autopilot.json`.
2. Re-enter the secrets. Every password field is blanked before the file is written, so
   the export is safe to commit — but that means you supply them after each import:
   - **MistralAI → Mistral API Key**
   - **Login Password** on Transcribe Audio, Stage Document, Save Task and Get File URL
3. Set **Login Email** on those same four components to a user that exists in the
   backend (register one at `POST /api/auth/register` first).
4. Run the API. Default base URL is `https://localhost:7276`; **Verify TLS** is off so
   the localhost dev certificate is accepted. Turn it on for a deployed API.

## What each component does

| Component | Calls | Notes |
| --- | --- | --- |
| **Transcribe Audio** | `POST /api/speech/transcribe` | WAV/MP3 only — the provider rejects AAC/M4A, which is what iOS voice memos are. Set **Language** to `ar-EG` or `en-US`. |
| **Stage Document** | `POST /api/documents/staging` | PDF or photo. Emits an `ATTACHED DOCUMENT` block containing the `storedPath` the agent must pass to `save_task`. |
| **Save Task** *(tool)* | `POST /api/usertaskstest`, `POST /api/documentstest` | Refuses to save a task with no due date. Links the staged document to the new task. |
| **Get File URL** *(tool)* | `GET /api/files/read-url` | Mints a 15-minute signed link so the user can open a file they uploaded earlier. |
| **Upload Avatar** | `POST /api/users/me/avatar` | FR-1.6. Not in the flow — import `components/upload_avatar.py` on its own when you need it. |

Every component logs in itself and caches the token until it expires, so a five-tool
conversation performs one login, not five.

### Why `Language` matters

Left blank, the ASR model auto-detects — and auto-detection fails badly on Arabic,
returning Latin transliteration rather than Arabic script. Send the user's locale.

### Empty inputs are a normal state

No recording and no document is how a typed-only command looks. Both components emit an
empty string, the prompt shows an empty section, and the agent is told that an empty
section means the user did not provide it. A recording that *was* supplied but failed to
transcribe raises instead — silently returning empty there would let the agent invent a
task out of nothing.

## Where the commit endpoint lives

`save_task` posts to **`/api/Planning/commit`** with a nested `{task, document}` payload.
That endpoint is **not in this repository** — no `PlanningController` exists on any
branch or in any commit. It is on the backend build the team runs (story #35), and it is
what has been writing the `tasks` collection all along, including the `Category` and
`Priority` fields and the `contentChunks` embeddings.

Do not "fix" this by repointing it at `/api/usertaskstest`. That route goes through the
C# `UserTask` entity, which declares only Id, UserId, Title, DueDate, Status, SourceType —
so category and priority are dropped, and no embedding is produced. The endpoint is
configurable on the component (**Commit Endpoint**) if the route ever moves.

## Two gaps that remain

**1. Staged documents expire.** `POST /api/documents/staging` writes to
`documents-staging`, which an Azure lifecycle rule clears after about a day. Promotion to
the permanent `documents` container is `PromoteStagedDocumentAsync`, which has no HTTP
endpoint of its own. If the commit endpoint does not promote the blob it is given, the
document record will eventually point at a deleted file.

**2. The `document` half of the commit payload is unverified.** The original tool always
sent `"document": null`, so the shape the server expects for a real document is a guess
based on the `Document` entity (`blobUrl`, `category`, `sourceType`, `uploadedAt`). If
the server rejects it with a 400 or 422, `save_task` **retries with `document: null`** so
the task is still saved, and reports that the document was not attached. Confirm the real
DTO with whoever owns #35 and adjust `_document_payload` if needed.

## Rebuilding after editing in the UI

The flow is a build artefact. Component Python lives in `components/` so it stays
reviewable in the repo instead of only inside a JSON string.

```bash
# Langflow must be running (default http://localhost:7860)
python langflow/build_flow.py "C:/Users/Omar/Downloads/Life Admin Autopilot.json"
```

It takes a fresh export, adds the three new nodes and their edges, rebuilds Save Task,
drops the dead Python Interpreter node, and blanks every secret.

**Node templates are built by your running Langflow**, via the same
`POST /api/v1/custom_component` endpoint the UI uses when you paste a component in. Hand-
written templates drift from whatever version you actually run and import as red
"blocked or outdated" nodes; asking Langflow to build them makes that impossible. It also
means a component whose code does not compile fails the build here instead of silently
producing a broken flow.

The script also refuses to run if the export's own edge ids do not round-trip through its
encoder — an edge id Langflow does not recognise is dropped on import, which looks like
the wiring simply vanished.

If you edit a component in the Langflow UI, copy it back into `components/` before
rebuilding, or the rebuild overwrites your change.

### Writing component code: imports must be plain

Langflow does not run your module normally. It parses the AST and executes only the
top-level `import` / `from … import` statements it finds. A defensive

```python
try:
    from langflow.schema.message import Message
except ImportError:
    from lfx.schema.message import Message
```

is skipped entirely, and the name is undefined when the class body is evaluated — the
component then imports as a red node with "blocked or outdated components in the flow".
Keep every import plain and top-level.

## Verification

The generated flow was loaded into Langflow 1.10.2 and built. Every component reports
`valid=True`:

| Vertex | Result |
| --- | --- |
| Chat Input, Prompt Template, Chat Output | ✅ |
| Transcribe Audio | ✅ (no file → empty transcript, as designed) |
| Stage Document | ✅ (no file → empty context) |
| Save Task, Get File URL | ✅ |
| Agent | ❌ `401 Invalid API Key` — expected, the Mistral key is blanked on purpose |

So the graph, the wiring and all four components are confirmed working. What is **not**
verified is a real end-to-end run: that needs the Mistral key, a registered backend user,
and the API running.

## Testing it

`postman/Life-Admin-Autopilot.postman_collection.json` drives the whole chain — login,
transcribe, stage a document, commit, and the Langflow flow itself (text, voice,
document, and both together). Import it into Postman and run the folders in order.

Two things it needs that the collection cannot store for you:

- **`langflowApiKey`** — Langflow **Settings → API Keys → Add New**. Since v1.5 the run
  endpoint rejects a session token and requires an API key.
- **`flowId`** — the UUID in the Langflow browser URL when the flow is open.

Also turn **SSL certificate verification off** in Postman settings, or every call to
`https://localhost:7276` fails on the self-signed dev certificate.

The `tweaks` keys in the Langflow requests are node ids, and **Langflow renames nodes on
import**. If a run returns `Vertex … not found`, open the flow JSON and copy the current
ids for Transcribe Audio and Stage Document.

## Agent instructions

The system prompt is in `build_flow.py` (`SYSTEM_PROMPT`) so it is reviewable and
versioned rather than living only in the Langflow database. Rules that exist for a
specific reason:

- **Reply in the user's language.** The default ASR locale is `ar-EG`; without this the
  agent answers an Arabic speaker in English.
- **Call `save_task` exactly once per task.** Mongo shows "Pay the electricity bill"
  saved 5 times, twice within 9 seconds — the agent double-calling on one confirmation.
- **Never invent a due date.** It must ask, not guess, or reminders fire on dates the
  user never chose.
- **Cairo is UTC+2.** "Tomorrow at 9am" is `T07:00:00.000Z`, not `T09:00:00.000Z`.
- **Leave `conflicts` empty.** The agent cannot see the calendar, so it is in no position
  to claim a clash.

## Security note

The export you started from contained the Mistral API key `k454...XxYy` in clear text,
and it was pasted into a chat. **Rotate it.** Anyone with that file can spend against
your Mistral account. The rebuilt file has it blanked.
