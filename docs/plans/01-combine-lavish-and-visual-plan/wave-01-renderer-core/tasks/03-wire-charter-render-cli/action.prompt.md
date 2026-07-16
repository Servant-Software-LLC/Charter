## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-01-renderer-core/03-wire-charter-render-cli": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Wire a `render` command into `Charter.Cli` that calls the `Charter.Core` renderer, so a user can run:

```
charter render <plan.mdx> -o <out.html>
```

- Add the command to `src/Charter.Cli/Program.cs` (using System.CommandLine, already referenced): a
  `render` subcommand taking an input `.mdx` path argument and a required `-o/--out` HTML output path.
- It reads the input markdown, calls `Charter.Core.CharterRenderer.Render(...)`, and writes the HTML
  to the output path. `Program.cs` MUST reference the `CharterRenderer` type (the entry point wired to
  the renderer, not a stub).
- Keep the existing banner / `--version` behavior intact.

**Scope:** your `writeScope` is `src/Charter.Cli/` only. Do NOT modify `Charter.Core` or the tests.

**Completion criteria (match this task's guardrails):** `Program.cs` references `CharterRenderer`
(the wiring is real), and running `charter render` on a small sample `.mdx` produces a non-empty HTML
file whose body contains the rendered content.
