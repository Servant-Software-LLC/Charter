---
maxTurns: 75  # turn-expensive (#94): entry-point wiring + a live serve smoke (start server, poll the loopback URL, assert) against a fresh command surface.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-02-review-server/04-wire-review-cli": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Wire a **`charter review <plan.mdx>`** command into `Charter.Cli` that renders the plan and serves it
read-only over the loopback review server for in-browser preview.

- Add a `ProjectReference` from `src/Charter.Cli/Charter.Cli.csproj` to
  `..\Charter.Server\Charter.Server.csproj`.
- Add a `review` subcommand to `src/Charter.Cli/Program.cs` (System.CommandLine, already referenced),
  parallel to the existing `render` verb and dispatched the same way (`args[0] == "review"`). Keep the
  banner / `--version` / `render` behavior intact.
- The command takes an input `.mdx` path argument and a `--no-open` flag; it creates a `ReviewSession`,
  calls `Charter.Server.ReviewServer.Start(...)`, and PRINTS exactly one stdout line containing the served
  loopback URL in the form `http://127.0.0.1:<port>/?key=<key>` — e.g.
  `Charter review server ready: http://127.0.0.1:5001/?key=abc123`. Unless `--no-open` is passed, also
  open that URL in the default browser. Then keep serving until the process is stopped (Ctrl+C).
- `Program.cs` MUST reference `ReviewServer` and INVOKE `ReviewServer.Start(...)` (the real wiring, not a
  mention).

**Scope:** your `writeScope` is `src/Charter.Cli/` only. Do NOT modify `Charter.Server`, `Charter.Core`,
or the tests.

**Completion criteria (match this task's guardrails):** `Charter.Cli.csproj` references `Charter.Server`;
`Program.cs` invokes `ReviewServer.Start(...)` behind a `review` verb; and running
`charter review <sample.mdx> --no-open` serves the rendered plan (carrying the injected `data-charter-sdk`
SDK marker) over `http://127.0.0.1:<port>/?key=<key>`, rejecting a request that lacks the key and a
path-traversal request.
