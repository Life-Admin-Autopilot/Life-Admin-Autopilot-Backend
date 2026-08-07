# File storage (Azure Blob Storage)

Documents and profile pictures live in Azure Blob Storage. Story #18.

Account: **`lifeadminautopilotdev`** (UAE North, Azure for Students).

| Container | Holds | Cleanup |
| --- | --- | --- |
| `documents-staging` | uploads awaiting confirmation | lifecycle rule deletes after 1 day |
| `documents` | committed documents, each with a task | none — deleted with its task |
| `avatars` | profile pictures | previous avatar deleted on replace |

All three are **private**. Nothing is publicly readable.

## The one decision worth understanding

**`Document.BlobUrl` and `ApplicationUser.ProfilePictureUrl` store a path, not a URL.**

```
documents/6a1f0c74-.../3fa85f64d2e14b0a.pdf
```

The obvious alternative — storing a SAS URL — is a trap: SAS URLs expire, so every record
in Mongo would rot into a dead link, and you'd be persisting a live access credential in the
database. Instead the path is stored, and a **short-lived read URL is minted on demand**
(15 minutes, read-only) whenever a client actually needs to display the file.

The user id is the first path segment, which makes ownership provable from the path alone —
no database lookup needed to reject a request for someone else's passport scan.

## Staging → commit

SRS §7.1: when `/planning/propose` receives a document, the file is stored **immediately**
so Claude can extract from it (FR-3.2) and the user can preview it during confirmation — but
**no `documents` record is written until `/planning/commit`**.

```
POST /api/documents/staging   →  documents-staging/{userId}/{guid}.pdf   (nothing in Mongo)
        ↓ user confirms
PromoteStagedDocumentAsync()  →  documents/{userId}/{guid}.pdf           (Mongo record written)
        ↓ user never confirms
        Azure lifecycle rule deletes it after ~1 day
```

The 24-hour cleanup is an **Azure lifecycle rule**, not a background job — configured in the
portal on the `documents-staging` prefix. Less code to write, test and keep running. It runs
once daily, so "24 hours" is approximate.

`PromoteStagedDocumentAsync` copies first and deletes the source only after the copy
completes — losing the source mid-promote would lose the user's document outright. It also
refuses to promote anything that isn't in the staging container, so a caller with crossed
wires gets an error instead of a silent duplicate.

## API

All endpoints require a bearer token; the user id always comes from the token.

| Endpoint | What it does |
| --- | --- |
| `POST /api/documents/staging` | Upload a document (multipart, field `file`). Returns its path plus a preview URL. |
| `POST /api/users/me/avatar` | Upload a profile picture (FR-1.6). Saves the path to the profile and deletes the old one. |
| `GET /api/files/read-url?path=...` | Exchange a stored path for a short-lived read URL. **403 if you don't own it.** |

```json
200 OK
{ "succeeded": true,
  "path": "documents-staging/6a1f.../3fa85f64.pdf",
  "readUrl": "https://lifeadminautopilotdev.blob.core.windows.net/...?sv=...&sig=...",
  "contentType": "application/pdf", "sizeBytes": 184320 }
```

Failures use the same shape with `succeeded: false` and an `errorCode`.

| Code | Meaning | HTTP |
| --- | --- | --- |
| `STORAGE_NO_FILE` | Nothing uploaded | 400 |
| `STORAGE_FILE_TOO_LARGE` | Over the limit (20 MB documents, 5 MB avatars) | 400 |
| `STORAGE_UNSUPPORTED_FORMAT` | Not an accepted type | 400 |
| `STORAGE_NOT_FOUND` | Blob is gone — likely a staged file that expired | 404 |
| `STORAGE_ACCESS_DENIED` | The path belongs to another user | 403 |
| `STORAGE_NOT_AUTHORIZED` | Account key wrong or rotated | 502 |
| `STORAGE_UNAVAILABLE` | Azure 5xx | 502 |
| `STORAGE_NOT_CONFIGURED` | No connection string in this environment | 503 |

Accepted document types are what **Claude's multimodal API** can read, since the Document
Agent extracts from these files directly: PDF, JPEG, PNG, WebP, GIF. Avatars: JPEG, PNG,
WebP.

## Configuration

```json
"Storage": {
  "DocumentsContainer": "documents",
  "StagingContainer": "documents-staging",
  "AvatarsContainer": "avatars",
  "ReadUrlLifetimeMinutes": 15,
  "MaxDocumentBytes": 20971520,
  "MaxAvatarBytes": 5242880
}
```

The connection string comes from `AZURE_STORAGE_CONNECTION_STRING` — never
`appsettings.json`. It is a **full-access account key**; rotate it on the storage account's
*Access keys* page if it leaks (there are two keys so you can swap without downtime).

```powershell
dotnet user-secrets set "AZURE_STORAGE_CONNECTION_STRING" "<from Portal → Access keys>" --project Life-Admin-Autopilot-Backend
```

⚠️ User secrets only load in **Development**. Deployments must supply it as an environment
variable.

With no connection string the API still starts and every other feature works; storage calls
return `STORAGE_NOT_CONFIGURED`.

## NFR-3: encryption at rest

Satisfied with no code — Azure Storage encrypts all blobs with AES-256 by default
(Storage account → Security + networking → Encryption). In transit it's HTTPS, enforced by
the account's "secure transfer required" setting.

## Verification status

The full lifecycle was verified against the live account:

| Step | Result |
| --- | --- |
| Upload to staging | ✅ path returned |
| Fetch via the minted SAS URL | ✅ HTTP 200, bytes match exactly |
| Request a URL as a **different** user | ✅ `STORAGE_ACCESS_DENIED` |
| Promote staging → documents | ✅ content type and size preserved |
| Staged copy after promote | ✅ gone |
| Download committed copy | ✅ 42 bytes, `application/pdf` |
| Delete | ✅ |

Unit tests (23) cover path building, traversal rejection, ownership, SAS generation and the
unconfigured path. The Azure round trips are verified live rather than mocked.

**Not yet done:** nothing calls `PromoteStagedDocumentAsync` — that happens in
`/planning/commit` (story #35), which does not exist yet. Staging and avatars work today;
the promote step is built and tested but has no caller.
