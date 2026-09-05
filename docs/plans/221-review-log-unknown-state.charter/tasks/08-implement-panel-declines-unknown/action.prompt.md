## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `08-implement-panel-declines-unknown`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-implement-panel-declines-unknown": { "someKey": "someValue" } }`.
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

Make `hydrateLog()` **decline** an Unknown review-log view instead of assigning it over known-good
state, so the tests authored by `07-author-tests-panel-declines-unknown` pass.

Edit `sdk/charter-annotate.js`.

**Scope boundary (harness-enforced):** Write only to `sdk/charter-annotate.js`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside those paths -- including other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file -- write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop. **Do NOT edit the authored tests.** If a test is genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` and stop rather than changing it.

### Where, and the precedent to copy

Grep for `function hydrateLog` and read it next to `function hydrate` a little above it. `hydrate` is the
annotation-queue read and it **already has this guard**, added for #209: it takes
`var taken = state.queueWrites` before the fetch and, when the response lands, declines with
`emit('list-loaded', { ..., stale: true })` if the page has written past it. Its comment explains the
trade. `hydrateLog` has no equivalent -- it assigns `state.log` unconditionally and then renders, which
is what empties the panel and drops focus to `<body>`.

**Decline out loud.** #209's `stale: true` is a structural fact a test can assert, which is the whole
reason that guard cannot rot into a branch nothing proves was taken. Emit the equivalent here rather
than silently returning.

### What must not change

- A genuinely **empty** view must still apply. A plan whose comments were all retracted, or one nobody
  has commented on, is a real state the panel must be able to show -- declining everything passes the
  wrong tests.
- The annotation-queue path is out of scope: #209 already guards it with its own write clock.
- This is serve-time SDK chrome only. `src/Charter.Core/assets/charter.css`, the renderer and the exporter are untouched,
  and the exported artifact stays byte-identical.
