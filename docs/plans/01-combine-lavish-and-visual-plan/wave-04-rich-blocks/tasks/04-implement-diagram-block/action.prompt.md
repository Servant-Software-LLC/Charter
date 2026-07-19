## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/04-implement-diagram-block": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement the `:::diagram` block in `Charter.Core` so the `Category=DiagramBlock` tests
(`tests/Charter.Core.Tests/DiagramBlockTests.cs`, authored by task 03) pass. **Fill real logic over the
existing wave-1 renderer seams; do NOT edit the tests.** Read the current shape of each file before editing
(the wave-1/2/3 code is materialized on the branch — verify, do not assume line numbers):

- **`src/Charter.Core/BlockModel.cs` — `CharterMarkdown.Describe`.** The `switch` currently maps every
  `CustomContainer` to `Warn` (if `Info=="warn"`) else `Note`. Add a case: a container whose `Info` is
  `diagram` classifies to `BlockKind.Diagram`. Keep the existing `note`/`warn` behavior intact.
- **`src/Charter.Core/CharterRenderer.cs` — `Render`.** Emit a `:::diagram` block's Mermaid body as
  `<pre class="mermaid" id="{stable-id}">…mermaid source…</pre>` with the block's content-derived stable id
  (`Block.StableId(rawContent)`) on the block root — the same anchoring convention every other block uses
  (see `CharterCodeBlockRenderer` for the code-block precedent of putting the id on `<pre>`). The Mermaid
  **source text** inside the container must survive into the `mermaid` element so the client library renders
  it. Individual node identity is assigned client-side by Mermaid; the SDK's `diagram-node` annotation kind
  anchors against this block-level stable id.
- **Offline runtime + theme-aware init (portability, invariant 1).** When the rendered document contains
  **at least one** `:::diagram`, inline the **vendored** Mermaid runtime read from
  `Charter.Core.MermaidResource` (task 02) into the output **once**, followed by a theme-aware
  `mermaid.initialize({ startOnLoad: true, theme: … })` / `mermaid.run()` bootstrap. This MUST be an inlined
  `<script>` with the embedded library — **NEVER** a CDN `<script src="https://…">` (the saved artifact must
  render diagrams with no network). When there is **no** diagram, do NOT inline the runtime.
- **`src/Charter.Core/SourceMap.cs` — `Build`.** Ensure the diagram block's stable id resolves via
  `LineForAnchor` to its 1-based markdown start line (the existing top-level-block loop should already
  cover a `:::diagram` container; verify with the test's round-trip assertion).

Keep the C#↔JS boundary narrow (invariant 6): the ONLY browser JS the renderer emits is the **vendored
third-party** Mermaid library plus a minimal init call required for the artifact to self-render — Charter's
own interaction JS (node-annotation binding, question submit) stays in `sdk/`.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/BlockModel.cs`,
`src/Charter.Core/CharterRenderer.cs`, and `src/Charter.Core/SourceMap.cs`. Do NOT edit the tests, the
`MermaidResource`/csproj (task 02 owns those), or any other project. An out-of-scope edit fails the task
and consumes a retry. If the authored tests are genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` to the state-out path and stop rather than editing them.

**Completion criteria (match this task's guardrails):** `dotnet test --filter "Category=DiagramBlock"`
passes, and `CharterRenderer.cs` inlines the vendored runtime (no CDN `src="http…mermaid"`).
