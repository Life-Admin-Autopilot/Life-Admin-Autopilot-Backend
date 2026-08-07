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

## Three gaps you should know about

**1. `/api/Planning/commit` does not exist.** The previous Save Task tool posted there
and got a 404 on every save, reported as a generic "Error saving task to backend". No
`PlanningController` is in the codebase — that is story #35. Until it lands, Save Task
writes through `UserTasksTestController` and `DocumentsTestController`, which are the
only endpoints that currently reach Mongo. Those are **unauthenticated dev scaffolding**
and must not reach a deployed environment.

**2. Category and priority are not persisted.** `UserTask` has exactly `Id`, `UserId`,
`Title`, `DueDate`, `Status`, `SourceType`. The agent extracts a category and a priority
and shows them to the user, but there is nowhere to store them, so ASP.NET drops both
from the request body. Save Task says so explicitly in its result rather than letting the
loss pass unnoticed. Category *is* stored on the document record when one is attached.

**3. Staged documents expire.** `POST /api/documents/staging` writes to
`documents-staging`, which an Azure lifecycle rule clears after about a day. Promotion to
the permanent `documents` container is `PromoteStagedDocumentAsync`, which has no HTTP
endpoint — it belongs to `/planning/commit` (#35). So a document linked from Langflow
today points at a blob that will be deleted. Save Task flags this in its result. Closing
it needs either #35 or a small promote endpoint.

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

## Security note

The export you started from contained the Mistral API key `k454...XxYy` in clear text,
and it was pasted into a chat. **Rotate it.** Anyone with that file can spend against
your Mistral account. The rebuilt file has it blanked.
