# Looking at MongoDB

The connection string is all you need — it contains the host, the user and the password:

```
mongodb+srv://fadya4265_db_user:<password>@lifeadminautopilotclust.9q1x52b.mongodb.net/
```

Database: **`LifeAdminAutopilotDB`** (cluster `lifeadminautopilotclust`).

## The easy way: MongoDB Compass

The official GUI. Free, and it takes about two minutes.

1. Download from <https://www.mongodb.com/try/download/compass>
2. Open it → **New connection**
3. Paste the whole connection string into the URI box → **Connect**
4. Left sidebar → `LifeAdminAutopilotDB` → click a collection

You get a table view, a JSON view, and a filter bar. Useful filters:

```javascript
// everything for one user
{ "UserId": "73010c68-3732-4ebb-f68b-08def1aee2ac" }

// tasks the agent created from speech
{ "SourceType": "voice" }

// tasks due after today
{ "DueDate": { "$gt": { "$date": "2026-08-07T00:00:00Z" } } }
```

Sort by `_id` descending to see the newest rows first — a Mongo `ObjectId` starts with a
timestamp, so `_id` order *is* creation order. That's the quickest way to confirm a task
you just saved actually landed.

## In the browser: Atlas

If you have the Atlas account login (ask Fady — the user is `fadya4265_db_user`):
<https://cloud.mongodb.com> → your cluster → **Browse Collections**.

Same data, nothing to install, but you need the Atlas account, not just the connection
string. Compass only needs the string.

## Network access

Atlas blocks unknown IPs. If Compass hangs on connect, that's why — Atlas → **Network
Access** → add your current IP. If the cluster is set to `0.0.0.0/0` (open to the
internet, which it appears to be since this connects from anywhere), it will just work.

That openness is worth raising with the team before the project is graded: combined with
a password sitting in a connection string that's been pasted into chats and config files,
anyone who sees the string has full read/write to all the project data. Rotating it and
restricting the IP list is a ten-minute job.

## What's actually in there

Snapshot taken 2026-08-07:

| Collection | Docs | Written by |
| --- | --- | --- |
| `tasks` | 76 | the Planning Agent's commit, plus Swagger testing |
| `documents` | 29 | commit, when a file is attached |
| `contentChunks` | 61 | commit — text + embedding vector, this is the RAG index |
| `PlanningSessions` | 5 | draft tasks awaiting confirmation |
| `reminders` | 1 | barely used yet |
| `calendarEvents` | 1 | barely used yet |

### `tasks`

```json
{ "_id": ObjectId, "UserId": "73010c68-…", "Title": "Play piano",
  "DueDate": ISODate, "Status": "Pending", "SourceType": "voice",
  "Category": "Personal", "Priority": "normal" }
```

Values in use: `Category` is Financial / Work-University / Health / Vehicle / Home /
Personal / General. `Priority` is normal / important / urgent. `SourceType` is voice /
document / text.

**`Category` and `Priority` are stored, but the C# `UserTask` entity has no fields for
them** — it declares only Id, UserId, Title, DueDate, Status, SourceType. So anything
saved through `/api/usertaskstest` silently loses both. The commit endpoint the agent
uses does store them, which is why 70 of 76 rows have them. Worth adding the two
properties to the entity.

### Data-quality problems visible right now

- **Duplicates.** "Pay the electricity bill" appears 5 times, twice within 9 seconds —
  the agent calling `save_task` twice for one confirmation. The prompt now forbids
  re-saving a task once `saved: true` comes back.
- **Swagger defaults committed as real rows.** `Title: "string"`, `Status: "string"`,
  `UserId: "string"` — someone pressed Execute on the default example. Harmless but it
  will look sloppy in a demo.
- **Empty `UserId`.** Several recent rows have `""`, so they belong to nobody and no
  user-scoped query will ever return them.
- **A typo in the data**: `Priority: "importantt"`.

None of these break anything, but they're all visible if a supervisor opens Compass.
Cleaning them is one delete query per problem.
