## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `03-author-tests-expand-invariants`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "03-author-tests-expand-invariants": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "03-author-tests-expand-invariants": { "someKey": "someValue" },
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

Author **failing** browser tests for the invariants that must survive the new expand mode: the
things a reviewer already has, which must keep working once one diagram fills the viewport. Write
the tests only — **do not implement anything**; a later task does that.

Create exactly one file:

**`tests/Charter.Browser.Tests/DiagramExpandInvariantTests.cs`**

**Scope boundary (harness-enforced):** Write only to
`tests/Charter.Browser.Tests/DiagramExpandInvariantTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path — including `sdk/charter-annotate.js`,
the sibling `DiagramExpandAffordanceTests.cs`, and the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

### The shape this project requires

Same as the sibling task: your file declares **`public sealed partial class ReviewLoopBrowserTests`**
(grep `tests/Charter.Browser.Tests/RerenderFocusTests.cs` for the shape), and **every test method
carries `[Trait("Feature", "DiagramExpandInvariants")]`** alongside `[SkippableFact]`. That per-pair
trait is the only selector that can discriminate this pair — `Category=BrowserAcceptance` sits on the
shared partial class and selects all 86 browser tests.

Note the trait value differs from the sibling task's (`DiagramExpandInvariants`, not
`DiagramExpandAffordance`). Using the wrong one silently folds your tests into the other pair's
guardrails.

### The behaviours to encode — one test each, PINNED to these exact method names

| behaviour | test method name |
|---|---|
| zoom still works while the diagram is expanded | `Zoom_still_works_while_the_diagram_is_expanded` |
| pan still works while the diagram is expanded | `Pan_still_works_while_the_diagram_is_expanded` |
| the zoom bar stays pinned while panning expanded | `The_zoom_bar_stays_pinned_while_panning_expanded` |
| Alt+click a node while expanded posts THAT node's sub-anchor | `Annotating_a_node_while_expanded_posts_that_nodes_anchor` |
| the expanded view survives a render() caused by a teammate's record | `The_expanded_view_survives_a_render_from_a_teammate_record` |
| that render does not steal focus from an unrelated control | `A_render_while_expanded_does_not_steal_focus` |

### Notes that decide whether each test proves anything

- **The annotation test must assert the POSTED PAYLOAD** — that the record which reached the server
  carries that specific diagram node's sub-anchor. Asserting that a composer opened proves nothing;
  a whole-block annotation opens the same composer. This is Charter #48 through the back door and it
  fails silently.
- **The re-entrancy test must cause the render the way the product causes it** — append a second
  author's record to `<plan>.review/` while the server is running and wait for it with the harness's
  `WaitForSelectorWhileTouchingAsync`. Do **not** call `render()` or `renderMarkers()` from the test:
  that asserts the browser, not the product. Because `render()` is one synchronous turn, a selector
  that has appeared is proof the decision has already been made — no extra wait is needed or
  trustworthy.
- **The anti-steal test is not optional and is not a duplicate.** Focus something the render does not
  rebuild (a `.table-scroll` region, or the open composer), trigger the same teammate-record render,
  and assert focus did **not** move. Without that half, "always restore focus" passes everything.
- **Re-resolve every element after the last `await`.** `renderMarkers` opens with `clearMarkers()`,
  which removes every badge and rail from the document, so a handle captured before an `await` can be
  detached — it reports a `0x0` rect at the origin and an **empty** computed style (which is how you
  tell detachment from `display: none`, which keeps a real style).
- **Do NOT use `WaitForFunctionAsync`** (the served page's CSP has no `'unsafe-eval'`, so its polling
  predicate throws the moment it genuinely has to wait) and **do not reuse `WaitForEventAsync` twice**
  in one test (it asks "has this ever happened", so the second call returns instantly). Use
  `WaitForSelectorAsync` and `WaitForEventCountAsync`.
- Any test that measures a scrollbar or a scroll affordance needs
  `TryLaunchAsync(showScrollbars: true)` — Playwright passes `--hide-scrollbars` to headless Chromium
  by default, so the measurement otherwise measures the flag.

### What "failing" means here

Your tests run against a tree where **no expand control exists yet** (the sibling implementation task
has not merged). Every test above therefore fails at the point it tries to open the expanded view —
that is a genuine red, and it is red for the right reason: none of these invariants can hold for a
state the page cannot enter.

If any test of yours passes on the current tree, it is not driving the expanded view — rewrite it. Do
not add `[Fact(Skip = "...")]` to anything; a skipped test reads as "not executed" and is reported as
an unbound behaviour.
