## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/09-author-tests-question-schema": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing xUnit tests** for the `:::question` **schema** AND the minimal **stub** they compile
against. The `QuestionSpec` type is a **behavioral** type (it parses + validates), so per stub-based TDD
(#155) this task writes TWO artifacts so the test project COMPILES and the tests FAIL against the stub:

1. **The stub** `src/Charter.Core/QuestionSpec.cs` — a `public sealed record QuestionSpec` with the schema
   fields, and a static entry point that PARSES + VALIDATES a body string but currently
   `throw new NotImplementedException();`. The schema (the single source of truth — invariant: format
   single-sourced) is:
   - `Id` (string, required), `Title` (string, required)
   - `Mode` — an enum with members for **single**, **multi**, **free-text**, **bool**, **number**
     (choose C# names, e.g. `SingleSelect`/`MultiSelect`/`FreeText`/`Bool`/`Number`; the token mapping is the
     implementer's, but the five modes MUST exist)
   - `Options` (a list — required + non-empty for single/multi; ignored/absent for free-text/bool/number)
   - `Target` — an enum with members **human** and **agent**
   - A static parse/validate entry point (e.g. `static QuestionSpec Parse(string body)` and/or
     `static (bool ok, string? error) Validate(...)`) — throwing `NotImplementedException` in the stub.
2. **The tests** `tests/Charter.Core.Tests/QuestionSchemaTests.cs`, class trait-tagged
   `[Trait("Category", "QuestionSchema")]`:
   - **Valid parse.** A well-formed body parses to a `QuestionSpec` with the expected id/title/mode/options/
     target. Use a **JSON** body (JSON is a subset of YAML, so a JSON body is accepted by either a
     System.Text.Json parser or a YAML parser — this keeps the test parser-agnostic and needs no new
     dependency; the plan's "YAML/JSON" is satisfied JSON-first, with YAML as an optional future superset).
   - **Known-bad reject (load-bearing).** At least one deliberately INVALID body — e.g. a missing `id`, an
     unknown `mode`, or a `single`/`multi` question with no `options` — is REJECTED by validation (an
     exception or a `Validate` returning not-ok). This is the anti-tautology teeth of a schema task.
   - Cover each of the five modes at least in the type surface (a `[Theory]` over the mode tokens is fine).

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Core.Tests/` and
`src/Charter.Core/QuestionSpec.cs` (the stub). Do NOT edit `CharterRenderer.cs`, `BlockModel.cs`,
`SourceMap.cs`, `Charter.Server`, or any `.csproj`. An out-of-scope edit fails the task and consumes a
retry. If you hit a compile error from a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.

**Required coverage (a guardrail greps the QuestionSchema test file — each MUST appear):**
`[Trait("Category","QuestionSchema")]`, `QuestionSpec`, a known-bad **negative-assertion** token
(`Assert.Throws`, `Assert.False`, or a known-bad marker `invalid`/`missing`/`unknown` — a bare `Validate`
no longer satisfies this, since a valid-only suite can call it), the five mode names or a `mode` token,
`target`, and a real `[Fact]`/`[Theory]`.

**Completion criteria (match this task's guardrails):** `tests/Charter.Core.Tests` BUILDS (the stub supplies
`QuestionSpec` so the tests are type-correct), and `dotnet test --filter "Category=QuestionSchema"` FAILS
(the parse/validate entry point throws `NotImplementedException`). Compiling is required; failing against
the stub is the intended TDD red.
