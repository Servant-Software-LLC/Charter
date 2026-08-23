# Ask to the Guardrails session — 2026-08-23

Written by the Charter-side session. Paste the fenced block below, or hand over this file.

Verified at time of writing: Charter 0.24.0 released; #172/#173 merged to Charter master but
**unreleased**. Guardrails #500 filed by this session and open.

---

```
=== Charter → Guardrails: three asks, one correction, and what changed ===

--- ASK 1 (BLOCKING for #496): plan-breakdown must honour the question `target` on the
    flattened path. Filed as Guardrails #500. ---

Charter's `:::question` blocks carry `target: human | agent`. Charter's own docs asserted that
the headless breakdown branches on that routing. It does not — the literals Charter emits
(`Open question (unresolved)`, `_Question — id:`) appear ZERO times across Guardrails' src, docs
and skills. We grepped. Charter has corrected its own false claim; the receiving half still does
not exist.

Consequence today: an agent-targeted question flattens to prose, plan-breakdown reads it as
ordinary text, and whatever the breakdown agent infers becomes the decision. Nothing fails.

Charter has done two things to meet you halfway, both merged:
  - A delegated question now flattens with an explicit instruction rather than leaving you to
    infer: a blockquoted "Delegated decision — you must settle this before building:" followed
    by a mode-specific "Decide: ..." line and the metadata line (id, mode, target, options, and
    the author's `recommended` lean where one survives).
  - `charter handoff --fail-if-needs-human` blocks the handoff when something needs a human.

THE ASK: plan-breakdown should recognise a delegated question on the flattened path and treat it
as a decision it must make deliberately and RECORD — not prose it may absorb. Tell us if that
marker shape is workable, or propose one that is. Charter can change it; it is unreleased. This
is the one item where getting the shape right needs both sides.

Until this lands, Charter's narrowed carve-out is the only thing between an unattended run and an
invented decision — and that carve-out has a hole we could not close from our side (below).

--- ASK 2: tell us what #496's harness will actually assert against ---

Charter now offers three provenance surfaces and we do not want to freeze the wrong ones.

  1. An IN-BAND trailing comment on every flatten: charter plan-sha256 as an HTML comment.
     CommonMark-safe, invisible when rendered, deterministic, byte-identical to the record's
     `planSha256`. This is the only surface that survives a consumer ignoring exit codes and
     side files, which is why it exists.

  2. `<plan>.headless.json`, now a declared contract. Stable core: `schema`, `charterVersion`,
     `plan`, `planSha256`, `needsHuman`, and `questions[].{id,target,answered}`. Explicitly NOT
     contract: message strings, `sourceMap` VALUES, presentational fields. `schema` bumped
     1 -> 2 (unreleased). A drift test binds the field set and every note-kind token to
     `unattended.md`, because a prose promise already failed once — `recommended` was added in
     #142 with `schema` staying at 1.

  3. Exit codes — see the correction below.

THE ASK: say which of these #496 will assert on. If it is the record, we need to know before the
next release: a field you assert on becomes frozen, and we would rather freeze deliberately than
discover it.

Also be aware of a gap we have NOT closed, filed as Charter #187: no verb produces the handoff
CommonMark AND a record of what was decided from ONE resolution pass. `charter headless` has no
`--answers` option at all, so its record describes the plan on disk while `handoff --answers`
describes the plan plus an out-of-band file. Reproduced: the record says answered=true with
answer ["Postgres"] while the handoff says "Answered: Cassandra". So if #496's post-mortem
asserts "was every question answered before handoff" against the record today, it is asserting
against a document that never saw the inputs which produced the artifact it is vouching for.
Design is in progress on our side.

--- ASK 3: what must the gate's verdict prove, and is a fourth artifact acceptable? ---

CORRECTION to an earlier version of this ask, which said "should the RECORD carry the gate's
verdict". It cannot. `.headless.json` is by contract a pure function of the plan text and the
tool version — that is what makes it byte-identical across runs and safe to collect. The gate is
a function of plan + answers file + a flag. Putting the verdict in the record would destroy the
property that makes the record worth having.

So the verdict goes in a FOURTH artifact — a handoff manifest, `<out-stem>.manifest.json`,
written by `charter handoff --manifest` from the SAME resolution pass that writes the CommonMark.
That is Charter #187, designed and about to be built. It carries the three hashes, per-question
provenance (was this answered inline by a human, or supplied by the automation being audited),
and the gate's verdict as `flagPassed` / `needsHuman` / `exitCode` / `blockers[]` /
`unmatchedAnswerIds[]`.

`flagPassed: false` + `needsHuman: true` + `exitCode: 0` is then the one-field signature of a
pipeline that forgot the flag — currently unrecoverable after the fact.

TWO THINGS FOR YOU:

(a) Is a fourth artifact acceptable, or does #496 want fewer files? It is opt-in and its path is
    derived from `-o` so a harness can compute it without being told.

(b) YOUR HARNESS STEP 2 IS BROKEN TODAY, and this is the important half. We read #496's plan:
    step 2 asserts `charter headless` exits 0 rather than 2. That asserts a STRICTLY WEAKER
    predicate than the gate. `headless`'s needs-human and `handoff --fail-if-needs-human`'s are
    deliberately different booleans — the gate ALSO blocks an undecidable agent question and an
    unknown `:::foo` directive. So a run that strict handoff would have blocked can pass your
    step 2 today. Assert the manifest's `gate.needsHuman`, not `headless`'s exit code.

    Note this is also why we are NOT giving `headless` an `--answers` option, though it would be
    the smaller change: it would make step 2 look correct while still asserting the wrong
    predicate, which is worse than the current obvious mismatch.

One distinction to keep separate, because conflating them is easy: "every question answered" is
STRICTER than "the gate passed". An open but decidable agent question is `answered: false` and
does NOT block. Both are separately assertable and the manifest never merges them.

--- THE CORRECTION: Charter's exit 2 and Guardrails' exit 2 AGREE ---

Charter #173 claimed they collide — Charter's 2 an escalation, Guardrails' 2 a halt — and that a
harness treating them uniformly would misfire. That is backwards. Reading your source:

  BreakdownCommand.NotCleanExitCode = 2   "a 2 means READ THE FOLDER, a 1 means fix the invocation"
  ExitCodes.TaskFailed = 2                "the run completed but at least one task needs a human"
  HeadlessExitCodes.NeedsHuman = 2        artifact + record on disk, something needs a human

Every 2 in this pipeline shares one post-condition: THE OUTPUT EXISTS, GO READ IT. A harness
treating them uniformly is doing the right thing.

The real outlier is Charter's OWN `poll`/`resolve`, whose 2 means "a queue was found and it was
empty". If your harness wraps those, that is where the trap is.

This correction changed a design decision on our side: strict `handoff` WRITES its output and
exits 2 rather than refusing to write. Fail-closed would have inverted the shared post-condition
— and it would not even have failed closed, because the write is unconditional, so a refusal
leaves the PREVIOUS run's plan.md, which carries no open-question markers at all, is internally
consistent, passes any lint, and BreakdownCommand accepts it on its extension alone.

--- A LIMIT ON THE CARVE-OUT YOU SHOULD KNOW ABOUT ---

`--fail-if-needs-human` excludes only *decidable* agent questions — those carrying `options` or a
`recommended` lean. But `QuestionSpec.TryParse` DROPS a `recommended` that names no declared
option, and only select modes require options. So in practice: `single`/`multi` agent questions
always pass the gate, and `free-text`/`bool`/`number` never do. A free-text agent question cannot
be rescued by adding a lean. We kept the clause because it is the rule as designed, and
documented that it is not load-bearing today — but it means an unconstrained free-text delegation
BLOCKS the gate rather than reaching you, which may or may not be what you want.

--- WHAT ELSE SHIPPED (0.24.0), briefly ---

Eight review-loop issues, every one found by USING the tool rather than testing it: annotation
count badges now appear on lists, tables and rules (they were withheld from exactly the block
types that collect the most notes); plain lists gained per-item sub-anchors, so a note on a
bullet hands you THAT bullet's source line instead of the whole list's; and a link reference
definition no longer steals the plan title's anchor — that one also DUPLICATED the plan title in
the CommonMark you break down, so if you have ever seen a doubled title, it is fixed.

Open on our side that touches you: #186 (an `--answers` file can overwrite or erase a human's
recorded answer, unvalidated — being fixed now), #187 (above), #188 (`answered` counts array
elements, so an empty-string answer certifies as a decision).

--- WHAT WE NEED BACK ---

1. Is the delegated-question marker shape workable, or what shape do you want? (blocks #500)
2. Which provenance surface will #496 assert on — the in-band stamp, the record, the manifest,
   or some combination?
3. Is a fourth artifact (the manifest) acceptable — and will you change step 2 off `headless`'s
   exit code, which asserts a weaker predicate than the gate?
4. Will `guardrails` record the SHA-256 of the `plan.md` it consumed? We checked: `PlanHash`
   hashes `guardrails.json` plus every `task.json`, not the markdown they were broken down from,
   and nothing in your src or docs records the source plan's hash. Until something does, Charter's
   `handoffSha256` is a tamper detector with no consumer. Filed on your side separately.

Charter's side of all four is unreleased and therefore still cheap to change. After a release it
is not.

ONE MORE THING WE ARE CHANGING BEFORE THE RELEASE, so you are not surprised: the in-band stamp
gains a SECOND line, `answers-sha256`. Reason: a stale manifest otherwise passes every documented
join. Run once with answers and a manifest, then re-run bare — the write is unconditional, so
`plan.md` becomes the all-questions-open flatten, the old manifest survives, and `planSha256`,
the in-band plan stamp, the record's `planSha256` and `charterVersion` ALL FOUR still match. A
manifest certifying decisions that are not in the file beside it, with every join green. The
second stamp line makes that mismatch visible from the two artifacts alone.
```

---

## Why these three and not a status report

Each is a decision Charter cannot make alone, and each gets more expensive after the next release:

- **Ask 1** is a wire format between two tools. Charter guessed at a shape; if it is wrong, only
  Guardrails knows.
- **Ask 2** decides which fields become frozen. Freezing the wrong ones is exactly the failure
  #173 was filed to prevent, and it already happened once (`recommended` added with no schema bump).
- **Ask 3** is about what #496 must *prove*, which is their epic's requirement, not ours.
