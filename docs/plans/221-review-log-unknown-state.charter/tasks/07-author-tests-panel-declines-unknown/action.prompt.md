## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `07-author-tests-panel-declines-unknown`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-author-tests-panel-declines-unknown": { "someKey": "someValue" } }`.
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

Author the **deterministic browser reproduction** of #221: an Unknown review-log view must not empty the
panel or drop the reviewer's keyboard focus.

Create `tests/Charter.Browser.Tests/ReviewLogUnknownPanelTests.cs`. Every TEST file in that project declares
**`public sealed partial class ReviewLoopBrowserTests`** -- yours must too. Grep
`tests/Charter.Browser.Tests/StaleQueueReadTests.cs` for the shape.

**Every test method must carry `[Trait("Feature", "ReviewLogUnknownPanel")]`.** That per-pair trait is
the only selector that can discriminate this pair: `Category=BrowserAcceptance` sits on the shared
partial class and selects every browser test in the project, so a class-name filter here matches nothing.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Browser.Tests/ReviewLogUnknownPanelTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside those paths -- including other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file -- write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

### The technique -- reuse the one that already works

`tests/Charter.Browser.Tests/StaleQueueReadTests.cs` holds `HoldFirstQueueReadAsync`, the route-boundary
intercept #209 used: it holds the page's own read at the Playwright route boundary, lets the REAL server
answer it, and delivers that genuine response at a chosen moment -- every byte the server's, only the
timing the test's. Read it and build the same shape for `/api/review-log`. That is what made #209's race
reproduce identically on every runner and both engines.

### The behaviours to encode -- one test each, PINNED to these exact method names

| behaviour | test method name |
|---|---|
| an Unknown view does not empty a populated panel | `An_unknown_view_does_not_empty_a_populated_panel` |
| an Unknown view does not drop the reviewer's focus to body | `An_unknown_view_does_not_drop_focus_to_body` |
| the decline is reported out loud, not silently | `The_declined_unknown_view_is_reported_out_loud` |
| a genuinely empty view DOES empty the panel | `A_genuinely_empty_view_still_empties_the_panel` |

The last row is the anti-tautology control and is not optional: without it, "never apply a view" passes
every other row. #209's own guard reports `stale: true` as a structural fact a test can assert, precisely
so the branch cannot rot into one nothing proves was taken -- assert the equivalent here, and assert it
AFTER the symptom, so the pre-fix failure is about the vanished panel rather than about the fix's own
instrumentation.

### Traps in this project that have cost real defects

- **Do NOT use `WaitForFunctionAsync`.** The served page's CSP has no `unsafe-eval`, so its polling
  predicate throws the moment it genuinely has to wait -- it only appears to work when the condition is
  already true on the first check.
- **`WaitForEventAsync` asks "has this EVER happened"**, so the second call in a test returns instantly.
  Use `WaitForEventCountAsync`.
- **Re-resolve every element AFTER the last `await`** and keep the measurement synchronous: a render
  sweeps the SDK's chrome away and rebuilds it, so a handle captured earlier can be detached -- it
  reports a 0x0 rect at the origin and an EMPTY computed style, which is how detachment is told apart
  from `display: none`.

These must **COMPILE and FAIL** -- the SDK applies an Unknown view unconditionally today. Do not add
`[Fact(Skip = "...")]` to anything.
