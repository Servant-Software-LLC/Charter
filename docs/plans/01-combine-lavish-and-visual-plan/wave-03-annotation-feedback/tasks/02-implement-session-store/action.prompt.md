## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-03-annotation-feedback/02-implement-session-store": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement the `AnnotationStore` in `src/Charter.Server/AnnotationStore.cs` by filling **real logic over the
`NotImplementedException` stubs** the previous task wrote, so the `Category=AnnotationStore` tests pass.
Fix the implementation, not the tests.

Requirements the tests encode:

- **Single-writer / locked (the plan's flagged store-concurrency open item).** `Enqueue` and `Drain` must
  be thread-safe: guard the internal queue with a `lock` (or an equivalent single-writer discipline). Under
  concurrent `Enqueue` + `Drain` no annotation may be lost or duplicated — this is the race the tests
  hammer with hundreds of concurrent calls.
- **`Drain` is atomic** — it returns the currently-queued annotations AND clears them under the same lock,
  so two racing `Drain`s never both return the same annotation.
- **`WaitForPendingAsync(timeout, ct)`** — the long-poll signal: complete `true` promptly when an
  annotation is (or becomes) available while a wait is outstanding, and `false` on timeout. Use a signaling
  primitive (e.g. a `SemaphoreSlim` released on `Enqueue`), NOT a busy-wait / fixed sleep.

**Scope boundary:** your `writeScope` is `src/Charter.Server/AnnotationStore.cs` and
`src/Charter.Server/Annotation.cs` only. Do NOT modify the tests (`tests/Charter.Server.Tests/`),
`ReviewServer.cs`, `Charter.Core`, or `Charter.Cli`. An out-of-scope edit fails the task and consumes a
retry. If the authored tests are genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` to the state-out path rather than editing them.

**Completion criteria (match this task's guardrail):**
`dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj --filter "Category=AnnotationStore"`
passes (exit 0), including the concurrency race.
