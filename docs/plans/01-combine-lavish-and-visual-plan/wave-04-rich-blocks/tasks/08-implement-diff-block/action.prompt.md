## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/08-implement-diff-block": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement the `:::diff` block in `Charter.Core` so the `Category=DiffBlock` tests
(`tests/Charter.Core.Tests/DiffBlockTests.cs`, task 07) pass. **Fill real logic; do NOT edit the tests.**
This task runs AFTER `04-implement-diagram-block` and `06-implement-comparison-block`, which already added
`:::diagram` and `:::comparison` cases to the same three files — **read their current shape first** (your
segment includes those edits; verify by reading) and ADD to them without disturbing existing behavior:

- **`CharterMarkdown.Describe`** — add a case: a container whose `Info` is `diff` classifies to
  `BlockKind.Diff`.
- **`CharterRenderer.Render`** — emit a diff block whose root carries `id="{block.Id}"`, with each diff LINE
  carrying its own stable per-line sub-anchor (`id`/`data-anchor` = `Block.StableId(<that line's raw text>)`)
  and added vs. removed lines distinguishable in the markup (e.g. `diff-add`/`diff-del` classes).
- **`SourceMap.Build`** — **reuse the per-sub-element descent that `06-implement-comparison-block`
  established** (do not fork a parallel mechanism): register each per-line sub-anchor → the 1-based markdown
  line of THAT diff line, so `LineForAnchor(lineSubId)` resolves per line. Keep sub-anchors purely
  content-derived (invariant 2's survival property); do NOT use positional indices.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/BlockModel.cs`,
`src/Charter.Core/CharterRenderer.cs`, and `src/Charter.Core/SourceMap.cs`. Do NOT edit the tests or any
other project. An out-of-scope edit fails the task and consumes a retry. If the authored tests are genuinely
wrong or incompatible, write `{"needsHuman": "<why>"}` and stop rather than editing them.

**Completion criteria (match this task's guardrails):** `dotnet test --filter "Category=DiffBlock"` passes
(and the previously-green DiagramBlock + ComparisonBlock tests still pass — you did not regress them).
