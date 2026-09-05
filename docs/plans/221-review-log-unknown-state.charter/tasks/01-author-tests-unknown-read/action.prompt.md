## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `01-author-tests-unknown-read`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-unknown-read": { "someKey": "someValue" } }`.
- EXCEPTION -- the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them. Nest one inside your
  folder-name key and the harness REJECTS the attempt -- nothing is written.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code -- or reword a document away from its own conventions -- to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the state-out
  path and stop. If instead a guardrail reports something ABSENT that you can see is
  PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and quote
  (a) the guardrail's exact claim and (b) the file:line that refutes it. If you cannot
  produce BOTH quotes it is not a defective guardrail -- retry the work, or escalate as
  "blocked-work". Difficulty is never "defective-guardrail".

## Settled decisions (from the reviewed plan -- do NOT re-decide)

The third state lives on the SERVER, as a third outcome on `ReviewLogRead`, following the
`ProbeResult` shape. `charter poll` reports **exit 4** on Unknown. A **short bounded** retry
fronts the read, mirroring the per-file `TryReadAllText`. The deterministic reproduction is
**browser-level only**, at the Playwright route boundary.

Build against these. If one is wrong, halt with `{"needsHuman": ...}` -- never silently choose
differently.

## Task

Author **failing** tests, plus the minimal stubs they compile against, for the three-state review-log
read. Write the tests and the stubs only -- **do not implement the real behaviour**; task 02 does that.

Create `tests/Charter.Server.Tests/ReviewLogUnknownStateTests.cs`, class **`ReviewLogUnknownStateTests`**,
carrying `[Trait("Category", "ReviewLogUnknownState")]` -- the per-class trait convention every other file
in that project follows. Pin that exact class name: this pair's guardrail filters use it.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Server.Tests/ReviewLogUnknownStateTests.cs` and `src/Charter.Server/ReviewLogStore.cs` and `src/Charter.Server/ReviewLogPaths.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside those paths -- including other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file -- write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

### The defect these tests are about

Read `src/Charter.Server/ReviewLogStore.cs` and `src/Charter.Server/ReviewLogPaths.cs` before writing
anything. `Read` returns `ReviewLogRead.Empty` when `EnumerateLogs` finds nothing, and `EnumerateLogs`
returns an empty array when the directory does not exist -- so an absent `.review/` and a plan nobody has
commented on produce the **identical** value, with empty comments and empty `Unreadable`. That is the
conflation, and `ReviewLogRead.Empty`'s own doc comment states it outright.

Follow the shape `ProbeResult` already uses in this same project for the same class of bug (#217): three
outcomes, and an `IsAbsent`-style property that exists so no caller reaches for the negation of another.
Grep `src/Charter.Server/ProbeResult.cs` and copy its naming discipline.

### The behaviours to encode -- one test each, PINNED to these exact method names

| behaviour | test method name |
|---|---|
| a directory with logs reads as present, with the folded comments | `A_directory_with_logs_reads_as_present` |
| a directory that EXISTS and holds no logs reads as EMPTY | `An_existing_directory_with_no_logs_reads_as_empty` |
| a directory that does NOT exist reads as UNKNOWN, not empty | `A_missing_directory_reads_as_unknown_not_empty` |
| Unknown and Empty are distinguishable by a caller | `Unknown_and_empty_are_distinguishable_by_a_caller` |
| a transient failure inside the bound still returns the real answer | `A_transient_failure_inside_the_retry_bound_still_reads_present` |
| the retry is BOUNDED -- a permanently missing directory settles as Unknown | `A_permanently_missing_directory_settles_as_unknown_within_the_bound` |

The last two are the bounded-retry decision. The bound is part of the deliverable: assert the read
**returns**, so an unbounded retry fails this test rather than hanging the suite.

### Stubs

The tests must **COMPILE and FAIL**. Add only the minimal skeleton the tests need -- the new outcome
member and any signature change -- with bodies that throw `NotImplementedException` or return a default.
Failing is intentional; not compiling is a mistake to fix. Do NOT implement the real read.

Do not add `[Fact(Skip = "...")]` to anything: a skipped test reads as not-executed and is reported as an
unbound behaviour.
