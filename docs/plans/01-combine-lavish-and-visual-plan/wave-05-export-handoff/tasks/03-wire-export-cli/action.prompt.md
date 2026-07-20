## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-05-export-handoff/03-wire-export-cli": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Wire an `export` command into `Charter.Cli` that calls the `Charter.Core.ArtifactExporter` implemented by
task 02, so a user can run:

```
charter export <plan.mdx> -o <out.html>
```

Read `src/Charter.Cli/Program.cs` first (do not assume a remembered shape — it already carries `render`
and `review` verbs you must not disturb) and follow the SAME style: a top-level `if (args.Length >= 1 &&
args[0] == "export") { return BuildExportRoot().Parse(args).Invoke(); }` dispatch block placed alongside
the existing `render`/`review` blocks (before the banner fallback), plus a `BuildExportRoot()` local
function mirroring `BuildRenderRoot()`'s shape (a `System.CommandLine` `RootCommand` with an `input`
argument and a required `-o/--out` option).

- The command reads the input markdown, resolves `planDirectory` as the input file's own directory
  (`Path.GetDirectoryName(Path.GetFullPath(inputPath))`), calls
  `Charter.Core.ArtifactExporter.Export(markdown, planDirectory)`, and writes the returned HTML to the
  output path (create the output directory if needed, mirroring `render`'s existing behavior).
  `Program.cs` MUST reference the `ArtifactExporter` type and call its `.Export(` member (the entry point
  wired to the exporter, not a stub).
- Keep the existing banner / `--version` / `render` / `review` behavior intact.

**Scope:** your `writeScope` is `src/Charter.Cli/` only. Do NOT modify `Charter.Core` or the tests.

**Completion criteria (match this task's guardrails):** `Program.cs` references `ArtifactExporter` and
calls `.Export(...)` (the wiring is real), and running `charter export` on a sample `.mdx` that contains a
local relative image produces a non-empty HTML file whose body contains a `data:` URI for that image, does
NOT contain the sample's local temp-directory path anywhere, and does NOT contain the `data-charter-sdk`
marker.
