# The machine consumer — strict handoff and the record contract

**Status:** design of record · **Rev 3**, 2026-08-23 (§10 added: the chain-of-custody manifest, and the
in-band stamp gains a second line) · Rev 2, 2026-08-23 (§9 added: the answers file is no longer trusted, and
`answered` no longer counts elements) · Rev 1, 2026-08-22 (adversarial pass applied — three of the first four
decisions were reversed)
**Closes:** #172 (`charter handoff` has no strict mode) · #173 (the `.headless.json` contract, and two
naming collisions) · **#186** (an `--answers` file overwrites or erases a recorded answer with no validation)
· **#188** (`answered` counts array elements, not content) · **#187** (no verb produces the handoff CommonMark
and a record of what was decided from one resolution pass)
**Filed in support of:** Guardrails #496 — an epic to drive a complete Charter → Guardrails pipeline with
no human in the loop, and prove afterwards that the run was proper rather than merely green.
**Filed reciprocally:** Guardrails #500 (nothing branches on `target`) · Guardrails #505 (nothing downstream
records the source plan's hash, so `handoffSha256` has no consumer).

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

*(Rev 3 gives it a second line, `<!-- charter: answers-sha256=<hex|none> -->`, immediately above it. One
stamp identifies the plan; the pair identifies the **resolution inputs**. See §10.5 for the reproduction that
forced it, and why the plan hash alone is not enough.)*

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

*(Rev 1 also listed #186 and #188 here. Rev 2 closes both — see §9. **Rev 3 closes #187 — see §10 — and the
second bullet stands: `headless` is still not given `--answers`, deliberately. §10.0 says why.**)*

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

---

# Rev 3 — the chain-of-custody manifest

## 10. One resolution pass, two artifacts

Rev 1 and Rev 2 made `charter handoff` **judge** correctly. They did not make it **testify**. The flattened
plan asserts a set of resolved decisions; nothing beside it records which inputs produced them, so the
question *"was every question answered before handoff, and by what"* had to be answered from
`<plan>.headless.json` — a file built by a **different verb**, from a **different resolution** (the plan on
disk, with no `--answers` merged at all). Both files exit `0`, both are internally consistent, and #187
reproduced them disagreeing on the same plan in the same session: the record said `"answered": true,
"answer": ["Postgres"]` while the handoff said `Answered: Cassandra`.

`charter handoff` therefore gains a boolean **`--manifest`**, which writes `<out-stem>.manifest.json` — a
**new artifact, with its own `schema 1` and its own drift test** — from the **same resolution pass** that
writes the CommonMark. `charter headless` is not touched at all.

### 10.0 The four things this deliberately is not

Each was considered and rejected; each is what a future reader will re-propose.

**Not `headless --answers`.** Everything `headless` produces is *by contract* a pure function of the plan
text — `HeadlessRecord.Build` has no answers parameter, and the artifact it writes is pinned byte-identical to
`charter export`'s (invariant 1). Feeding it answers forces a choice between breaking that pinned test and
having one verb emit two outputs that contradict each other. Worse, **`headless`'s needs-human is a strictly
WEAKER predicate than the gate's** (§4.1 — the gate also blocks an undecidable agent question and an unknown
`:::foo`), so a `headless --answers` would look like the pipeline gate while testing a different question.
Guardrails #496's harness asserts `headless` exits `0` today; giving it `--answers` would make that assertion
*look* correct while still testing the wrong predicate.

**Not a third verb.** Charter already has one silent wrong-verb failure — reach for `headless` when you
wanted `handoff` and you get exit `0` with no `plan.md` (§7). A third name in the same neighbourhood buys a
second.

**Not a merged verb, no rename, no exit-code renumbering.** §7's reasoning is unchanged: `headless`'s `0`/`2`
contract is documented, tested, and being written against now.

