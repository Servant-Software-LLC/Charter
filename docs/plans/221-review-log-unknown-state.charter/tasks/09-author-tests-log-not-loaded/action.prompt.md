## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `09-author-tests-log-not-loaded`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-author-tests-log-not-loaded": { "someKey": "someValue" } }`.
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

Author the **deterministic reproduction of what issue #221 actually is**. Tasks 01-08 fixed a real but
DIFFERENT defect - the server conflating an absent `.review/` with an empty one. This pair fixes the race
#221's own captured trace shows, which lives entirely on the client and needs no server response at all.

Create `tests/Charter.Browser.Tests/ReviewLogNotLoadedTests.cs`. Every TEST file in that project declares
**`public sealed partial class ReviewLoopBrowserTests`** - yours must too.

**Every test method must carry `[Trait("Feature", "ReviewLogNotLoaded")]`**, and the trait must appear on
each METHOD and nowhere above the class declaration. A guardrail rejects a class-level one: an attribute
on any partial declaration applies to the whole type, which would widen this pair's filter from 4 tests to
every browser test in the project. Tests here are spelled **`[SkippableFact]`**, not `[Fact]`.

**Scope boundary (harness-enforced):** Write only to
`tests/Charter.Browser.Tests/ReviewLogNotLoadedTests.cs`. The harness runs a `git diff` check after this
task and rejects any edit outside that path. An out-of-scope edit fails the task and consumes a retry. If
a compile error names a file you may not write, do NOT edit it - escalate `{"needsHuman": "..."}` and stop.

### The defect, and why it is not the one task 08 fixed

Read these two lines of `sdk/charter-annotate.js` before writing anything:

- **line ~92** - `state.log` is initialised to
  `{ comments: [], diagnostics: [], unreadable: [], selfEmail: null }`. That is a **literal empty log**,
  byte-identical to one the server returned empty. The client has **no way to say "not loaded yet"**.
- **line ~4436** - `es.addEventListener('review-log', function () { emit('review-log-changed', {}); hydrateLog(); });`
  The emit is synchronous; `hydrateLog()` is **fire-and-forget** - nothing awaits it.

So a render triggered by that SSE event runs against the **initial** `state.log` while the fetch is still
in flight, sees zero entries, and `restoreChromeFocus` reports the reviewer's note gone. Issue #221's own
captured event wire shows exactly this ordering:

```
review-log-changed -> markers-rendered -> focus-restored ->
review-log-loaded  -> review-log-changed -> markers-rendered -> panel-opened ->
focus-not-restored -> review-log-loaded  -> ...
```

`focus-not-restored` fires **before** the `review-log-loaded` that would have filled the list, with
`items=0` and `panelHidden=false`. Task 08's fix declines an **Unknown response**; here there is **no
response yet to decline**, which is why that fix does not reach this case.

### The technique - the same one that already works

`tests/Charter.Browser.Tests/StaleQueueReadTests.cs` holds `HoldFirstQueueReadAsync`: it holds the page's
own read at the Playwright route boundary, lets the **real server** answer, and delivers that genuine
response at a chosen moment. Build the same shape for `/api/review-log` so you can force a render into the
window between `review-log-changed` and `review-log-loaded`.

**Do not fabricate the response body.** A guardrail rejects `FulfillAsync` with a literal `Body =`: a
fabricated body cannot see whether the server and the SDK agree on their JSON property names, so it would
pass against a server that never emits the field. Fulfil with `Response = response`.

### The behaviours to encode - one test each, PINNED to these exact method names

| behaviour | test method name |
|---|---|
| a render before the first load does not report a note gone | `A_render_before_the_first_log_load_does_not_report_a_note_gone` |
| focus is not reported unrestorable while the log is unloaded | `Focus_is_not_reported_unrestorable_while_the_log_is_unloaded` |
| the panel renders its entries once the log loads | `The_panel_renders_its_entries_once_the_log_loads` |
| a loaded and genuinely empty log still renders as empty | `A_loaded_and_genuinely_empty_log_still_renders_as_empty` |

The last row is the anti-tautology control and is **not optional**: without it, "never render anything"
passes every other row. A loaded-and-empty log is a real state the panel must still be able to show.

### Traps in this project that have cost real defects

- **Do NOT use `WaitForFunctionAsync`.** The served page's CSP has no `unsafe-eval`, so its polling
  predicate throws the moment it genuinely has to wait.
- **`WaitForEventAsync` asks "has this EVER happened"**, so the second call in a test returns instantly.
  Use `WaitForEventCountAsync`. Note both are **private static helpers on this partial class**, called
  undotted - a guardrail bans them in that form.
- **Re-resolve every element AFTER the last `await`** and keep the measurement synchronous: a render
  sweeps the SDK's chrome away and rebuilds it, so a handle captured earlier can be detached - it reports
  a 0x0 rect at the origin and an EMPTY computed style.

These must **COMPILE and FAIL**. Do not skip a test to get there.
