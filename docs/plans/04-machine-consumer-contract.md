# The machine consumer — strict handoff and the record contract

**Status:** design of record · **Rev 1**, 2026-08-22 (adversarial pass applied — three of the first four
decisions were reversed)
**Closes:** #172 (`charter handoff` has no strict mode) · #173 (the `.headless.json` contract, and two
naming collisions)
**Filed in support of:** Guardrails #496 — an epic to drive a complete Charter → Guardrails pipeline with
no human in the loop, and prove afterwards that the run was proper rather than merely green.
**Split out, deliberately NOT closed here:** #186 (an `--answers` file overwrites or erases a recorded
answer with no validation) · #187 (the chain-of-custody manifest) · #188 (`answered` counts array elements,
not content).
**Filed reciprocally:** Guardrails #500.

---

## 1. The problem, in one sentence

Charter has always been designed for a human reader; every place a *machine* consumes it — an exit code, a
JSON record, a flattened plan — was correct-by-accident rather than by contract, and three of those places
were quietly wrong.

Three symptoms, one shape:

1. **`charter handoff` cannot fail.** An unanswered `:::question` flattens as prose and exits `0`, so an
   unattended pipeline resolves a decision nobody made and reports success.
2. **`<plan>.headless.json` is an undeclared contract.** The moment #496's post-mortem harness asserts on
   it, its shape is a contract — an undeclared one, breakable in a patch release with nobody at fault.
3. **The flattened plan self-identifies as nothing.** No version, no hash, no link to its source, with a
   test pinning that front matter is stripped. *"Did the plan Charter recorded match the plan Guardrails
   consumed"* was unanswerable **in principle**.

## 2. What the adversarial pass reversed

Recording these because each reversal is load-bearing and the discarded option is the one a future reader
will re-propose.

### 2.1 Strict mode does NOT fail closed

**First design:** `--fail-if-needs-human` refuses to write the handoff.

**Reversed.** Every exit `2` in this pipeline shares one post-condition — *the output exists, go read it*:
`HeadlessExitCodes.NeedsHuman`, Guardrails' `BreakdownCommand.NotCleanExitCode` (commented "a 2 means READ
THE FOLDER") and Guardrails' `ExitCodes.TaskFailed` ("the run completed but at least one task needs a
human"). Inverting that at this seam is the exact class of surprise the flag exists to prevent.

It also **would not work**. The write at `CharterCommands.cs` is unconditional, so a refusal leaves the
**previous** run's `plan.md` in place — a stale flattened plan carrying no open-question markers at all,
internally consistent, passing any lint, and indistinguishable to a `BreakdownCommand` that only checks the
file extension. "Fail closed" would hand a downstream something it cannot tell is wrong. Pinned by
`HandoffStrictModeTests.ARefusalWouldLeaveAStalePlan_WhichIsWhyItDoesNotRefuse`.

### 2.2 #173's exit-code premise is backwards

**The issue says** Charter's `2` (an escalation) and Guardrails' `2` (a halt) disagree, and a harness
treating them uniformly will misfire.

**Reversed.** They agree — see the three constants above. **The outlier is Charter's own**:
`ReviewExitCodes.CleanEmpty = 2` means "a queue was found and it was empty", which is close to the
opposite. So the warning worth publishing is the inverse of the one asked for, and it is now one shared
constant (`CharterCommands.ExitCodeVocabularyNote`) appended to every verb help that mentions a `2`.

### 2.3 The `target: agent` carve-out is NARROWED, not adopted

**The issue proposes** exempting `target: agent` questions, "because the flattened path branches on
`human` vs `agent`".

**Reversed, because that claim is false.** The literals Charter emits (`Open question (unresolved)`,
`_Question — id`) appear **zero** times across Guardrails' `src`, `docs` and skills. Nothing branches on
`target`. Delegating is not a routing decision some downstream honours — it is **prose asking the next
agent to decide**.

So the exemption is narrowed to **decidable** agent questions: those carrying `options` or a `recommended`
value. A bare free-text agent question with no lean is invisible to *both* of Charter's existing gates
(`HeadlessRecord.NeedsHuman` skips agent questions outright; `FindQuestionsMissingRecommendation` skips
agent questions **and** is scoped to select modes), so Charter would certify "no human needed" while the
downstream invents an answer. That is not a delegation; it is an unconstrained invention.

The false claim itself was corrected at both copies — `skills/charter/references/handoff.md` and
`HandoffMarkdown.QuestionMetadataLine`'s remarks — to *"the routing signal, so that a consumer **can**
branch"*. Filed reciprocally as Guardrails #500; the Guardrails side is not ours to fix.

### 2.4 The record's version marker already exists, and had already failed

**First design:** write a prose stability section.

