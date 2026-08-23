# The machine consumer — strict handoff and the record contract

**Status:** design of record · **Rev 2**, 2026-08-23 (§9 added: the answers file is no longer trusted, and
`answered` no longer counts elements) · Rev 1, 2026-08-22 (adversarial pass applied — three of the first four
decisions were reversed)
**Closes:** #172 (`charter handoff` has no strict mode) · #173 (the `.headless.json` contract, and two
naming collisions) · **#186** (an `--answers` file overwrites or erases a recorded answer with no validation)
· **#188** (`answered` counts array elements, not content)
**Filed in support of:** Guardrails #496 — an epic to drive a complete Charter → Guardrails pipeline with
no human in the loop, and prove afterwards that the run was proper rather than merely green.
**Split out, deliberately NOT closed here:** #187 (the chain-of-custody manifest).
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
- **The answer merge is shared too.** One function is called by the gate *and* by
  `HandoffMarkdown.EmitQuestion`. A gate that computed "answered" differently from the emitter would certify
  a document other than the one written — the same failure, one level up. In Rev 1 this was
  `HandoffGate.ResolvedAnswer` and it preserved today's behaviour verbatim, #186 included; **Rev 2 moved it to
  `AnswerRules.Merge`** (§9.4) and the one-place change is what #186 then cost.

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

