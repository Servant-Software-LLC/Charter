## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-05-export-handoff/05-implement-handoff-markdown": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement `Charter.Core.HandoffMarkdown` so the `Category=HandoffMarkdown` tests
(`tests/Charter.Core.Tests/HandoffMarkdownTests.cs`, authored by task 04) pass. **Fill real logic over the
existing stub; do NOT edit the tests.** Read the current shape of the stub and its neighbors first (do not
assume a remembered shape): `src/Charter.Core/HandoffMarkdown.cs`, `src/Charter.Core/BlockModel.cs`
(`BlockDocument.Parse`, `BlockKind`), `src/Charter.Core/QuestionSpec.cs` (`QuestionSpec.Parse`).

**Reuse `BlockDocument.Parse` — do not re-implement Markdig traversal** (format single-sourced, invariant
3: the block model is the one source of truth for what a block IS). `Emit(markdown, answers)` should:

1. Call `BlockDocument.Parse(markdown)` to get the ordered blocks (`Kind` + `RawContent`).
2. For each block, in source order, produce its handoff text by `Kind`:
   - **`Prose`, `Heading`, `List`, `Table`, `Code`** — emit `RawContent` verbatim (already plain
     CommonMark; nothing to convert).
   - **`Note`, `Warn`** — a `:::note`/`:::warn` block's `RawContent` SPANS the whole container, so strip
     the opening fence line (matches `^:::\w+`) and the closing fence line (matches `^:::\s*$`), keep
     what's between, and re-emit each remaining line prefixed with `> ` (a blockquote), with the FIRST
     non-empty line additionally prefixed with the bold label — `**Note:** ` for `Note`,
     `**Warning:** ` for `Warn`.
   - **`Comparison`** — strip the SAME fence-line pair, keep everything between VERBATIM (the inner
     content is already a plain markdown list — nothing else to convert).
   - **`Diagram`** — strip the fence-line pair, wrap what's between in a fenced code block:
     `` ```mermaid `` newline, the inner Mermaid source verbatim, newline, `` ``` ``.
   - **`Diff`** — same shape as `Diagram` but the fence language is `diff`.
   - **`Question`** — strip the fence-line pair to get the JSON body, parse it with `QuestionSpec.Parse`.
     If `answers` is non-null and contains an entry for `spec.Id`, emit resolved prose:
     `**Q: {spec.Title}** — Answered: {values joined with ", "}`. Otherwise emit a clearly-flagged open
     question: `> **Open question (unresolved):** {spec.Title}` — and when `spec.Mode` is `SingleSelect`
     or `MultiSelect`, append the options, e.g. `(options: A, B)`. NEVER emit the raw JSON body in either
     branch.
3. Join the per-block handoff texts with a blank line between them and return the result.

**Invariant 5 is the acceptance bar, not `guardrails validate` (which validates a Guardrails task-DAG
folder, not a plan document — a category error for this handoff).** The concrete, deterministic proxy the
tests enforce: no LINE in the output begins with `:::` (line-anchored — NOT a bare substring search; a
Prose block is passed through verbatim and may legitimately MENTION `:::note` mid-sentence as
documentation text, which must survive unmolested — test 12 in the authored suite is the regression guard
for this distinction, do not "fix" the implementation to strip `:::` wherever it appears in prose), the
output is itself valid input back through `Charter.Core.BlockDocument.Parse`/`CharterRenderer.Render` (self-parse
round-trip), and it carries no annotation-loop artifact (`data-anchor`, `<script`, `data-charter-sdk`).
Satisfy these exactly as the tests assert them — do not weaken a test to make this easier.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/HandoffMarkdown.cs`. Do NOT edit
the tests, `BlockModel.cs`, `QuestionSpec.cs`, any `.csproj`, or any file in `src/Charter.Cli/` (a later
task wires the CLI verb). An out-of-scope edit fails the task and consumes a retry. If the authored tests
are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path and stop rather
than editing them.

**Completion criteria (match this task's guardrails):** `dotnet test --filter "Category=HandoffMarkdown"`
passes, and the implementation genuinely dispatches on `BlockKind` (a real `switch`/`if` chain over the
parsed blocks) rather than hard-coding the test fixtures' exact strings.
