## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/03-author-tests-diagram-block": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing golden-HTML xUnit tests** for the `:::diagram` block, in a new file
`tests/Charter.Core.Tests/DiagramBlockTests.cs`, class trait-tagged `[Trait("Category", "DiagramBlock")]`.
This is the TDD "red" achieved **without stubs**: the tests compile against the EXISTING renderer surface
(`Charter.Core.BlockDocument.Parse`, `Charter.Core.CharterRenderer.Render`, `Charter.Core.SourceMap.Build`,
and the `BlockKind.Diagram` member added by task `01-add-block-kinds`) and FAIL at **runtime** because a
`:::diagram` container still classifies to `Note` and renders as `<div class="note">`. Task
`04-implement-diagram-block` makes them pass. Do NOT implement the classifier or renderer.

Read the real materialized surface first (do not trust a remembered shape):
`tests/Charter.Core.Tests/RendererGoldenTests.cs` (the golden-per-block pattern — assert against
`block.Id`, NEVER a hard-coded hash), `src/Charter.Core/BlockModel.cs` (`BlockDocument.Parse`, `BlockKind`,
`CharterMarkdown.Describe`), `src/Charter.Core/CharterRenderer.cs`, and `src/Charter.Core/SourceMap.cs`.

Author these golden facts for a small document whose body is a `:::diagram` container wrapping a Mermaid
graph (e.g. `:::diagram` / `graph TD; A-->B;` / `:::`):

1. **Classification.** `BlockDocument.Parse(md).Blocks[0].Kind == BlockKind.Diagram`.
2. **Rendered markup + stable id (the diagram-node anchor).** `CharterRenderer.Render(md)` emits the Mermaid
   body inside `<pre class="mermaid" id="{block.Id}">…graph TD…</pre>` (or the block root the renderer
   chooses) carrying the block's content-derived stable id on the block root — assert the id equals
   `BlockDocument.Parse(md).Blocks[0].Id`, exactly as `RendererGoldenTests` does. The Mermaid **source text**
   (`graph TD`, `A-->B`) must survive into the `mermaid` element so the client library can render it. This
   stable block id is what the SDK's `diagram-node` annotation kind anchors against (individual node
   identity is assigned client-side by Mermaid).
3. **Offline Mermaid runtime + theme-aware init (portability, invariant 1).** A rendered document that
   contains at least one `:::diagram` must inline the **vendored** Mermaid runtime and a theme-aware
   `mermaid.initialize(...)` / `mermaid.run(...)` bootstrap in the output — assert the rendered string
   contains a Mermaid init/config token (e.g. `mermaid.initialize`) AND a theme setting token (e.g.
   `theme`), and assert it does **NOT** contain a CDN `src="http…mermaid` link (the saved artifact must
   render diagrams with no network). Also assert a document with **no** diagram does NOT inline the runtime
   (the init is emitted only when a diagram is present).
4. **Source-map round-trip.** `SourceMap.Build(md).LineForAnchor(block.Id)` resolves the diagram block's
   stable id to its 1-based markdown start line.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Core.Tests/`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside that path — including
`src/Charter.Core/` (that is task 04's surface) or any `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another file
(e.g. `BlockKind.Diagram` is absent), do NOT edit that file — write `{"needsHuman": "<what is missing>"}`
to the state-out path and stop (task `01-add-block-kinds` is its ancestor and should have added it).

**Required coverage (a guardrail greps the DiagramBlock test file — each MUST appear):**
`[Trait("Category","DiagramBlock")]`, `BlockKind.Diagram`, a `mermaid` token, a `mermaid.initialize`-shaped
init token, `SourceMap`, and at least one real `[Fact]` or `[Theory]` attribute. Lower-bound presence checks
— they do not substitute for the real golden assertions above.

**Completion criteria (match this task's guardrails):** `tests/Charter.Core.Tests` BUILDS with the
DiagramBlock tests present (all referenced types already exist), and
`dotnet test --filter "Category=DiagramBlock"` FAILS (a `:::diagram` still renders as a Note). Failing at
runtime is intended; not compiling is a mistake to fix.