**Not a `HeadlessRecord` emitted from `handoff`.** Three of its fields are wrong on this path and #187 names
them: `artifact` has no value (emitting `null` retypes an always-string field, a schema bump by the record's
own rule), `sourceMap` and `questions[].sourceLine` map to the `.charter.md` whose line numbers bear no
relation to the flattened output, and `anchorId` appears nowhere in the flattened markdown. A consumer would
join against the wrong file. That rejection is what §10.1's *deliberately absent* list preserves.

### 10.0.1 `--manifest` is BOOLEAN, not a path

`HeadlessCommand`'s own reasoning decides it: that verb exists partly for *"a path convention a harness can
compute from the plan path alone — `export` requires you to name `-o`, which means telling the harness where
to look."* The manifest's name is therefore **derived from `--out`**: `-o plan.md` ⇒ `plan.manifest.json`,
`-o ../gr/plan.md` ⇒ `../gr/plan.manifest.json`. A derived name that would collide with the plan or with
`--out` is **refused (exit 1)**, exactly as `HeadlessCommand` refuses its own.

### 10.0.2 Neither flag implies the other, in either direction

`--fail-if-needs-human` does **not** turn the manifest on: it would write an unbidden file beside a plan,
which plan-03 §5.0 solo primacy forbids ("no trace where nothing was said"). `--manifest` does **not** turn
the gate on: it would change an exit code as a side effect of asking for a file, which is the seam-level
surprise §2.1 exists to prevent. The manifest records `gate.flagPassed` precisely so the two stay separable —
the gate is *always evaluated* when a manifest is written, and `flagPassed: false` records that its verdict
was reported to the file and **not** to `$?`.

### 10.1 Contents

**Stable core — a machine may assert on this:** `schema` · `charterVersion` · `planSha256` ·
`answersSha256` · `handoffSha256` · `malformedQuestions` · `gate.{flagPassed, needsHuman, exitCode}` ·
`gate.unmatchedAnswerIds` · `questions[].{id, answered, answer, answerSource}`, and **`questions[]` is
document-ordered**.

**Explicitly NOT contract:** the three file-**name** fields (`plan`, `answers`, `handoff`) ·
`questions[].title` · `gate.blockers[]` ordering · JSON key order.

The names deserve their own sentence, because they are the fields a reader reaches for first and the ones
that mean least. `-o ../gr/plan.md` records `"handoff": "plan.md"` — the bare name, never a path (the same
no-local-path guarantee the record and the artifact keep) — and **effectively every Guardrails handoff is
named `plan.md`**. **The hashes are the join key; the names are decoration.**

**Deliberately absent, and this is load-bearing: no `artifact`, no `sourceMap`, no `anchorId`.** The
governing rule, which has a negative test of its own:

> **Every line number in the manifest is a line in `plan`, and the manifest carries no map into the handoff
> output at all.**

Those three fields are exactly why emitting a `HeadlessRecord` from `handoff` was rejected (§10.0). A future
"helpful" addition of any of them reintroduces the wrong-file join this artifact exists to avoid.

**`gate.blockers[].detail` is NOT serialized.** `HandoffGate.cs` documents it as *"Not a contract; do not
parse it"*; putting it in a versioned schema makes it a de-facto contract the first time a harness greps it.
`kind` carries the wire meaning, and `id`/`title`/`target`/`sourceLine` carry the rest.

### 10.2 `answerSource` — what it can and cannot say

**Two values: `inline` | `answers-file`.** #186 shipped **refusal** of the override, so `overrodeInline` and
`clearedInline` are *impossible* — they are not omitted for brevity, they cannot occur. **Do not add them:
a field that can never be true is worse than an absent one**, because a consumer will write an assertion
against it and read the permanent `false` as evidence.

**It does NOT distinguish "a human decided this" from "the automation supplied this."** §9.1 records the
withdrawal of that first reading, and it is repeated here so it cannot propagate back in: `inline` conflates
a reviewer's answer folded in by `poll --apply` / `resolve`, the drafting agent's own edit of the
`.charter.md`, and anything else that wrote the key — and **`handoff` never reads the review log**, so it
holds no evidence about who decided anything.