**Reversed.** `HeadlessRecord.Schema = 1` shipped documented as "bumped when the on-disk shape changes
incompatibly", and #142 added `recommended` with the number left at `1` — so there are records in the wild,
both `"schema": 1`, with different question shapes, while `unattended.md` still showed the pre-#142 example.
**A prose promise is exactly what failed.** The deliverable is a **drift test binding the record's field set
to its documentation** (`HeadlessRecordContractTests`), the same mechanism `charter-format`'s frontmatter
uses against `CharterFormat.Version` (#155).

## 3. Prerequisite: ONE question-body parse

**#172's design assumed the two verbs already shared a parse. They did not**, and the divergence went both
ways — verified against the real code, not inferred:

| Container shape | `charter handoff` | `charter headless` |
|---|---|---|
| Unterminated at EOF (LF or CRLF) | flattens perfectly | `malformed-question`, escalates |
| `::::question` (four-colon fence) | **deletes** the id, title and target behind `> **Malformed question …**` | reads it fine |
| CR-only line endings | flattens perfectly | `malformed-question` |

`HeadlessRecord` read bodies through `QuestionResolution.QuestionBody` → `TryLocateJsonBody`, which
**required** a closing `:::` fence and looked only for `\n`. `HandoffMarkdown` used its own private
`InnerLines`, which normalized line endings and tolerated a missing closing fence but recognised an opening
fence only as `^:::\w+`. So strict mode would have refused to write a handoff the emitter produces
perfectly — and, in the other direction, the record would certify a routable `target: human` question the
handoff had already reduced to a malformed-question line.

**The definition was single-sourced; the parse was not.** `QuestionResolution.QuestionBody` is now the ONE
definition, tolerating any fence length, any line ending, and an unterminated container at EOF — the three
shapes the *renderer* already accepts, so the parse agrees with what a reviewer sees on the page.
`TryLocateJsonBody` survives only as the span-based **write** twin (a splice needs indices, which cannot
survive normalization) and was widened to match, so a question the record can READ is now one whose answer
`resolve` will WRITE.

`QuestionBodyParityTests` pins it **behaviourally** — it asserts the two verbs reach the same verdict on the
same container, not that they call the same method, so a future re-fork fails however it is spelled.

## 4. `charter handoff --fail-if-needs-human`

- **Writes its output, exits `2`** (§2.1). `HeadlessExitCodes` is shared with `charter headless`, because
  the two verbs mean the same thing by a `2`.
- **Evaluated AFTER `--answers` is merged.** `HeadlessRecord.Build` has no answers parameter and its
  `NeedsHuman` is computed from inline `spec.Answer`, so this is new code at a new seam. `Build`'s signature
  and its "pure function of the plan text" contract are untouched.
- **The two predicates share an INVENTORY, not a verdict.** `PlanInventory.Build` is the one walk;
  `PlanInventory.NeedsHuman` is the record's rule and `HandoffGate.Evaluate` is the gate's. They are
  deliberately different booleans (§4.1), so safety comes from their never disagreeing about *which
  questions the plan has*, which a copied walk would eventually get wrong.
- **The answer merge is shared too.** `HandoffGate.ResolvedAnswer` is called by the gate *and* by
  `HandoffMarkdown.EmitQuestion`. A gate that computed "answered" differently from the emitter would certify
  a document other than the one written — the same failure, one level up. It preserves today's behaviour
  verbatim, #186 included, which makes #186 a one-place change rather than two.

### 4.1 Where the gate is stricter than the record, and why

| | record (`NeedsHuman`) | gate (`HandoffGate`) |
|---|---|---|
| open `target: human` | blocks | blocks |
| open `target: agent`, has options/lean | — | — |
| open `target: agent`, **no** options or lean | — | **blocks** |
| unparseable `:::question` body | blocks | blocks |
| **unknown `:::foo` directive** | note only | **blocks** |
| duplicate question ids | blocks | blocks |
| missing marker · missing `recommended` · untracked deferral | note only | — |

The record excludes `UnknownDirective` on the reasoning that widening the escalation rule would make the
flag almost always true. **That reasoning does not transfer to a flag whose whole purpose is strictness**: a
misspelled `:::questoin` classifies as an unknown directive, so under the record's rule a hidden
`target: human` decision exits `0` from *both* verbs (reproduced). Charter cannot tell a typo'd question
from a container the catalog genuinely does not define — the body is preserved as prose either way and
nothing interprets it — so the gate resolves the ambiguity **toward the human**, the same principle the
anchor model uses when it orphans rather than misattributing.

### 4.2 Unmatched `--answers` ids are reported, never a veto

Ids in the answers file matching no `:::question` are named on stderr. *"Your answers file had three ids and
none of them matched"* is a signal a pipeline needs — a stale id, a renamed question, or a generator written
against a different plan all looked identical, because Charter discarded them in silence. They do **not**
change the exit code: the questions they failed to answer already block on their own account, and a second
veto here would be a rule nothing else in the pipeline shares.

## 5. The flatten's two additions

### 5.1 A delegated question tells the agent what to do

An open `target: agent` question now leads with **"Delegated decision — you must settle this before
building:"** and carries a `_Decide: …_` instruction phrased for its mode, naming the author's lean where
there is one. On this path there is no parser; prose is the interface, and *"Open question (unresolved)"*
reads as something **someone else** will settle — exactly backwards for a block whose `target` says the
reader settles it. The metadata line is unchanged in shape. An **answered** agent question gains no
instruction.

### 5.2 The in-band provenance stamp

Every flattened plan ends with `<!-- charter: plan-sha256=<hex> -->`, hashing the plan text **exactly as
read** — byte-identical to `planSha256` in the same plan's headless record, which is the join.

Four properties earn it its place, and no out-of-band record has all four: CommonMark-safe, **invisible** in
a rendered diff, deterministic, and — the one that decides it — it **survives a consumer that ignores exit
codes and side files**. *Out-of-band signalling cannot fix a failure of out-of-band signalling.*

Charter's own renderer escapes it, because the pipeline sets `DisableHtml` so bare prose HTML can never run
(`:::custom-html` is the one sanctioned exception). That is the security posture working, and it is pinned
as a known consequence rather than left unasserted.

## 6. The record contract — `schema` 2

Declared in `skills/charter/references/unattended.md` and **bound to the code by
`HeadlessRecordContractTests`**, which holds the emitted field set and every `notes[].kind` token against
that file, and the documented `"schema"` against `HeadlessRecord.Schema`.

- **Stable core:** `schema` · `charterVersion` · `plan` · `planSha256` · `needsHuman` ·
  `questions[].{id, target, answered}`.
- **Explicitly not contract:** `message` strings · `sourceMap` **values** · `notes[]` ordering.
- **`sourceMap`:** the `{anchorId: line}` shape and ascending-line ordering are stable; the anchor id
  **values** are not — they moved **twice inside v0.24.0 alone** (#166, #171). Join
  `questions[].anchorId` to `sourceMap` **within one record**, never across versions.
- **Absence semantics are stated**, because *empty*, *not applicable* and *too old to know* look identical on
  the wire: `options: []` on bool/free-text means not-applicable; `recommended: null` is a declined lean
  while a **missing key** means the producer predates #142; `sourceLine: null` on a note means document-wide;
  `answered: false` means no inline answer, **not** "unresolved at handoff".
- **An unrecognised `kind` must be IGNORED, never rejected.** New kinds are the compatible change this
  contract absorbs; refusing the file over one turns a diagnostic into an outage.

### 6.1 The note set was fixed BEFORE the contract was declared

`notes: []` did **not** mean "Charter noticed nothing": `handoff` printed `WarnOnMissingRecommendation` and
`WarnOnUntrackedDeferrals` (#156) with no matching note kind, so *"did Charter raise diagnostics nobody
read"* — precisely #496's query — was silently unanswerable. `missing-recommendation` and
`untracked-deferral` now exist and neither raises `needsHuman`.

### 6.2 Why `schema` is 2

Adding a field is compatible and would not warrant a bump. Changing what `notes: []` **means** is not: a
consumer's `notes.length === 0` assertion flips. That is an incompatible change by the constant's own rule,
so the number moves — and 2 is also the first version whose shape is bound by a test rather than by a
sentence. Declaring the contract while violating its own versioning rule would have repeated #142.

### 6.3 `planFormatVersion` is a pair, not an int

`CharterFormat` returns a null version for a **missing** marker and for a present-but-non-integer one alike,
so `charter-format-version: 1.0` reads identically to an unstamped plan. The record emits
`{ status, marker, version }` — a status token plus the **raw declared value**.
`VersionMarkerResult.RawValue` was added to carry it.

## 7. Naming and help (the #173 half)

- **No rename of `headless`, no exit-code renumbering.** Its `0`/`2` contract is documented, tested, and
  #496 is being written against it now.
- **The disambiguation moved to where the confused reader is.** `references/handoff.md` called `handoff`
  "the headless half of Charter's dual handoff" with no disambiguation in that file, while the
  disambiguation lived in `unattended.md` — the file that reader never opens. An agent reaching for
  `charter headless` there gets no `plan.md` and exit `0`, so the pipeline reports success and Guardrails
  gets nothing (or a stale file). Both files now carry a pointer to the other.
- **`handoff --help` gains its exit codes; `resolve --help` gains the `0/2/3/4/5` it returns and documented
  nowhere.** Both end with the shared vocabulary note (§2.2).
- **The top-level banner gets a POINTER, never a table.** Charter's verbs do not share one exit-code
  vocabulary, so a banner table would have to be wrong or long enough to bury the command list.

## 8. What this does NOT do

- `Answered => Answer.Count > 0` still counts elements rather than content, so `[""]` certifies as a
  decision (**#188**). It changes a published field's meaning in three readers and is its own schema bump.
- An `--answers` file still overrides an inline answer unconditionally, with no validation against `mode` or
  `options`, and an empty value still re-opens a settled question (**#186**). Preserved verbatim, and now
  behind ONE shared function (§4).
- No chain-of-custody manifest (**#187**). Its in-band half is §5.2; the manifest is not.
- `charter headless` still has no `--answers`, so the record still describes *the plan on disk* while the
  handoff describes *the plan plus a file* (**#187** again).
