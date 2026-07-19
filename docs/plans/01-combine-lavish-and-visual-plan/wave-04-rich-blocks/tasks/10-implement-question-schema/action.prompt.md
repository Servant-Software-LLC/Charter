## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/10-implement-question-schema": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement `QuestionSpec` parsing + validation in **`src/Charter.Core/QuestionSpec.cs`** — fill real logic
over the throwing stub task 09 authored — so the `Category=QuestionSchema` tests
(`tests/Charter.Core.Tests/QuestionSchemaTests.cs`) pass. **Do NOT edit the tests.** Read the stub and the
tests first to match their exact entry-point signatures.

- **Parse** the `:::question` body with **`System.Text.Json`** (no new dependency; a JSON body is a subset
  of YAML, so a YAML parser could be layered in later without changing this contract). Map the body to the
  `QuestionSpec` record (`Id`, `Title`, `Mode`, `Options`, `Target`).
- **Validate**: `Id` and `Title` required and non-empty; `Mode` must be one of the five allowed modes;
  `Options` required + non-empty for the single/multi modes and not required for free-text/bool/number;
  `Target` must be `human` or `agent`. A malformed/incomplete body is REJECTED (throw a clear exception,
  or return a not-ok validation result — match whatever shape the tests assert).
- `QuestionSpec` is the **single source of truth** for the question schema (invariant: format
  single-sourced). The renderer (`12-implement-question-form`) and any skill cite this type — do NOT
  duplicate the schema anywhere else.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/QuestionSpec.cs`. Do NOT edit the
tests, the renderer, or any other file. An out-of-scope edit fails the task and consumes a retry. If the
authored tests are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` and stop rather than
editing them.

**Completion criteria (match this task's guardrails):** `dotnet test --filter "Category=QuestionSchema"`
passes — valid bodies parse; the known-bad body is rejected.