What it carries is **which hash reproduces this decision**: `inline` ⇒ `planSha256` covers it; `answers-file`
⇒ reproducing it also needs `answersSha256`. It is therefore defined **mechanically — which input the merge
took the value from** — so it cannot drift from the emitter. In code it is not a second reader of the same
inputs: the classification asks `AnswerRules.Merge` *what it just did* (identity against the merge's own
return value), immediately beside the merge call in `HandoffGate.Evaluate`, and the flatten calls that same
`Merge`. A re-stated-verbatim value therefore reads `answers-file`, because that is where the merge took it
from, even though both hashes would reproduce it.

**The limit this implies, stated so nobody oversells the artifact:** *the manifest can certify where a
decision lived, never that a human made it.*

### 10.3 Absence semantics

Stated because *empty*, *not applicable* and *too old to know* look identical on the wire — the same reason
§6 states them for the record.

- **`answers: null` + `answersSha256: null` ⇒ no `--answers` was passed.** That is **not** the same as an
  empty answers file, which is a file and hashes to the hash of its text (`{}` → a real hex).
- **`questions[]` counts READABLE questions.** A `:::question` whose body will not parse is not in it — it
  appears only as a `malformed-question` blocker with `id: null`, because Charter has no id to give it.
  **`malformedQuestions` is emitted so `> 0` is a one-field detection**, since otherwise a plan with a broken
  question yields a `questions[]` that looks complete and entirely answered while the handoff has DELETED
  that question's id, title and target from the document the manifest is vouching for.
- **`questions[]` is not a map.** Duplicate ids are a gate blocker, but the plan still has two entries and
  both are emitted; `sourceLine` tells them apart. (Permitted by the rule in §10.1: it is a line in `plan`.)
- **`gate.blockers: []` genuinely means nothing blocks.** Nothing here is computed conditionally — unlike the
  record's old `notes: []` (§6.1), which meant "nothing of a kind Charter had" while two lints went to stderr
  with no note kind.
- **`answerSource: null` on an unanswered question.** There is no value, so there is no source; it is not a
  third token.

### 10.4 No clock

The manifest is a **pure function of (plan text, answers text, the `--fail-if-needs-human` flag,
`charterVersion`, three file names)**. No timestamp, no local path, no random. Two runs are byte-identical,
which makes **reproducibility itself assertable** — a harness diffs two runs rather than trusting a sentence.
The "when" is the file's own mtime, exactly as for the headless record.

### 10.5 The in-band stamp gains a second line

`<!-- charter: answers-sha256=<hex|none> -->` is emitted beside the existing `plan-sha256` line, immediately
above it — so the flattened plan still *ends* with the plan hash, which §5.2 and `references/handoff.md`
already promise.

**The hazard, reproduced before it was fixed.** Run once with `--answers v1.json --manifest
--fail-if-needs-human` (exit `0`), then re-run as plain `charter handoff plan.charter.md -o plan.md`. The
write is unconditional, so `plan.md` becomes the all-questions-open flatten; **no manifest is written, because
it is opt-in**; the OLD manifest survives — and `planSha256`, the in-band plan stamp, `.headless.json`'s
`planSha256` and `charterVersion` **all four match**. The result is a manifest certifying decisions that are
not in the file beside it, with **every documented join green**.

The second line makes that visible **from the two artifacts alone**: the manifest says a hex, the file says
`none`. It is necessary AND sufficient — the hazard bites only when the resolution **inputs** differ, and the
pair of stamps is exactly the inputs. It is also **CRLF-immune**, which the `handoffSha256` byte-hash is not:
a line-ending rewrite in transit invalidates the byte hash while leaving both stamps readable and correct.

This changes #172's stamp, which is **unreleased** — cheap now, expensive after the release.

### 10.6 The hash recipe — document it literally, it is not what a reader assumes

**One recipe for all three hashes**, because a manifest whose fields mean different things is worse than one
with fewer fields:

> `File.ReadAllText` strips a UTF-8 BOM and decodes UTF-16 per the BOM; `PlanHash.Sha256Hex` then hashes the
> **UTF-8 re-encoding of that decoded string**.

