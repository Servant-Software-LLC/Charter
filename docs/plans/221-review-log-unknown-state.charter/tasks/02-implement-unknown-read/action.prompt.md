## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key -- the name of the directory this task.json lives in (e.g. `02-implement-unknown-read`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-implement-unknown-read": { "someKey": "someValue" } }`.
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

Implement the three-state review-log read so the tests authored by `01-author-tests-unknown-read` pass.
Fill real logic over the stubs.

Edit `src/Charter.Server/ReviewLogStore.cs` and `src/Charter.Server/ReviewLogPaths.cs`.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Server/ReviewLogStore.cs` and `src/Charter.Server/ReviewLogPaths.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside those paths -- including other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do
NOT edit that file -- write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop. **Do NOT edit the authored tests.** If a test is genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` and stop rather than changing it.

### What must be true

- An **absent** `.review/` directory reads as **Unknown**, never as Empty.
- A directory that **exists and holds no logs** still reads as **Empty**. A plan nobody has commented on
  is a normal state, not a failure -- and `.review/` is created lazily on the FIRST APPEND -- the only
  `Directory.CreateDirectory` for it in the whole tree sits in `ReviewLogWriter.cs`'s append path; grep it --
  so an absent directory is the *usual* state of a solo review. The common path must stay silent
  and cheap; do not turn it into a warning.
- A **short bounded retry** fronts the read, mirroring the per-file `TryReadAllText` already in this
  file. That helper retries precisely because a concurrent append or a `git checkout` creates brief
  sharing conflicts, and the directory-level read is inconsistent in not doing the same. **Bound it** --
  an unbounded retry hangs the panel instead of emptying it, which is worse than the bug being fixed.
- Name the property so the negation of another is not the reachable spelling. `ProbeResult.IsAbsent`
  exists in this project for exactly that reason (#217); grep it and follow it.

### What must NOT change

`ReviewLog.Fold` and the fold's semantics are out of scope, as is anything that would make
`charter review` create `.review/` eagerly -- that would break §5.0 of
`docs/plans/03-git-mediated-team-review.md` -- *"Solo is the primary use case and must not regress"*, whose
binding rules are no new required setup and no nagging.
