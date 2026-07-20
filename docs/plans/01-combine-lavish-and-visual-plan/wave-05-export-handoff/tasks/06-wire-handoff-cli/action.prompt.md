## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-05-export-handoff/06-wire-handoff-cli": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Wire a `handoff` command into `Charter.Cli` that calls the `Charter.Core.HandoffMarkdown` implemented by
task 05, so a user (or Guardrails' own `/plan-breakdown` step) can run:

```
charter handoff <plan.mdx> -o <out.md> [--answers <answers.json>]
```

Read `src/Charter.Cli/Program.cs` first — do NOT assume a remembered shape. Task `03-wire-export-cli` has
already landed on this branch and added an `export` verb to this SAME file; read its actual current
shape before adding `handoff` alongside it (grep for the `export`/`review`/`render` dispatch blocks — do
not rely on a remembered line number, which will have moved). Follow the SAME style: a top-level
`if (args.Length >= 1 && args[0] == "handoff") { return BuildHandoffRoot().Parse(args).Invoke(); }`
dispatch block placed alongside the existing verbs (before the banner fallback), plus a
`BuildHandoffRoot()` local function mirroring `BuildRenderRoot()`'s shape: an `input` argument, a required
`-o/--out` option, and an OPTIONAL `--answers` option (a path to a JSON file).

- The command reads the input markdown. If `--answers` is supplied, read that file and parse it as a flat
  JSON object mapping question id → an array of answer value strings — e.g.
  `{"q1": ["A"], "q2": ["some free-text answer"]}` — into an
  `IReadOnlyDictionary<string, IReadOnlyList<string>>`. If `--answers` is omitted, pass `null` for
  `answers` (every question in the plan is handed off as an open/unresolved question — a legitimate,
  common case; do not require the option).

  **This flat-dict `--answers` shape is DELIBERATELY MINIMAL and INTENTIONALLY DISTINCT from
  `Charter.Server.Answer`** (the record `ReviewServer`'s already-implemented `GET /api/answers` returns, as
  a JSON ARRAY of `{QuestionId, Mode, Values, Target}` objects, drained from the live review session's
  `AnswerStore`). Wave 5 deliberately builds `charter handoff` as an OFFLINE, file-in/file-out CLI command
  with no dependency on a running server or a live session — reaching into `Charter.Server`'s HTTP API from
  here would violate the same "no network/live-service dependency" scoping this whole wave was authored
  under. Bridging a live session's drained `Answer[]` into this flat-dict shape (a small array→dict adapter
  plus wherever it's invoked from — a `charter poll`/`charter answers export` command, or folding it into
  `review`) is NOT built by any task in this wave and is explicitly OUT OF SCOPE here — do not attempt to
  add it, and do not treat the shape mismatch as a bug to reconcile. For now, a `--answers` file is
  hand-authored (by a human or an agent transcribing browser-submitted answers) in the shape documented
  above.
- Call `Charter.Core.HandoffMarkdown.Emit(markdown, answers)` and write the returned markdown text to the
  output path (create the output directory if needed, mirroring `render`'s existing behavior).
  `Program.cs` MUST reference the `HandoffMarkdown` type and call its `.Emit(` member (the entry point
  wired to the emitter, not a stub).
- Keep the existing banner / `--version` / `render` / `review` / `export` behavior intact.

**Scope:** your `writeScope` is `src/Charter.Cli/` only. Do NOT modify `Charter.Core` or the tests.

**Completion criteria (match this task's guardrails):** `Program.cs` references `HandoffMarkdown` and
calls `.Emit(...)` (the wiring is real), and running `charter handoff` on a sample `.mdx` containing a
`:::diagram` and an unanswered `:::question` produces a non-empty markdown file that contains NO `:::`
anywhere, contains a ` ```mermaid ` fence, and flags the question as open/unresolved — and, when the same
sample is run again WITH a `--answers` file resolving that question, the output instead shows the
resolved answer for it.
