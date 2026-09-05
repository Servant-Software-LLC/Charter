## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `03-author-tests-drain-unknown`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-author-tests-drain-unknown": { "someKey": "someValue" } }`.
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

Author **failing** tests that `charter poll` reports **exit 4** -- the existing "drain state UNKNOWN"
code -- rather than exit 2 ("a queue was found and it was EMPTY") when the review log cannot be read.

Create `tests/Charter.Cli.Tests/ReviewLogDrainUnknownTests.cs`, class **`ReviewLogDrainUnknownTests`**,
carrying `[Trait("Category", "ReviewLogDrainUnknown")]` -- the per-class trait convention that project
uses. Pin that class name; this pair's filters use it.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Cli.Tests/ReviewLogDrainUnknownTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside those paths -- including other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file -- write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

### Why this consumer matters most

Grep `src/Charter.Server/ReviewLogDrain.cs` for `ReviewLogStore.Read` and read what happens to
`read.State.Comments`. An empty read makes `fresh` empty and the drain reports a clean, confident
**exit 2** -- so an agent draining a review round is told **the reviewer said nothing**. That is silent
and wrong, where the panel's version of this bug is merely visible and transient.

Exit 4 already exists and is already documented as *"the drain could not complete, so the queue state is
UNKNOWN -- never read this as 'nothing queued'"*. Grep `src/Charter.Cli/ReviewExitCodes.cs` to confirm
the constant and its name before relying on it. This plan makes that code **reachable** for this cause;
it does not redefine it.

### The behaviours to encode -- one test each, PINNED to these exact method names

| behaviour | test method name |
|---|---|
| an unreadable review log makes poll exit 4, not 2 | `An_unknown_review_log_exits_four_not_two` |
| a genuinely empty review log still exits 2 | `A_genuinely_empty_review_log_still_exits_two` |
| the stderr on exit 4 says unknown, not that nothing was queued | `The_unknown_exit_says_unknown_not_nothing_queued` |

The middle row is the discriminator and is why this is not a one-line change: exit 2 must survive for the
case it was always right about.

The types these tests reference exist -- task 02 has landed -- so they must **COMPILE and FAIL** because
the drain does not translate Unknown yet. Do not add `[Fact(Skip = "...")]` to anything.
