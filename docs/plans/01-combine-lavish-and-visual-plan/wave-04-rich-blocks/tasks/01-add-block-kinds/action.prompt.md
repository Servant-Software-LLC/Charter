## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/01-add-block-kinds": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Add the four M4 block kinds to the `Charter.Core.BlockKind` enum in **`src/Charter.Core/BlockModel.cs`**:
`Diagram`, `Comparison`, `Question`, and `Diff`. Read the file first — the enum currently declares
`Prose, Heading, List, Table, Code, Note, Warn`. Add the four new members with brief XML-doc comments
matching the existing style (each names the `:::` directive it will classify: `:::diagram`, `:::comparison`,
`:::question`, `:::diff`).

**This is a pure data-model addition — the enum declaration IS the implementation.** Do NOT change
`CharterMarkdown.Describe` (the `switch` that classifies containers), the `CharterRenderer`, or `SourceMap`.
A `:::diagram` / `:::comparison` / `:::question` / `:::diff` container must STILL classify to `Note` after
this task (the per-block implement tasks add the `Describe` cases + rendering). The point of this task is
only to make the enum members EXIST so the per-block author-tests tasks compile against
`BlockKind.Diagram` etc. and fail at RUNTIME (wrong HTML), not at compile time (#155).

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/BlockModel.cs`. After this task
completes, the harness runs a `git diff` check and rejects any edit outside that path — including
`CharterRenderer.cs`, `SourceMap.cs`, the tests, or any `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Completion criteria (match this task's guardrails):** `src/Charter.Core/BlockModel.cs` declares the four
new enum members (`Diagram`, `Comparison`, `Question`, `Diff`), and `Charter.Core` builds. The
`Describe` switch is unchanged — the containers still map to `Note`.
