## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-06-agent-skill-polish/03-refresh-cli-status-banner": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make the bare-`charter` **status banner** truthful. Read `src/Charter.Cli/Program.cs`. The banner is the
block that renders when no recognized verb is given — the `AnsiConsole.Write(new FigletText("Charter")...)`
call followed by the `AnsiConsole.MarkupLine(...)` status lines (find it by grepping for **`FigletText`** —
do not rely on a line number). Today those lines say:

> `Status: scaffold. The local review server lands next; try charter render.`
> `Try:    charter --version`

Both are now **false**: the local review server has landed, and `render` / `review` / `export` / `handoff`
all exist (verify against the verb dispatch at the top of `Program.cs`).

Update the status `MarkupLine`s so they:
- **Do not** call the tool a `scaffold` and **do not** say the review server "lands next".
- **Do** surface the real command surface — name `render`, `review`, `export`, and `handoff` (at minimum the
  new `export` and `handoff` verbs must appear) so a first-time user sees what the tool can do.
- Keep the one-line hint style (Spectre `MarkupLine`); a truthful "Status:" line is fine as long as it no
  longer says "scaffold".

**Change ONLY the banner text.** Do not touch the verb dispatch (`render`/`review`/`export`/`handoff`), the
`--version`/`-v` handling, or any other logic. The build must stay clean under `TreatWarningsAsErrors`
(watch Spectre markup escaping — literal `[`/`]` in markup must be doubled `[[`/`]]`).

**Scope boundary (harness-enforced):** Write only under `src/Charter.Cli/`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that directory — including `src/Charter.Core/`,
`src/Charter.Server/`, `tests/`, and `README.md`. An out-of-scope edit fails the task immediately and
consumes a retry.

**Completion criteria (match this task's guardrails):** running the built binary with no arguments prints a
banner that no longer contains "scaffold" or "lands next" and that mentions the real `export`/`handoff`
verbs, and `charter --version` still prints `charter <version>`.
