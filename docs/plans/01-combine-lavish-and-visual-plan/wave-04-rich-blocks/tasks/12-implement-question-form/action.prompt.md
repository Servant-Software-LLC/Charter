## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/12-implement-question-form": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement the `:::question` block's rendering in `Charter.Core` so the `Category=QuestionForm` tests
(`tests/Charter.Core.Tests/QuestionFormTests.cs`, task 11) pass. **Fill real logic; do NOT edit the tests.**
This task runs AFTER the diagram/comparison/diff implements (which already extended the same three files) —
**read their current shape first** (your segment includes those edits; verify by reading) and ADD to them:

- **`CharterMarkdown.Describe`** — add a case: a container whose `Info` is `question` classifies to
  `BlockKind.Question`.
- **`CharterRenderer.Render`** — parse the container body via `QuestionSpec.Parse` (task 10, real parsing)
  and render a **native HTML `<form>`**: the block root carries `id="{block.Id}"` (content-derived stable
  id) and the question id (a hidden field or `data-question-id`, so a submitted answer correlates to its
  question). Emit native controls per mode — `single` → radio inputs per option; `multi` → checkboxes;
  `free-text` → text `<input>`/`<textarea>`; `number` → `<input type="number">`; `bool` → a boolean control.
  Each option's label appears. The form is plain native HTML — it displays with NO Charter JS (the SDK
  wires the serve-time SUBMIT in task 15; do not embed submit JS in the rendered artifact).
- **`SourceMap.Build`** — register the question block's stable id → its markdown start line (the top-level
  loop likely already covers the container; verify against the test).

Keep the `QuestionSpec` schema single-sourced (invariant): parse via the `QuestionSpec` type, do NOT
re-declare the schema in the renderer.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/BlockModel.cs`,
`src/Charter.Core/CharterRenderer.cs`, and `src/Charter.Core/SourceMap.cs`. Do NOT edit the tests,
`QuestionSpec.cs`, or any other project. An out-of-scope edit fails the task and consumes a retry. If the
authored tests are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` and stop rather than
editing them.

**Completion criteria (match this task's guardrails):** `dotnet test --filter "Category=QuestionForm"`
passes (and the previously-green DiagramBlock + ComparisonBlock + DiffBlock tests still pass — you did not
regress them).
