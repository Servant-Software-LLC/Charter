## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/11-author-tests-question-form": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing golden-HTML xUnit tests** for the `:::question` block's **rendering to a native HTML
`<form>`**, in a new file `tests/Charter.Core.Tests/QuestionFormTests.cs`, class trait-tagged
`[Trait("Category", "QuestionForm")]`. TDD "red" **without new stubs**: compile against the EXISTING
renderer surface + the `BlockKind.Question` member (task 01) + the `QuestionSpec` type (task 09's stub — its
type surface exists even though parse/validate is stubbed), FAIL at **runtime** because `:::question` still
classifies to `Note`. Task `12-implement-question-form` makes them pass. Do NOT implement the classifier or
renderer. Read `RendererGoldenTests.cs`, `src/Charter.Core/BlockModel.cs`, `src/Charter.Core/CharterRenderer.cs`,
and `src/Charter.Core/QuestionSpec.cs` first.

Author these golden facts for a `:::question` document whose body is a JSON question spec (id, title, mode,
options, target):

1. **Classification.** `BlockDocument.Parse(md).Blocks[0].Kind == BlockKind.Question`.
2. **Native `<form>` with the block anchor.** `CharterRenderer.Render(md)` emits a `<form …>` whose block
   root carries `id="{block.Id}"` (content-derived stable id) and carries the **question id** (so a submitted
   answer correlates to its question — e.g. a hidden field or `data-question-id`).
3. **Controls match the mode.** Author at least the load-bearing modes and assert the native control:
   `single` → radio `<input type="radio">` per option; `multi` → `<input type="checkbox">` per option;
   `free-text` → a text `<input>`/`<textarea>`; `number` → `<input type="number">`; `bool` → a boolean
   control. Each `option` label appears in the rendered form.
4. **Standalone-inert, served-interactive.** The rendered form is a plain native `<form>` (it needs no
   Charter JS to display) — assert the markup is native HTML inputs, NOT a script-built widget. (The submit
   WIRING — posting answers to the server — is added serve-time by the SDK, task 15; it is not part of the
   rendered artifact and is not asserted here.)

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Core.Tests/`. The harness runs a
`git diff` check after this task and rejects any edit outside that path (including `src/Charter.Core/` — task
12's surface — and any `.csproj`); an out-of-scope edit fails the task and consumes a retry. If a compile
error is caused by a missing symbol in another file (e.g. `BlockKind.Question` or `QuestionSpec`), do NOT
edit that file — write `{"needsHuman": "<what is missing>"}` and stop.

**Required coverage (a guardrail greps the QuestionForm test file — each MUST appear):**
`[Trait("Category","QuestionForm")]`, `BlockKind.Question`, a `<form` token, a native input token
(`type=\"radio\"` / `type=\"checkbox\"` / `type=\"number\"` / `<textarea`), the question-id correlation token
(`data-question-id` or a hidden id field), and a real `[Fact]`/`[Theory]`.

**Completion criteria (match this task's guardrails):** `tests/Charter.Core.Tests` BUILDS with the
QuestionForm tests present, and `dotnet test --filter "Category=QuestionForm"` FAILS. Failing at runtime is
intended; not compiling is a mistake to fix.
