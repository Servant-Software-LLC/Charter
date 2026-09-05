## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-author-tests-expand-affordance`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-author-tests-expand-affordance": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "01-author-tests-expand-affordance": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author **failing** browser tests for a new review-time affordance: a **full-screen expand mode**
for an oversized `:::diagram` block (Charter #234). Write the tests only — **do not implement the
feature**; a later task does that.

Create exactly one file:

**`tests/Charter.Browser.Tests/DiagramExpandAffordanceTests.cs`**

**Scope boundary (harness-enforced):** Write only to
`tests/Charter.Browser.Tests/DiagramExpandAffordanceTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path — including changes to
`sdk/charter-annotate.js`, other test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

### The shape this project requires (read before writing a line)

Every file in `tests/Charter.Browser.Tests/` declares **`public sealed partial class
ReviewLoopBrowserTests`** — they are partials of one class that owns the shared Playwright harness.
Your file must do the same. Grep `tests/Charter.Browser.Tests/MarkerAffordanceTests.cs` for the
declaration and copy its shape.

**Every test method you write MUST carry `[Trait("Feature", "DiagramExpandAffordance")]`** in
addition to the usual `[SkippableFact]`. That trait is how this task pair's guardrails select your
tests: `Category=BrowserAcceptance` already sits on the class and selects all 86 browser tests, so it
cannot discriminate. A method without the `Feature` trait is invisible to the guardrails and will be
reported as an unbound behaviour.

### The behaviours to encode — one test each, PINNED to these exact method names

The guardrail reads a per-test census keyed on these names. Use them verbatim.

| behaviour | test method name |
|---|---|
| the zoom bar carries an expand control with an accessible name | `An_oversized_diagram_offers_an_expand_control` |
| activating it expands that diagram to fill the viewport | `Expanding_a_diagram_fills_the_viewport` |
| activating it again restores the diagram's original box | `Expanding_then_collapsing_restores_the_original_box` |
| Escape collapses the expanded view | `Escape_collapses_the_expanded_diagram` |
| the expand control is reachable and operable from the keyboard | `The_expand_control_is_reachable_by_keyboard` |
| the zoom hint names expand while the diagram is wider than its column | `The_zoom_hint_names_expand_when_the_diagram_is_too_wide` |
| expanding hides no existing chrome (no `display: none`) | `Expanding_hides_no_existing_chrome` |

The last one is load-bearing and is a settled decision of the plan, not a nicety: the expanded view
must paint **over** the page, never remove anything from layout. Assert it by reading the computed
`display` of the review panel (and the page body's own content region) while expanded and requiring
that none of them is `none`. `getComputedStyle` on a `display: none` element still returns a real
style object, so read the property, do not infer from a box measurement.

### How these tests must be written (each of these has cost this repo a defect)

- **Assert the POSTED PAYLOAD, not just the DOM,** wherever a test drives a control that submits.
  Clicking the real control and asserting what reached the server is the standard here.
- **Do NOT use `WaitForFunctionAsync`.** The served page's CSP has no `'unsafe-eval'`, so Playwright's
  polling predicate throws `EvalError` the moment it genuinely has to wait — it only appears to work
  when the condition is already true on the first check. Use `WaitForSelectorAsync` or the harness's
  bounded C# poll over `EvaluateAsync`.
- **`WaitForEventAsync` asks "has this EVER happened"**, so the second one in a test returns
  instantly. Use `WaitForEventCountAsync` / `SaveComposerAsync` where the harness provides them.
- **Anything measuring a scroll affordance needs `TryLaunchAsync(showScrollbars: true)`** — Playwright
  passes `--hide-scrollbars` to headless Chromium by default, so a scrollbar measurement otherwise
  measures the flag and passes while proving nothing.
- **Re-resolve every element AFTER the last `await`** and keep the measurement synchronous: a render
  sweeps the SDK's chrome away and rebuilds it, so an element handle captured before an `await` can
  be detached (it reports a `0x0` rect at the origin and an empty computed style).
- **`document.elementFromPoint` is viewport-relative and `scrollIntoView` scrolls every scrollable
  ancestor.** Scroll once, before either reading, and never again between two readings.

### What "failing" means here

There is no expand control in `sdk/charter-annotate.js` yet, so every test above must **compile** and
**fail at runtime** — the control is absent, so locating or activating it fails. Compiling is
required; failing is intentional. If a test of yours passes on the current code, it is asserting
something that is already true and does not encode the behaviour — rewrite it to drive the real
control.

Do **not** add `[Fact(Skip = "...")]` to anything. A skipped test reads as "not executed" to the
census and is reported as an unbound behaviour.
