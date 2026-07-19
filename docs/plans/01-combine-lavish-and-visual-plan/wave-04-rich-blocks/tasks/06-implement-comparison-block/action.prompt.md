## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/06-implement-comparison-block": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement the `:::comparison` block in `Charter.Core` so the `Category=ComparisonBlock` tests
(`tests/Charter.Core.Tests/ComparisonBlockTests.cs`, task 05) pass. **Fill real logic; do NOT edit the
tests.** This task runs AFTER `04-implement-diagram-block`, which already added a `:::diagram` case to the
same three files — **read their current shape first** (your segment includes task 04's edits; verify by
reading, do not rely on remembered line numbers) and ADD to them without disturbing the diagram/note/warn
behavior:

- **`CharterMarkdown.Describe`** — add a case: a container whose `Info` is `comparison` classifies to
  `BlockKind.Comparison`.
- **`CharterRenderer.Render`** — emit a structured comparison. The block root carries `id="{block.Id}"`
  (content-derived stable id, as every block does). **Each row/option additionally carries its own stable
  sub-anchor** — an `id` or `data-anchor` attribute equal to `Block.StableId(<that row's raw text>)`, so a
  reviewer can annotate one row and its anchor survives edits to other rows. Distinct rows get distinct
  sub-anchors.
- **`SourceMap.Build`** — this is the sub-block anchor model's foundation. Extend `Build` so that, for a
  `:::comparison` container, it registers **each per-row sub-anchor → the 1-based markdown line of THAT
  row** (in addition to the block-level id → block start line it already registers for top-level blocks).
  Keep the "first occurrence wins" behavior for duplicate ids. Design this so the mechanism is reusable —
  task `08-implement-diff-block` extends the same descent for per-line diff sub-anchors.

The per-row sub-anchor is what makes `LineForAnchor(rowSubId)` resolve to that row's source line — the
per-sub-element half of the comment-in-place round-trip (invariant 2). Keep sub-anchors purely
content-derived (invariant 2's survival property); do NOT use positional indices.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/BlockModel.cs`,
`src/Charter.Core/CharterRenderer.cs`, and `src/Charter.Core/SourceMap.cs`. Do NOT edit the tests or any
other project. An out-of-scope edit fails the task and consumes a retry. If the authored tests are genuinely
wrong or incompatible, write `{"needsHuman": "<why>"}` and stop rather than editing them.

**Completion criteria (match this task's guardrails):** `dotnet test --filter "Category=ComparisonBlock"`
passes (and the previously-green DiagramBlock tests still pass — you did not regress them).
