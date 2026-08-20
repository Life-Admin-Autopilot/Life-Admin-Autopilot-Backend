# Working conventions

Notes for anyone making changes in this repo, human or assistant.

## Attribution

**Commits and pull requests carry no tool attribution.**

- No `Co-Authored-By:` trailer naming an AI assistant or any other tool.
- No "generated with", "created by \<tool\>", or similar notice in a commit
  message, a pull-request body, a file header or a code comment.
- A commit's author is the person who made it. Nothing else is added.

This holds on every branch, including ones nobody expects to read again. If a
tool appends a trailer by default, strip it before committing — do not leave it
for someone else to clean up, and do not offer to add one.

One thing this rule does **not** cover: `Life-Admin-Autopilot.DAL/Claude/` is the
product's own Claude API client, registered in `Program.cs` via
`AddClaudeService`. That is application code, not an authorship trace. Leave it
exactly as it is.

## Read before changing behaviour

This service is a port of a Node reference, and parity with it is load-bearing.

- `docs/DIVERGENCES.md` — every deliberate departure from the reference, and why.
  Add a numbered section here when you make another one.
- `docs/TESTING.md` — how the suite is laid out and run
- `docs/RUNNING.md` — the local start sequence
- `docs/KERNEL.md` — the shape of the shared kernel

## Building while the API is running

`dotnet build` fails with `MSB3027` when the PL assembly is loaded by a running
server. Redirect the output rather than stopping the server:

```
dotnet build -p:BaseOutputPath=<a-temp-dir>/
```

Do not also pass `BaseIntermediateOutputPath`: that yields `CS0579` duplicate
`TargetFrameworkAttribute` errors.

## Langflow

`langflow/planning-agent.v4.json` is an exported flow, not a source file the
server reads at runtime. Edits take effect only after
`tools/dev/langflow-import.sh --replace`.

A tool argument has to be declared in three places to exist: the component's
`code`, `node.template.<field>`, and `template.tools_metadata.value[0].args`.
Miss one and the agent silently cannot pass that argument.
