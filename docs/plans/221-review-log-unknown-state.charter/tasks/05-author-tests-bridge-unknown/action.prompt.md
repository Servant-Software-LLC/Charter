## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `05-author-tests-bridge-unknown`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-author-tests-bridge-unknown": { "someKey": "someValue" } }`.
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

Author **failing** tests for the two review-log bridge consumers: the view served on `/api/review-log`,
and `FindComment`.

Create `tests/Charter.Server.Tests/BridgeUnknownTests.cs`, class **`BridgeUnknownTests`**, carrying
`[Trait("Category", "BridgeUnknown")]`. Pin that class name; this pair's filters use it.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Server.Tests/BridgeUnknownTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside those paths -- including other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file -- write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

### The two consumers

Grep `src/Charter.Server/ReviewLogBridge.cs` for `ReviewLogStore.Read` -- it appears more than once, and
each call is a separate consumer. Establish the real set yourself rather than assuming two.
`ReviewLogView.Build` takes the `ReviewLogRead` directly, so the outcome is already in scope at that call
site; another reads `.State.Comments` to find a comment by id.

### The behaviours to encode -- one test each, PINNED to these exact method names

| behaviour | test method name |
|---|---|
| the view served to the panel carries the Unknown outcome | `The_view_carries_the_unknown_outcome` |
| a present read still serves its comments unchanged | `A_present_read_still_serves_its_comments` |
| FindComment does not report not-found on an unread directory | `FindComment_does_not_report_not_found_on_an_unread_directory` |
| FindComment still reports not-found for a genuinely absent id | `FindComment_still_reports_not_found_for_a_genuinely_absent_id` |

The two "still" rows are the discriminators: this change must not turn a correct not-found, or a correct
empty view, into an error. Without them the fix could pass by treating everything as Unknown.

The types exist -- task 02 has landed -- so these must **COMPILE and FAIL**. Do not add
`[Fact(Skip = "...")]` to anything.
