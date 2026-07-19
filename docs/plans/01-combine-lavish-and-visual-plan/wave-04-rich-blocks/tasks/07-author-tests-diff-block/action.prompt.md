## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/07-author-tests-diff-block": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing golden-HTML xUnit tests** for the `:::diff` block, in a new file
`tests/Charter.Core.Tests/DiffBlockTests.cs`, class trait-tagged `[Trait("Category", "DiffBlock")]`. TDD
"red" **without stubs**: compile against the EXISTING renderer surface + the `BlockKind.Diff` member (task
01), FAIL at **runtime** because `:::diff` still classifies to `Note`. Task `08-implement-diff-block` makes
them pass. Do NOT implement the classifier or renderer. Read `RendererGoldenTests.cs`,
`src/Charter.Core/BlockModel.cs`, `src/Charter.Core/CharterRenderer.cs`, and `src/Charter.Core/SourceMap.cs`
first — and note the sub-anchor model that `06-implement-comparison-block` establishes in `SourceMap`
(per-sub-element content-derived anchors → per-sub-element source lines); `:::diff` reuses it per line.

Author these golden facts for a small `:::diff` document (a container wrapping a few diff lines — e.g.
lines prefixed `+`/`-`/context; assert against the shape the renderer will accept):

1. **Classification.** `BlockDocument.Parse(md).Blocks[0].Kind == BlockKind.Diff`.
2. **Block-level anchor.** `CharterRenderer.Render(md)` emits a diff block whose root carries `id="{block.Id}"`.
3. **Per-line sub-anchors (the invariant).** Each rendered diff LINE carries its OWN stable anchor
   (`id`/`data-anchor` = `Block.StableId(<that line's raw text>)`), and added vs. removed lines are
   distinguishable in the markup (e.g. a class like `diff-add`/`diff-del`). Assert two distinct lines carry
   two DISTINCT content-derived sub-anchors.
4. **Source-map round-trip for per-line sub-anchors.** `SourceMap.Build(md).LineForAnchor(lineSubId)`
   resolves a per-line sub-anchor to the 1-based markdown line of THAT diff line.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Core.Tests/`. The harness runs a
`git diff` check after this task and rejects any edit outside that path (including `src/Charter.Core/` and
any `.csproj`); an out-of-scope edit fails the task and consumes a retry. If a compile error is caused by a
missing symbol in another file (e.g. `BlockKind.Diff`), do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.

**Required coverage (a guardrail greps the DiffBlock test file — each MUST appear):**
`[Trait("Category","DiffBlock")]`, `BlockKind.Diff`, a per-line sub-anchor token (`data-anchor` or a second
distinct `id=`), `Block.StableId`, `SourceMap`, and a real `[Fact]`/`[Theory]`.

**Completion criteria (match this task's guardrails):** `tests/Charter.Core.Tests` BUILDS with the DiffBlock
tests present, and `dotnet test --filter "Category=DiffBlock"` FAILS. Failing at runtime is intended; not
compiling is a mistake to fix.
