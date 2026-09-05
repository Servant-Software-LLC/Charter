## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `04-implement-expand-invariants`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04-implement-expand-invariants": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "04-implement-expand-invariants": { "someKey": "someValue" },
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

Make the expand-mode invariants hold, so the tests authored by
`03-author-tests-expand-invariants` pass. The expand affordance itself already landed in
`02-implement-expand-affordance`; this task is about everything that must keep working **while a
diagram is expanded**.

Edit exactly one file: **`sdk/charter-annotate.js`**.

**Scope boundary (harness-enforced):** Write only to `sdk/charter-annotate.js`. After this task
completes, the harness runs a `git diff` check and rejects any edit outside that path — including
`assets/charter.css`, the renderer, the exporter, and **either test file**. An out-of-scope edit fails
the task immediately and consumes a retry. **Do NOT edit the authored tests.** If a test is genuinely
wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path and stop.

### Read the expand implementation before you change it

`02-implement-expand-affordance` has already merged, so the expand control and the expanded container
exist in this file. **Read what it actually built** rather than assuming a shape — grep for the
`---- :::diagram pan/zoom (Charter #51)` section marker and for the expand control the sibling task
added. Do not rely on line numbers; this file moves.

Run `git log`/`git diff` against the prior task's commit if you want to see exactly what changed —
read-only git inspection is available to you.

### The settled decision that constrains every fix here

`sequencing-vs-221` = `Build now, with a hard rule that expand hides no existing chrome`

No `display: none` on the expand path — not on the panel, not on the composer, not on the page
behind. Charter #221 is an undiagnosed focus defect whose leading hypothesis is that focus into a
`display: none` subtree silently does nothing, and this feature must not manufacture more of that
condition. If the only way you can see to make a test pass is to hide something, halt with
`{"needsHuman": …}` instead.

### What is likely to need work, and what is likely already free

Charter #51 chose to widen the `<svg>` rather than use a CSS transform, so hit-testing,
`getBoundingClientRect()`, arrow-key panning and the annotation overlay work in the page's own
coordinate frame and mostly do not care that the container grew. Expect zoom, pan and per-node
annotation to need little or nothing. Two things genuinely change:

- **`pinDiagramChrome`** pushes absolutely-positioned chrome back by the container's
  `scrollLeft`/`scrollTop`. A `position: fixed` container establishes a different containing block,
  so re-derive the offset rather than assuming the existing arithmetic still holds. `renderMarkers`
  calls `pinDiagramChrome` for every diagram after it rebuilds badges, so both the scroll path and
  the render path have to be right.
- **Focus.** The render path ends in `restoreChromeFocus`, and the expand control is chrome that a
  render can rebuild. The anti-steal test asserts a render does **not** move focus away from an
  unrelated control the render did not rebuild — so whatever you do about restoring focus must be
  conditional on the reviewer's focus actually having been in the rebuilt chrome, the way
  `disableChrome` already is.

If a test passes without you changing anything, that is a legitimate outcome — say so in your
summary. Do not manufacture a change to look busy, and do not weaken a test that is already green.

### What must NOT change

The exported artifact stays byte-identical: `charter.css`, `CharterRenderer` and `ArtifactExporter`
are untouched. `DiagramPanZoomArtifactTests` runs as one of this task's guardrails and enforces it.
