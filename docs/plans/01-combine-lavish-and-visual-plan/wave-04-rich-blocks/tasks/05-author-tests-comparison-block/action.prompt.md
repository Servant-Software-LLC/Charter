## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/05-author-tests-comparison-block": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing golden-HTML xUnit tests** for the `:::comparison` block, in a new file
`tests/Charter.Core.Tests/ComparisonBlockTests.cs`, class trait-tagged
`[Trait("Category", "ComparisonBlock")]`. TDD "red" **without stubs**: compile against the EXISTING renderer
surface + the `BlockKind.Comparison` member (task 01), FAIL at **runtime** because `:::comparison` still
classifies to `Note`. Task `06-implement-comparison-block` makes them pass. Do NOT implement the classifier
or renderer. Read `tests/Charter.Core.Tests/RendererGoldenTests.cs`, `src/Charter.Core/BlockModel.cs`,
`src/Charter.Core/CharterRenderer.cs`, and `src/Charter.Core/SourceMap.cs` first.

The load-bearing new concern is **per-sub-element anchoring** (the block catalog says `:::comparison` is
"annotatable per-row/option"). The sub-anchor model this wave establishes: **each row/option carries its own
CONTENT-DERIVED stable sub-anchor** (derive it from the row's own content via `Block.StableId(rowContent)`,
so it survives edits to other rows), and the **`SourceMap` resolves each sub-anchor to its own source
line**. Author these golden facts for a small `:::comparison` document (a container wrapping a couple of
option rows — use the shape the renderer will accept; a pipe table inside the container, or `- Option:`
rows, is fine — pick one and assert against it):

1. **Classification.** `BlockDocument.Parse(md).Blocks[0].Kind == BlockKind.Comparison`.
2. **Block-level anchor.** `CharterRenderer.Render(md)` emits a comparison block whose root element carries
   `id="{block.Id}"` (the block's content-derived stable id), as every other block does.
3. **Per-row sub-anchors (the invariant).** Each rendered row/option carries its OWN stable anchor
   attribute (e.g. `id="{rowSubId}"` or `data-anchor="{rowSubId}"`) that the SDK can bind to — assert that
   two distinct rows carry two DISTINCT sub-anchors, and that a sub-anchor is derived from that row's
   content (i.e. equals `Block.StableId(<that row's raw text>)`, computed in the test the same way, so it is
   asserted structurally, never a hard-coded hash).
4. **Source-map round-trip for sub-anchors.** `SourceMap.Build(md).LineForAnchor(rowSubId)` resolves a
   per-row sub-anchor to the 1-based markdown line of THAT row (not merely the block's start line) — the
   per-sub-element half of the comment-in-place round-trip (invariant 2). Also assert the block-level id
   still resolves.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Core.Tests/`. The harness runs a
`git diff` check after this task and rejects any edit outside that path (including `src/Charter.Core/` —
task 06's surface — and any `.csproj`); an out-of-scope edit fails the task and consumes a retry. If a
compile error is caused by a missing symbol in another file (e.g. `BlockKind.Comparison`), do NOT edit that
file — write `{"needsHuman": "<what is missing>"}` and stop.

**Required coverage (a guardrail greps the ComparisonBlock test file — each MUST appear):**
`[Trait("Category","ComparisonBlock")]`, `BlockKind.Comparison`, a per-row sub-anchor token
(`data-anchor` or a second distinct `id=`), `Block.StableId`, `SourceMap`, and a real `[Fact]`/`[Theory]`.

**Completion criteria (match this task's guardrails):** `tests/Charter.Core.Tests` BUILDS with the
ComparisonBlock tests present, and `dotnet test --filter "Category=ComparisonBlock"` FAILS. Failing at
runtime is intended; not compiling is a mistake to fix.
