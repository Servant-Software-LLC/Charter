---
maxTurns: 75  # turn-expensive (#94/#203): wiring task that integrates with task 04's not-yet-landed ReviewServer changes AND embeds task 05's SDK; live-serve behavior across the injection seam.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-03-annotation-feedback/06-wire-sdk-into-server": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Wire the real annotation SDK (`sdk/charter-annotate.js`, produced by task 05) into `Charter.Server` so the
served, SDK-injected HTML carries the **real** SDK instead of the wave-2 placeholder. This is the
composition step that makes the browser half real: the SDK exists and the API answers, but until this task
the server still injects a placeholder comment.

**Read the real code first — durable markers, not line numbers.** By the time this task runs, task 04 will
have edited `src/Charter.Server/ReviewServer.cs` (it added the `/api/*` + `/events` routing). So this
description reflects the plan-authoring-time state and may have shifted — verify before assuming. Locate the
injection point by **grepping for its marker string, not a line number**: search `ReviewServer.cs` for
`SdkScript` and for the wave-2 placeholder text `data-charter-sdk` — the wave-2 field is
`private const string SdkScript = "<script data-charter-sdk>/* Charter review SDK — wave-2 placeholder; ... */</script>";`.
Do NOT rely on line numbers (task 04 moved them).

Do two things:

1. **Embed the SDK.** Add an `<EmbeddedResource>` to `src/Charter.Server/Charter.Server.csproj` including
   `sdk/charter-annotate.js` (the repo-root `sdk/` file — use a relative `..\..\sdk\charter-annotate.js`
   Include with a stable `LogicalName`). Embedding (not disk-reading) is required so the SDK ships inside
   the single-file binary and the served copy has it at runtime.
2. **Replace the placeholder with the real SDK.** Change how the injected `<script data-charter-sdk>…`
   body is produced so it contains the **real** `sdk/charter-annotate.js` content (read from the embedded
   resource at startup) instead of the wave-2 placeholder comment. Keep injecting via the existing
   `SdkInjector.Inject(...)` call, keep the `data-charter-sdk` marker attribute (tests and the browser find
   the script by it), and keep the saved artifact SDK-free — injection stays serve-time only (invariant 1).

**Scope boundary:** your `writeScope` is `src/Charter.Server/` (edit `ReviewServer.cs` and
`Charter.Server.csproj`; you may add a small resource-loader helper). Do NOT modify `sdk/` (task 05 owns
it), the tests, `Charter.Core`, or `Charter.Cli`. Do NOT break the wave-2 read-only serve or the task-04
`/api/*` routes. An out-of-scope edit fails the task and consumes a retry.

**Completion criteria (match this task's guardrails):** `Charter.Server.csproj` embeds
`charter-annotate.js`; and running `charter review <plan.mdx> --no-open` serves HTML whose body contains
BOTH the `data-charter-sdk` marker AND the real SDK's `CharterAnnotate` namespace (proving the placeholder
was replaced). The whole test suite (wave-1/2/3) must stay green.