- No chain-of-custody manifest (**#187**). Its in-band half is §5.2; the manifest is not.
- `charter headless` still has no `--answers`, so the record still describes *the plan on disk* while the
  handoff describes *the plan plus a file* (**#187** again).

*(Rev 1 also listed #186 and #188 here. Rev 2 closes both — see §9.)*

---

# Rev 2 — the answers file is an assertion, not an instruction

## 9. What Rev 1 left trusted, and should not have

Rev 1 put the answer merge behind one function and preserved its behaviour **verbatim**, on the stated
grounds that fixing it would then be a one-place change. This is that change. Two defects, one code path,
one absence: **nothing validated what an answers file contained, and nothing checked that an answer was more
than an array with something in it.**

Reproduced on the released 0.24.0, on a plan whose `db` question carries `options: ["Postgres","MySQL"]` and
a human's inline `answer: ["Postgres"]`:

| Invocation | 0.24.0 emitted | Exit |
|---|---|---|
| `--answers '{"db":["Cassandra"]}'` | `Answered: Cassandra`, with `options: Postgres, MySQL` on the next line | `0` |
| `--answers '{"db":[]}'` (and `null`) | `> **Open question (unresolved):**` — the human's decision gone | `0` |
| `--answers '{"db":[""]}'` | `Answered:` with nothing after it — **even under `--fail-if-needs-human`** | `0` |

### 9.1 An answers file may FILL, never REPLACE (the load-bearing decision)

**An `--answers` entry may settle a question the plan left open, and may re-state an `answer` the plan
already records — verbatim. It may never replace one.**

The alternative on the table was #187's: allow the override and **record its source** (`inline` vs
`answers-file`) in a chain-of-custody manifest. That was rejected as the fix, and the reasoning matters
because #187 is still open and should build on this rather than re-propose it:

- **Recording makes an override auditable, not safe.** The flattened plan still asserts `Cassandra`,
  `plan-breakdown` still reads it as settled, and the audit lives in a **side file the consumer may never
  open** — precisely the out-of-band-signalling failure §5.2 exists to fight. *An audit trail is the right
  answer for something you must allow; it is the wrong answer for something you should not do.*
- **The living-document model is the thing being protected.** Its whole claim is that a resolved question
  carries its answer inline and that answer is DURABLE. A channel that can quietly replace it makes "durable"
  false, and no amount of recording makes it true again.
- **Refusing costs a caller nothing they cannot recover.** The honest way to change a recorded decision is to
  change it where it lives: re-answer in review and fold it in with `poll --apply` / `resolve` (atomic,
  staleness-checked, duplicate-id-refusing), or edit the `.charter.md`.

**Re-stating an identical value is accepted**, and that clause is what keeps the rule usable: a generator
that supplies its whole answer set on every run must not break the day one of those questions gets answered
inline. So among the values that **pass**, an answers file only ever ADDS information — the accepted set is
monotone, and safe to apply twice.

**That monotonicity is a property of the accepted values, not a licence covering the whole input space**, and
§9.2 is exactly where the difference bites: `[]` "adds nothing", so under monotonicity alone it would be a
harmless no-op — which is what a reader who has just internalised the word will predict, and they will be
wrong. §9.2 is a **second rule from a second argument**, not a corollary of this one.

**What #187 should take from this.** `answerSource` remains worth recording; it is now a fact with exactly
two values (`inline`, `answers-file`) and an invariant — **it can never mean "the file overrode the plan"**.
That is a strictly simpler thing to record than the three-way "which one won" it would have been.

**But not for the reason first given, and the withdrawn reading must not propagate into #187.** It was argued
that the field distinguishes *a human decided this* from *the automation supplied this*. **It cannot.**
`inline` conflates at least three origins — a human's review answer folded in by `poll --apply` / `resolve`,
the drafting agent's own edit of the `.charter.md`, and anything else that wrote the key — and `handoff` does
not read the review log at all, so it holds no evidence about who decided anything. What the field actually
carries is **which hash reproduces this decision**: `inline` means `planSha256` covers it; `answers-file`
means reproducing it also needs `answersSha256`. That is a claim the producer can support, and it is the one
a chain-of-custody manifest needs.

### 9.2 An empty or null value is an ERROR, not an erasure

**A different argument from §9.1's, and it must not be read as following from it.** §9.1 is about
**authority** — what an answers file may do to a decision that already exists. This is about the
**exhaustiveness of the vocabulary** — whether an explicit `[]` has anything left to say. Both rules are
right; neither derives from the other, and §9.1's monotonicity would in fact predict the opposite here.

It has nothing left to say. "This question was not answered here" is **already** expressible by omitting the
id, and omission has a defined meaning (fall back to the inline answer, else open). Every reading `[]` could
carry is therefore already spoken for, and the only behaviour left to give it — erasure — is exactly what let
a generator delete a decision it could not make itself. **A spelling with no meaning left is an error, not a
no-op**: accepting it silently would tell a caller their key did something when it did nothing.
`ReadAnswers` maps JSON `null` to an empty array, so both spellings land on the same rule.

### 9.3 A violation fails the RUN, at exit 1, with nothing written

**Every violation is named on stderr, `charter handoff` exits `1`, and no handoff is written.** Four
reasons, in the order they decided it:

1. **It is a bad invocation, not a plan defect.** An unreadable or unparseable `--answers` file has always
   exited `1` here with nothing written. A file that parses as JSON but asserts a value the plan's own schema
   forbids is the same class — it is not a valid answers file *for this plan*. Drawing the line at *"is it
   syntactically JSON"* would be arbitrary.
2. **It does not invert the `2` vocabulary** (§2.1). Every `2` in this pipeline means *the output exists, go
   read it*. `1` **promises nothing** about the output — `unattended.md` documents it as "the files **may**
   not exist" — which is why nothing is being redefined by writing nothing under it. Note what that does NOT
   license: a consumer must not branch on a no-write *guarantee* at `1`, because across Charter's verbs there
   is none. Charter writes nothing on this path; the exit code is not the thing that says so.
3. **Every "write it anyway" variant produces a document that silently differs from the resolution the caller
   asked for**, with the difference living only on stderr. That is the out-of-band failure §5.2 exists to
   fight, one level up.
4. **Whole-run, not per-question.** A per-question skip yields a partially-applied handoff — internally
   consistent, `0`-able without the flag, and indistinguishable from a complete one to a consumer that only
   reads the file.

**The residual hazard, stated rather than hidden.** A refusal leaves a **previous** run's `plan.md` in place
(§2.1's objection to fail-closed), and the `plan-sha256` stamp **cannot** expose it: same plan, different
answers file, same hash. Closing that needs a hash of the *answers* beside it — which is exactly #187's
`answersSha256`, and is the second thing this decision hands that issue.

**It composes with `--fail-if-needs-human` by dominating it:** the check runs before the write and before the
gate, so a rejected value can never be certified. And in-library, a rejected entry falls back to the inline
answer rather than winning, so a direct caller of `HandoffGate.Evaluate` sees the question as **still open**
and **blocks** — a rejected answer can never pass the gate, however it reaches it.

### 9.4 The rules, and the two apparent asymmetries they dissolve

`AnswerRules` (Charter.Core) is the one place: `IsDecision` (what counts as an answer at all) and
`Merge`/`Check` (what an answers file may do). `Merge` replaces `HandoffGate.ResolvedAnswer` because the
moment *"the dictionary wins"* became *"the dictionary wins IF it passes the rules"*, the rules became part
of the merge — and the gate/emitter agreement §4 depends on is only provable if both live in one file.

The table below is a **design record, not a normative source** (invariant 3). The normative homes are
`charter-format` for the question schema and `AnswerRules` for the code. Editing this table does not change a
rule; change it there, then update this.

| Mode | Arity | Value rule |
|---|---|---|
| `single` | exactly one | must be one of `options` |
| `multi` | one or more | every value must be in `options` |
| `bool` | exactly one | `true` or `false` |
| `number` | exactly one | parses as a number (invariant culture) |
| `free-text` | exactly one | **shape only** — no `options` exist to test against |

Plus, for every mode: no blank values (§9.5), and no replacing a recorded answer (§9.1).

**The two are NOT the same kind of thing**, and a reader hunting for one unifying rule behind them will not
find it. They sit on different axes: Asymmetry 2 is about the **question's declared schema** — how much shape
there is to check at all; Asymmetry 1 is about the **answer's provenance** — who supplied the value. Each
dissolves under its own principle, and neither dissolves under the other's.

**Asymmetry 1 — an INLINE `answer` is still never checked against `options`.** The principle:

> **Validation is a function of WHO SUPPLIED the value, not of WHERE IT LANDS.** A human at a review page
> holds the authority to exceed the declared options; an invocation does not.

Stated that way there is no asymmetry — **one rule, two suppliers**. The write-in is the mechanism, not the
reason: the renderer offers it (#109) because the agent authoring the options is the party least qualified to
know they are exhaustive, so a reviewer's departure from the framing is a real decision and `charter-format`
rightly forbids validating it away. An `--answers` file has no human behind it and no such authority; the
flatten already instructs a delegated agent to *choose one of the options above*. An agent that genuinely
needs a write-in records it inline, where every other decision lives.

Stating it as a principle rather than as the write-in's story is what makes it **generalise**: the next
channel someone adds — a `headless --answers`, an API POST, a manifest replay — takes its rule from who is
behind it, without re-arguing this section. Both halves are stated in `charter-format`, because the
contradiction is the first thing a reader will think they have found.

**Asymmetry 2 — `free-text` can only be checked for shape.** It declares no options, so there is no set to
test a value against. That is not a hole: the rule is *a supplied answer must be something the question's
DECLARED shape can accept*, and `free-text` declares less shape than `single`. Naming it is the point —
an unnamed asymmetry is the one a future reader "fixes" by inventing a rule for free-text.

### 9.5 `answered` means a DECISION, and rides the unreleased schema 2

`HeadlessQuestion.Answered` was `Answer.Count > 0` — elements, not content. It is now
`AnswerRules.IsDecision`: **at least one value, none of them blank.** A blank value ANYWHERE disqualifies the
array, because a `multi` answer carrying a blank element is a defective answer, not a partial one — the blank
came from the same producer as the real values.

The predicate is read in **four** places and all four now share it: the record's `answered`, the flatten's
Answered/Open branch, the rendered page's `data-answered` / "Answered" status, and the missing-lean lint.
A field read in several places that means several things is the same defect one level up.

**Why no `schema` 3.** The rule first, because the next person to need it will arrive with a different field:

> **A schema number that has never appeared in a released binary is still malleable. The moment it ships, it
> freezes.** Reusing an unshipped number is not a licence to change a meaning under a *released* one — that is
> the exact mistake #142 made, shipping `recommended` with the number left at 1.

Changing what `answered` means flips a consumer's assertion, which is a bump by `HeadlessRecord.Schema`'s own
rule. But `schema` 2 has not shipped: it was raised from 1 by #173 *after* 0.24.0 was released — **verified,
not assumed**, by reading the release commit's own `HeadlessRecord.cs`, which carries `Schema = 1`. No
consumer has ever seen a schema-2 record, so there is nothing in the wild to break and both meaning changes
ride the same 2.

**The precondition is one a CONSUMER cannot check.** Only Charter can see which schema numbers reached a
release; from outside, a schema-2 record whose meaning changed is indistinguishable from one whose meaning did
not. A rule whose safety rests on a fact only the producer can observe has to be exercised **conservatively
and recorded** — which is what this section and `unattended.md`'s note are for. Verify it the way it was
verified here, against the release commit, and write down that you did.

**The drift test was binding NAMES, not meanings**, and #188 proves that is not enough: `answered` changed
meaning while every assertion in `HeadlessRecordContractTests` stayed green, because they all check that the
token appears. It now also binds the one meaning that moved — the doc must carry the `[""]` case, and the
record must actually report it unanswered, in the same test.
