## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-03-annotation-feedback/01-author-tests-session-store": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing xUnit tests** plus the **minimal `NotImplementedException` stubs** for the annotation
**session store** in `Charter.Server`. This is the TDD "red": the tests MUST COMPILE and FAIL against the
stubs. Do NOT implement the real logic — task `02-implement-session-store` does that.

The store is the plan's flagged **store-concurrency open item**: Lavish does whole-file read-modify-write
of session JSON, so a concurrent `poll` + `prompts` can race. Charter's store is therefore **single-writer /
locked**, and the tests must prove it.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Server.Tests/` and the two stub files
`src/Charter.Server/AnnotationStore.cs` and `src/Charter.Server/Annotation.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths — including `ReviewServer.cs`,
`Charter.Core`, `Charter.Cli`, the `.csproj` files, or `Charter.sln`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another file,
do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Produce:

1. **Stubs** (compile-only skeletons in `namespace Charter.Server;`):
   - `Annotation.cs` — the payload the store holds. A `public enum AnnotationKind { Element, TextRange,
     DiagramNode }` (the three annotation kinds: element / text-range / diagram-node) and a
     `public sealed record Annotation(string Id, AnnotationKind Kind, string AnchorId, string Note, int?
     SourceLine = null)`. `SourceLine` is the resolved markdown source line — left `null` here; the
     annotation-API task fills it via `SourceMap.LineForAnchor`. This is a pure data model — no stub body
     needed (the record declaration IS the implementation).
   - `AnnotationStore.cs` — `public sealed class AnnotationStore` with, at minimum:
     `public void Enqueue(Annotation annotation)`, `public IReadOnlyList<Annotation> Drain()` (returns the
     queued annotations and clears them, atomically), and
     `public Task<bool> WaitForPendingAsync(TimeSpan timeout, CancellationToken cancellationToken)` (the
     long-poll signal the API uses: completes `true` when an annotation is available, `false` on timeout).
     Every member throws `NotImplementedException` (the behavioral stub).

2. **Tests** in `tests/Charter.Server.Tests/`, in a new file, class trait-tagged
   `[Trait("Category", "AnnotationStore")]` (so a guardrail can `--filter "Category=AnnotationStore"` to
   this task's tests only — distinct from wave-2's `Category=ReviewServer`):
   - **Enqueue + Drain** — an `Enqueue`d annotation is returned by the next `Drain`; a second `Drain`
     returns empty (Drain clears); `Drain` preserves the annotations' identity/fields.
   - **Concurrency race (load-bearing — the flagged open item)** — start N (e.g. 500) concurrent `Enqueue`
     calls interleaved with concurrent `Drain` calls (use `Task.WhenAll` / `Parallel`, no `Thread.Sleep`
     timing hacks), collect every annotation the `Drain`s return plus a final `Drain`, and assert the union
     is EXACTLY the N enqueued annotations — none lost, none duplicated. This is the proof the store is
     single-writer / locked and does not lose annotations under a concurrent poll + prompts.
   - **Long-poll signal** — `WaitForPendingAsync` returns `false` on an empty store after its timeout, and
     returns `true` (promptly) when an `Enqueue` happens while a wait is outstanding.

   **Required coverage (a guardrail greps the AnnotationStore test files — each MUST appear):**
   `[Trait("Category","AnnotationStore")]`, `Enqueue`, `Drain`, and a concurrency primitive
   (`Task.WhenAll` / `Parallel` / `Task.Run`). Lower-bound presence checks — they do not substitute for
   real, meaningful assertions.

**Completion criteria (match this task's guardrails):** the test project BUILDS with the tests + stubs
present, and `dotnet test --filter "Category=AnnotationStore"` FAILS (the stubs throw). Failing is
intended; not compiling is a mistake to fix.
