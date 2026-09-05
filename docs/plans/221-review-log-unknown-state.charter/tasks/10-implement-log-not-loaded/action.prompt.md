## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `10-implement-log-not-loaded`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "10-implement-log-not-loaded": { "someKey": "someValue" } }`.
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

Give `state.log` a **loaded / not-loaded** distinction on the client, so a render landing before the first
review-log load declines instead of reading the initial empty log as a real answer. Make the tests
authored by `09-author-tests-log-not-loaded` pass.

Edit `sdk/charter-annotate.js`.

**Scope boundary (harness-enforced):** Write only to `sdk/charter-annotate.js`. The harness runs a
`git diff` check after this task and rejects any edit outside that path - including the renderer, the
exporter and `src/Charter.Core/assets/charter.css`. **Do NOT edit the authored tests**; if a test is
genuinely wrong, escalate `{"needsHuman": "..."}` and stop.

### What is wrong

`state.log` is initialised at line ~92 to a **literal empty log**. `hydrateLog()` (line ~832) is the only
thing that ever assigns a real one, and it is called **fire-and-forget** from the SSE handler at line
~4436. Between the `review-log-changed` emit and the `review-log-loaded` that follows it, any render sees
an empty entry set that means *"not asked yet"* and treats it as *"the reviewer wrote nothing"*.

This is the **third state, one layer over** from the one tasks 01-06 added to the server - and it is the
one issue #221's captured trace actually exhibits.

### The precedent to copy, and the shape of the fix

`hydrate()` a little above already carries the #209 write-clock guard and reports its decline **out loud**
via `emit('list-loaded', { ..., stale: true })`. That structural fact is exactly what stops the branch
rotting into one nothing proves was taken. Do the same here: make the unloaded state observable, and emit
the decline rather than returning silently.

`restoreChromeFocus` must not report a miss it cannot attribute. The #236 fix already separated *"the
counterpart was not built"* from *"it was built and would not take focus"*; an unloaded log is a **third**
reason, and it must not borrow either sentence.

### What must not change

- A **loaded and genuinely empty** log must still render as empty. A plan nobody has commented on is a
  real state, and declining everything passes the wrong tests.
- The annotation-queue path is out of scope - #209 already guards it with its own write clock.
- Task 08's Unknown-view decline stays as it is. This is a different state with a different cause, and
  collapsing the two would lose the distinction #221 needs.
- Serve-time SDK chrome only: the exported artifact stays byte-identical.
