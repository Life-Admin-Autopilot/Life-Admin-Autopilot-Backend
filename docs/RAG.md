# RAG — embeddings, `contentChunks`, and retrieval

Stories #15 (Atlas + Vector Search), #82 (retrieval service), #83 (Copilot Chat).
Implements the `contentChunks` collection from the team's MongoDB schema and SRS §3.7.

## Shape

```
task / document write ──▶ KnowledgeService.IngestAsync
                            │  chunk (1200 chars, 150 overlap)
                            │  embed  (RETRIEVAL_DOCUMENT)
                            └─▶ contentChunks   { userId, sourceType, sourceId,
                                                  chunkIndex, text, embedding[768], model }

agent question ──▶ POST /me/knowledge/search
                     │  embed (RETRIEVAL_QUERY)
                     └─▶ $vectorSearch (filter: userId)  ──▶ top-k + score
```

**Retrieval lives in this API, not in the flow.** The agent calls it as a tool, the
same way it calls the other eleven. That keeps the owner filter, the erasure cascade
and the quota on the server that owns the data, instead of trusting a flow to scope
its own reads.

## Configuration

| env | section | default |
|---|---|---|
| `EMBEDDINGS_API_KEY` | `Ai:Embeddings:ApiKey` | none — slice stays off |
| `EMBEDDINGS_MODEL` | `Ai:Embeddings:Model` | `gemini-embedding-001` |
| `EMBEDDINGS_BASE_URL` | `Ai:Embeddings:BaseUrl` | Google Generative Language v1beta |

> **Do not reuse `GEMINI_API_KEY` for this.** That name is load-bearing:
> `AiAvailability` reads it as "is the AI slice wired?", and six routes
> (`/me/tasks/search|summarize|estimate-backlog|categorize|translate` and the custom
> clarification answer) pass their 503 gate on it and then immediately throw
> `NotWiredHere`. Setting it turns six honest 503s into 500s. Retrieval has its own
> switch so the two stay independent.

## The Atlas vector index — must be created by hand

A driver can only issue `createIndexes`; an **Atlas Search index is created through
the Atlas UI or Admin API**. `KnowledgeIndexes` therefore creates only the B-tree
index and leaves the vector index to an operator. Create it once per cluster:

- **Name:** `contentchunks_embedding_idx` (must match `ContentChunkVocabulary.VectorIndexName`)
- **Collection:** `contentchunks`
- **Type:** Vector Search

```json
{
  "fields": [
    {
      "type": "vector",
      "path": "embedding",
      "numDimensions": 768,
      "similarity": "cosine"
    },
    {
      "type": "filter",
      "path": "userId"
    }
  ]
}
```

Three things that will silently break it:

1. **`path` is lower-camelCase — `embedding`, not `Embedding`.**
   `MongoKernelConventions` registers `CamelCaseElementNameConvention` globally, so
   the stored element names differ from the C# property names. An index declared on
   `Embedding` matches nothing, returns zero rows, and raises no error. The constants
   `ContentChunkVocabulary.EmbeddingField` / `.UserIdField` exist so the stage and
   this document cannot drift apart.
2. **`userId` must be declared as a `filter` field.** The owner scoping is *inside*
   the `$vectorSearch` stage. Without the declaration Atlas rejects the query.
3. **`numDimensions` is fixed at 768** and pinned by
   `ContentChunkVocabulary.Dimensions`. Changing the model or the dimensionality
   invalidates every stored vector — that is a re-embed, not a redeploy.

`similarity` is `cosine` because the provider normalises vectors at the boundary but
truncated-dimension Gemini embeddings arrive un-normalised (measured ‖v‖ ≈ 0.587).
Cosine is scale-invariant, so it is correct either way; `dotProduct` would not be.

## Behaviour without Atlas

**Retrieval still works.** `$vectorSearch` is an Atlas-managed *approximate* nearest
neighbour index over the whole collection; with the corpus already narrowed to one
user, an exact in-process scan is both feasible and strictly more accurate. When the
cluster rejects the stage, `ContentChunkRepository` loads that user's chunks and
ranks them by cosine similarity in memory.

So Atlas is an optimisation here, not a prerequisite. Create the index when the
corpus outgrows a scan; until then a local `mongod` serves RAG correctly.

## Failure policy

- **Ingest is best-effort.** It runs on the task write path; a slow embedding
  provider must not fail a user's write. Failures are logged and swallowed.
- **Retrieval is not.** A silent empty result there is indistinguishable from
  "nothing matched", so it propagates.

## What retrieval is used for

Beyond answering questions, the same index backs **duplicate detection**:
`ConflictService` embeds a candidate title and treats a hit above 0.92 against
another open matter as "you already have this". That check is best-effort — with
retrieval unconfigured the caller still gets its time-clash answer — because a
missing embedding key must not silently disable the *other* half of conflict
detection.

## Not built yet

- No Langflow tool node calls `/me/knowledge/search` yet, so the chat agent cannot
  cite the corpus. Ingest itself IS wired: `TaskWriteService` indexes every task on
  create.