So **none of the three hashes equals `sha256sum` of the file's bytes unless the file is BOM-less UTF-8.** For
the plan and the handoff that is nearly always true (Charter writes the handoff itself). For the answers file
it is not: a pipeline generating `answers.json` from **Windows PowerShell 5.1** gets UTF-16LE, and every
comparison against `sha256sum` then mismatches permanently with nothing to explain it. `charter handoff`
therefore **warns on stderr when the answers file is not BOM-less UTF-8**, naming what it found. A warning,
not a rejection: the file decodes correctly and the run is honest — only the hash's relationship to
`sha256sum` is surprising.

`PlanHash`'s own remarks carry this, since it is now the identity function for **any** file in this pipeline
rather than for the plan alone.

### 10.7 The drift test binds MEANINGS, not names

`HandoffManifestContractTests` follows the **fixed** template, not the original. #186/#188 proved the
original insufficient: `HeadlessRecordContractTests` bound field NAMES only, so `answered` changed meaning
while every assertion stayed green (§9.5). **A drift test that pins names is a spell-checker.**

Three meanings are bound behaviourally as well as documentally, chosen because each is silently changeable
and each would break a reproduction check with nobody at fault:

1. **`answerSource`'s invariant that `answers-file` can never mean override** — bound as *the refusal*, not
   as the token. A supplied value that differs from a recorded inline answer is rejected (#186), the merge
   keeps the inline value, and the manifest says `inline`.
2. **What `handoffSha256` is computed over** — the bytes written **including** both provenance stamps, not
   the text before them.
3. **What `answersSha256` covers** — the answers file's own text, **not** a canonicalized dictionary. Two
   JSON texts that parse to the same dictionary hash differently, deliberately.

Note that `answered` means something **narrower here than in the record**, and that is the fourth meaning the
test binds: the record's `answered` is *the plan's own inline answer records a decision*, while the
manifest's is *the MERGED answer records a decision*. One field name, two artifacts, two scopes — which is
precisely the shape of defect #188 was, so it is asserted rather than assumed.

`QuestionBodyParityTests` is extended, and is the highest-value test in the change. There is a real seam:
`HandoffMarkdown.Emit` **normalizes line endings and then parses**, while `HandoffGate` →
`PlanInventory.Build` → `PlanWalk.Blocks` parses the **raw** string. The manifest is the first artifact that
vouches for the flattened file while being assembled from a *different parse of a different string*. The
extension asserts **behaviourally** — the manifest's `questions[]` id set equals the ids the flatten actually
emitted, and each entry's `answer` equals what the flatten printed, on the same inputs — without asserting
the two call the same method. A **CR-only** row and rows that pass an answers dictionary were added, because
neither existed.

### 10.8 Known limits — stated, not fixed

- **`handoffSha256` has no consumer.** Guardrails' own `PlanHash` hashes `guardrails.json` plus every
  `task.json`; it does **not** hash the markdown the folder was broken down from, and nothing there records
  the source plan's hash. Filed as **Guardrails #505**. The field is kept as the only tamper detector Charter
  can offer, and is documented as **advisory**: a mismatch means either tampering **or a line-ending rewrite
  in transit**, and those are not distinguishable from the hash alone — which is the other half of why
  §10.5's stamps matter.
- **Whether the plan was ever reviewed by a human is unknowable here.** `handoff` does not read the review
  log at all. §10.2's limit is the same fact from the other end.
- **`gate.flagPassed` records the argv, not obedience.** It says `--fail-if-needs-human` was on the command
  line; it says nothing about whether the caller honoured the exit code. That is why the field is named
  `flagPassed` and **not** `enforced`.
- **Write order is handoff FIRST, then manifest**, and the exit-`1` help text was corrected to match: with
  `--manifest`, a `1` no longer promises that *nothing* was written. A handoff with no manifest is an honest
  degraded state; a manifest describing a file that does not exist is a lie. Both are written
  temp-file-then-`File.Move(overwrite: true)`, so neither is ever observed half-written.
