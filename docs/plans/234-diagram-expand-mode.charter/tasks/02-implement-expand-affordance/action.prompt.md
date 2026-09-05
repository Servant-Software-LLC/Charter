## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `02-implement-expand-affordance`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-implement-expand-affordance": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "02-implement-expand-affordance": { "someKey": "someValue" },
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

Implement full-screen **expand mode** for an oversized `:::diagram` block, in the annotation SDK
only, so the tests authored by `01-author-tests-expand-affordance` pass.

Edit exactly one file: **`sdk/charter-annotate.js`**.

**Scope boundary (harness-enforced):** Write only to `sdk/charter-annotate.js`. After this task
completes, the harness runs a `git diff` check and rejects any edit outside that path — including
`assets/charter.css`, `CharterRenderer`, `ArtifactExporter`, and **the test file**. An out-of-scope
edit fails the task immediately and consumes a retry. **Do NOT edit the authored tests.** If a test
is genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path and stop
rather than changing it.

### The settled decisions this task must honour

These were decided by the reviewer on the plan and are not open:

`expand-mechanism` = `In-place position: fixed on the existing block`
`sequencing-vs-221` = `Build now, with a hard rule that expand hides no existing chrome`
`expand-discovery` = `A button in the existing zoom bar` + `A hint in the zoom bar's hint text when the diagram is wider than the column`

Build against these. If one is wrong, halt with `{"needsHuman": …}` — never silently choose
differently. In particular:

- **In-place `position: fixed`. Do NOT reparent the block**, and do **NOT** use the native
  Fullscreen API (`requestFullscreen`). The reason the native API is disqualified is concrete: in
  fullscreen the browser renders only the fullscreen element's subtree, and the annotation composer
  is appended to `document.body` (see `openComposer`'s mount, around `document.body.appendChild(root)`),
  so `Alt`+click while expanded would open a composer that exists, takes focus, and is invisible.
- **Nothing may be `display: none` while expanded.** Paint over the page, dim it, make it inert if
  you need to — but never remove the panel, the composer, or the page content from layout. This is a
  hard rule, not a preference: Charter #221 is an undiagnosed focus defect whose leading hypothesis
  is that focus into a `display: none` subtree silently does nothing, and this feature must not
  manufacture more of that condition.
- **A keyboard shortcut is explicitly out of scope** as a discovery route. The button and the hint
  are the only ways in. Escape still *leaves* — that is an exit, not a discovery mechanism.

### Where this belongs in the file

The `:::diagram` pan/zoom implementation is Charter #51 and lives entirely in this file — grep for
the section marker `---- :::diagram pan/zoom (Charter #51)` rather than relying on a line number.
The pieces you will be working with (grep for each; do not trust a line number, this file moves):

- `activateDiagram` — builds the per-diagram view and calls `buildZoomBar`.
- `buildZoomBar` — constructs the `−` / `%` / `+` / `Reset` controls and the hint span. Your expand
  control belongs here, built with the same `make()` / `button()` helpers so it is SDK-owned chrome.
- `syncZoomBar` — currently sets the hint to one of two strings. Your third state goes here.
- `pinDiagramChrome` — pushes absolutely-positioned chrome back by `scrollLeft`/`scrollTop`.
- `DIAGRAM_ZOOM` — the frozen tuning constants.

**Why this is smaller than it looks.** #51 deliberately does not use a CSS transform: zooming widens
the `<svg>` itself and the block is an ordinary scroll container. There is no coordinate frame of
Charter's own, so hit-testing, `getBoundingClientRect()`, arrow-key panning and the annotation
overlay do not need to know the container grew. Do not introduce a transform-based approach.

### Requirements

1. An expand control in the zoom bar, built as SDK chrome (`make()`/`button()`, `data-charter-ui`,
   no `id`), with an accessible name, keyboard-operable like the other zoom-bar buttons.
   **Name the expanded-state class `charter-expand`** (`charter-expand-btn` for the control itself is
   fine — the union guardrail matches the `charter-expand` stem). This is a contract, not a style
   preference: the plan's terminal union invariant gates its contribution check on that token, so a
   differently-named class leaves that half of the gate permanently inert.
2. Activating it expands that one diagram to fill the viewport, in place, via `position: fixed`.
3. Activating it again — or pressing Escape — restores the diagram's original box. Escape is
   handled at the document level; the composer already handles Escape and calls
   `stopPropagation()`, so a composer open inside the expanded view swallows the first Escape and
   closes itself. That precedence is correct: do not fight it.
4. The zoom bar's hint gains a third state naming expand while the diagram is wider than its column.
   The hint is a single span already driven between two states in `syncZoomBar` — decide, and state
   in a comment, what wins when a diagram is both too wide **and** already zoomed.
5. Zoom, pan and the existing chrome pinning keep working while expanded.
6. No `display: none` anywhere on the expand path.

### What must NOT change

The exported artifact must stay byte-identical. `charter.css`, `CharterRenderer` and
`ArtifactExporter` are untouched — expand mode is review-time SDK chrome only, injected at serve
time, exactly like the pan/zoom it extends. `DiagramPanZoomArtifactTests` exists to enforce that
boundary and runs as one of this task's guardrails. If you find yourself wanting a `.charter-zoom*`
or `.charter-expand*` rule in `assets/charter.css`, you have broken the portability invariant —
put the style in this file's own serve-time stylesheet block instead.
