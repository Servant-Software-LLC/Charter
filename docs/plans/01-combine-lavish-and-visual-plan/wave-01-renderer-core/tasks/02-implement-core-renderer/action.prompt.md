---
maxTurns: 75  # turn-expensive (#94): discovers the Markdig API + implements 3 coupled Core components (block model, renderer, source-map) in one task — the #176-coupling trade-off noted at breakdown.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-01-renderer-core/02-implement-core-renderer": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement the `Charter.Core` renderer over the stubs the previous task authored, so its
`[Trait("Category","CoreRenderer")]` tests pass. Reference libraries: **Markdig** (add the
`Markdig` NuGet package to `src/Charter.Core/Charter.Core.csproj`) for CommonMark parsing + custom
containers.

Fill real logic into:
- `src/Charter.Core/BlockModel.cs` — parse markdown into ordered `Block`s; derive each block's stable
  `Id` from a hash of its normalized content (deterministic, stable under unrelated edits).
- `src/Charter.Core/CharterRenderer.cs` — render blocks to portable HTML via Markdig, wrapping each
  block element with its stable `id` attribute.
- `src/Charter.Core/SourceMap.cs` — build the `Id -> markdown line range` map and resolve an anchor
  to its block.

**Rules:** Do NOT edit any file under `tests/` — the tests are the spec. If an authored test is
genuinely wrong or contradictory, write `{"needsHuman": "<why>"}` to the state-out path rather than
changing it (an out-of-scope edit to a test file fails the task and burns a retry). Your `writeScope`
is `src/Charter.Core/` only.

**Completion criteria (match this task's guardrail):** `dotnet test tests/Charter.Core.Tests` passes
the `Category=CoreRenderer` tests (all green), and the solution builds.
