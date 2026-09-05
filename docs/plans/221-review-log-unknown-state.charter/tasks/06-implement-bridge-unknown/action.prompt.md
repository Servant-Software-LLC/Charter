## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `06-implement-bridge-unknown`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-implement-bridge-unknown": { "someKey": "someValue" } }`.
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

Surface the Unknown outcome in the review-log view, and stop `FindComment` reporting not-found on an
unread directory, so the tests authored by `05-author-tests-bridge-unknown` pass.

Edit `src/Charter.Server/ReviewLogBridge.cs` and `src/Charter.Server/ReviewLogView.cs`.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Server/ReviewLogBridge.cs` and `src/Charter.Server/ReviewLogView.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside those paths -- including other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file -- write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop. **Do NOT edit the authored tests.** If a test is genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` and stop rather than changing it.

### Notes

- The view is what the browser SDK reads, so whatever you add here is the contract task 08 consumes.
  Keep it small and explicit: a single outcome field the client can branch on.
- `ReviewLogView.Build` already receives the `ReviewLogRead`, so the outcome needs no new plumbing to
  reach it. Grep the signature before assuming otherwise.
- A present or empty read must serve exactly what it serves today. This adds a state; it does not
  reshape the existing ones.
