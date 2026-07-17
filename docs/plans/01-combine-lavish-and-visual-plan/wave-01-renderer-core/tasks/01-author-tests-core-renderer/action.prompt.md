## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-01-renderer-core/01-author-tests-core-renderer": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing xUnit tests** plus the **minimal `NotImplementedException` stubs** for the
`Charter.Core` renderer. This is the TDD "red": the tests MUST COMPILE and FAIL against the stubs.
Do NOT implement the real logic — a later task does that.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Core.Tests/` and the three stub
files `src/Charter.Core/BlockModel.cs`, `src/Charter.Core/CharterRenderer.cs`,
`src/Charter.Core/SourceMap.cs`. After this task completes, the harness runs a `git diff` check and
rejects any edit outside these paths — including other production files, the existing
`PlanDocument.cs`, or any `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Produce:

1. **Stubs** (compile-only skeletons; every member throws `NotImplementedException`):
   - `src/Charter.Core/BlockModel.cs` — a `Block` record (a directive/prose block with `Kind`, raw
     content, and a **content-derived stable `Id`**) and a `BlockDocument` that parses markdown into
     ordered `Block`s. The stable-ID derivation method is the behavioral seam under test.
   - `src/Charter.Core/CharterRenderer.cs` — `string Render(string markdown)` returning portable HTML,
     each block wrapped with its stable `id`.
   - `src/Charter.Core/SourceMap.cs` — `SourceMap Build(string markdown)` mapping each block's stable
     `Id` to its markdown **line range**, and `int? LineForAnchor(string anchorId)`.

2. **Tests** in `tests/Charter.Core.Tests/`, each class trait-tagged `[Trait("Category","CoreRenderer")]`
   (so a guardrail can `--filter "Category=CoreRenderer"` to this task's tests only):
   - **Stable-ID tests** — the same block content yields the same `Id`; different content yields a
     different `Id`; IDs are deterministic across runs.
   - **Renderer golden tests** — for prose/heading/list, a `:::note` callout, a GFM table, and a fenced
     code block, `Render` emits the expected HTML fragment carrying the block's stable `id`. Use small
     inline expected-HTML strings (golden-per-block).
   - **Source-map test** (name it with `SourceMap`) — `Build` maps a known block's `Id` to its correct
     markdown line.
   - **Anchor-survival test** (name it `Anchor_SurvivesUnrelatedBlockEdit`) — the load-bearing proof:
     resolve a block's anchor, edit an **unrelated** block above it, re-`Build`, and assert the original
     anchor **still resolves to the right block** (content-derived IDs are stable under unrelated edits,
     unlike positional selectors).

   **Required coverage (a guardrail greps the CoreRenderer test files for these — each MUST appear):**
   `StableId`, a `.Render(` call, `SourceMap`, and `Anchor` + `Surviv` (the anchor-survival test). A
   missing token fails the task. These are lower-bound presence checks — they do not substitute for
   writing real, meaningful tests.

**Completion criteria (match this task's guardrails):** the solution BUILDS with the tests + stubs
present, and `dotnet test --filter "Category=CoreRenderer"` FAILS (the stubs throw). Failing is
intended; not compiling is a mistake to fix.
