# Handoff — offline export and the Guardrails handoff

Once the plan is approved in the review loop, two commands finish the job: `charter export` captures a
shareable offline snapshot (optional), and `charter handoff` converts the plan into the plain CommonMark
that Guardrails `plan-breakdown` consumes (required to feed the pipeline).

## `charter export` — a self-contained, offline artifact

```
charter export plan.charter.md -o plan.html
```

`export` writes a **truly offline** HTML artifact:

- every **local asset** the plan references is inlined as a `data:` URI, so nothing loads from disk;
- any remaining **local path** is scrubbed (no `file://` or absolute paths leak into the file);
- it is **SDK-free** — like `render`, the review SDK is never baked in (invariant 1, *portable
  artifact*).

The difference from `render`: `render` produces a portable file but leaves local asset references as-is,
so it's the fast inner-loop check while you draft. `export` produces a file that survives being emailed,
attached to a ticket, or archived — it opens correctly on a machine that has never seen the plan's
directory. Use it when you need a snapshot of the approved plan to hand to a person, not to a tool.

## `charter handoff` — plain CommonMark for Guardrails

```
charter handoff plan.charter.md -o plan.md --answers answers.json
```

`handoff` reads the reviewed plan and rewrites **every `:::` directive** — `:::note`, `:::warn`,
`:::comparison`, `:::diagram`, `:::custom-html`, `:::question`, … — into **plain CommonMark**. This is the
**headless** half of Charter's dual handoff (invariant 5, *dual handoff to Guardrails*): the flattened
`plan.md` is what the autonomous Guardrails `plan-breakdown` path consumes. The **interactive**
`/plan-breakdown` doesn't need it — it reads the `.charter.md` directly, interpreting the `:::` blocks via
the `charter-format` skill. Reach for `handoff` when you're feeding the headless path.

> **"Headless" here does NOT mean the `charter headless` verb.** Two unrelated senses of the word live in
> this repo, and the one on this page is *which ingestion path Guardrails uses*. `charter headless` is *how
> Charter runs with no human present*: it writes an HTML artifact and a JSON forensic record, and **no
> CommonMark at all**. They meet nowhere in code — `headless` emits no handoff markdown, and `handoff`
> writes no record.
>
> Reaching for `charter headless` here is a silent pipeline failure, not an error: it writes no `plan.md`,
> exits `0` on a clean plan, and the run reports success while Guardrails gets nothing — or, worse, a
> **stale** `plan.md` from an earlier run. **The verb that feeds Guardrails is always `charter handoff`.**
> For an unattended run, that is `charter handoff --fail-if-needs-human` (below); `references/unattended.md`
> covers `charter headless`.

> **Guardrails compatibility.** Direct `.charter.md` ingestion (the interactive path) requires
> **Guardrails ≥ `1.0.0-preview.47`** — the release that implements it (Guardrails #390–393); current
> Guardrails is well past it. Against **any earlier Guardrails**, run `charter handoff` and feed the
> flattened `plan.md` instead: the flatten path has **no version floor** and is supported permanently. When
> unsure which the target Guardrails supports, `handoff` always works.
>
> **The reviewer may hand you this command themselves.** Once a round is settled, the review panel offers
> `/plan-breakdown "<plan>.charter.md"` to copy, with their own path already in it — so the natural end of a
> review is the human pasting that to you. Charter starts nothing; the page only writes the string.
> If a `charter poll --watch` of yours is still running, it must be stopped first, or their paste queues
> behind it, looks like nothing happened, and gets pasted a second time — and a second breakdown regenerates
> the task folder over any guardrail a human has edited.
>
> **And `charter skills install` must have been run.** Step 0c stops outright if it cannot load
> `charter-format` as a top-level skill — a plan it cannot interpret is one it refuses to guess at. That is
> the failure to check first when direct ingestion dead-ends on a machine that has the right Guardrails.

`--answers` is **optional**. A `:::question` already resolved **inline** — its `answer` filled in by
`charter poll --apply` / `charter resolve` during review — hands off as **Answered** on its own, because
`handoff` reads the inline answer. `--answers` is for questions **not** already answered inline: supply it
to resolve them. Omit it, and any question with no inline answer hands off as an **open question** — a
legitimate, common case when the human hasn't decided yet.

### What an `--answers` entry may do — and what it may not

**It may only ADD information.** It fills a question the plan left open, and it may re-state a recorded
answer verbatim (so a generator that supplies its whole answer set every run keeps working once a question
gets answered inline). **It may never replace a decision the plan already records**, and an empty value is
not a way to un-answer one.

A violating entry is **rejected**: `charter handoff` exits **1**, writes **nothing**, and names every
violation on stderr. Five rules, each of which used to pass silently (#186, #188):

| Rejected | Because |
|---|---|
| a value that is not one of a `single`/`multi` question's `options` | the flattened plan would assert an answer that is not in its own options list, printed on the very next line |
| the wrong number of values for the `mode` (`single`/`bool`/`number`/`free-text` take exactly one; `multi` takes one or more) | the mode is the question's declared shape |
| a `bool` value that is not `true`/`false`, or a `number` value that is not a number | same rule; those two modes declare a value domain too |
| `[]` or `null` | "this question was not answered here" is already spelled by **omitting the id**, so an empty value could only ever delete a decision |
| `[""]` or any blank value | a blank string is what a mis-written `jq` produces, not a decision — it would flatten as `Answered:` with nothing after it |
| a value that differs from an `answer` the plan already records | a recorded decision is the living document's durable half; change it in the `.charter.md`, not from outside |

**Two asymmetries, on purpose.**

- **An INLINE `answer` is never checked against `options`; a supplied one is.** The rendered form appends a
  "Something else" write-in to every select (#109), so a *reviewer's* answer may legitimately fall outside
  the options and `charter-format` states plainly that you must not validate `answer ⊆ options`. An
  `--answers` file is **not a reviewer at a page** — it is a machine input with no human behind it, and the
  flatten already tells a delegated agent to *choose one of the options above*. If you genuinely need a
  write-in, record it **inline**, where every other decision lives.
- **A `free-text` question can only be checked for SHAPE.** It declares no `options`, so there is no set to
  test against and the checkable facts reduce to *one value, not blank*. The rule is *a supplied answer must
  be something the question's declared shape can accept*; `free-text` declares less shape than `single`.

If you need to change a recorded decision, change it where it lives: re-answer in review and fold it in with
`charter poll --apply` / `charter resolve`, or edit the `.charter.md`. Both write atomically and both refuse
a plan they would corrupt.

### The `--answers` JSON shape

A **flat object** mapping each question's `id` (the `id` you gave the `:::question` block, and the
`questionId` you drained from `GET /api/answers`) to an **array of answer value strings**:

```json
{
  "db-choice": ["Postgres"],
  "regions": ["us-east-1", "eu-west-1"],
  "notes": ["Keep the read path Postgres-only for v1."]
}
```

- `single` / `bool` / `number` → a **one-element** array (`["Postgres"]`, `["true"]`, `["3"]`).
- `multi` → the **selected values** (`["us-east-1", "eu-west-1"]`).
- `free-text` → the **text as one element** (`["Keep the read path Postgres-only for v1."]`).

This file is **hand-authored** — you write it from the answers you drained during review. It is
deliberately a plain file-in/file-out shape with no dependency on a running review server: `handoff` is
an offline command.

### Open question vs Answered

For each `:::question`, `handoff` emits **two** plain-markdown lines: a status line, then a metadata line.

The **status** line is one of:

- **Answered** (an `answer` filled in inline, or an accepted `--answers` entry) → an **"Answered:"** line
  carrying the chosen value(s). "Answered" means the values **record a decision** — at least one, none of
  them blank. `[""]` is not an answer and flattens as **open** (#188).
- **Open, `target: human`** (no inline `answer` and no accepted `--answers` entry) → an
  **"Open question (unresolved)"** line. Guardrails sees an unresolved decision it can surface for a human.
- **Open, `target: agent`** → a **"Delegated decision — you must settle this before building:"** line, the
  metadata line, and a `_Decide: …_` instruction naming the mode's actual action and the author's lean:

  ```
  > **Delegated decision — you must settle this before building:** Which cache should front it?
  > _Question — id: `cache`; mode: `single`; target: `agent`; options: `Redis`, `in-memory`; recommended: `Redis`_
  > _Decide: choose exactly one of the options above, state the choice and your reason in the work you
  > generate from this plan, and build against it. Do not carry it forward as an open question. The plan's
  > author leans `Redis`; depart from it only with a stated reason._
  ```

  **Why the wording differs.** On this path there is no parser and no routing table — the flattened plan is
  prose, and prose is the whole interface. "Open question (unresolved)" reads as something *someone else*
  will settle, which is exactly backwards for a block whose `target` says the reader settles it. An
  **answered** agent question gains no instruction: the decision is already made.

The **metadata** line is the same shape in both cases — `id`, `mode`, `target`, and (when the mode declares
them) `options`, as emphasis + inline code:

```
_Question — id: `db-choice`; mode: `single`; target: `human`; options: `Postgres`, `DynamoDB`_
```

So the same plan hands off differently depending on what you supply:

```
# with --answers db-choice → ["Postgres"]
**Q: Which datastore should the service use?** — Answered: Postgres
_Question — id: `db-choice`; mode: `single`; target: `human`; options: `Postgres`, `DynamoDB`_

# without an answer for it
> **Open question (unresolved):** Which datastore should the service use?
> _Question — id: `db-choice`; mode: `single`; target: `human`; options: `Postgres`, `DynamoDB`_
```

**Why the metadata line is load-bearing** (it is not decoration):

- **`options` survive on an ANSWERED question.** `charter-format` says to fold a resolved answer in *"keeping
  the `options` as rationale"* — the **rejected** option is what lets the breakdown author a guardrail that
  FAILS if the implementation reaches for it. Dropping options once a question was answered destroyed exactly
  that, and made answered/open asymmetric.
- **`target` is the routing signal, so that a consumer *can* branch** on `human` vs `agent`; without it the
  flattened path structurally cannot honour `target: agent`, and a decision the plan author explicitly
  delegated is indistinguishable from one that needs a person.

  **No claim is made that any consumer does branch on it.** This line used to say the headless breakdown
  branches on `human` vs `agent`. It does not: neither literal Charter emits (`Open question (unresolved)`,
  `_Question — id`) appears anywhere in Guardrails' source, docs or skills. That false claim was the sole
  justification for exempting `target: agent` from `--fail-if-needs-human`, which is why the exemption is
  now narrowed (below). Filed reciprocally as Guardrails #500.
- **`id` correlates** the flattened question back to its `:::question` block (and to `--answers`).

It stays **plain CommonMark** — emphasis and inline code, nothing that reopens a `:::` directive — so the
headless contract is unchanged and the line reads naturally in a rendered PR diff.

### Unknown directives

An unrecognized `:::foo` (a typo, or a container the catalog does not define) is flagged as unknown **and its
body is preserved** — emitted as blockquoted prose under the marker, so the content behind the typo is never
silently dropped:

```
> **Unknown Charter directive `:::file-tree` — not in the format catalog.** Its body is preserved below.
>
> src/
>   Charter.Core/
```

## `--fail-if-needs-human` — the unattended gate

```
charter handoff plan.charter.md -o plan.md --answers answers.json --fail-if-needs-human
```

Without it, an **unanswered** `:::question` hands off as prose and the command exits `0`. That is right for
the attended flow — a human reads the rendered plan and sees the open fork. Unattended it is a
silent-degradation hazard: the pipeline proceeds, `plan-breakdown` reads *"Open question: …"* as ordinary
prose, and a decision nobody made gets resolved by whatever the breakdown agent infers. Nothing fails,
nothing warns, and the run goes green having quietly picked an option the human never chose.

**It writes the handoff and exits `2`.** It does **not** refuse to write, for two reasons:

- Every `2` in this pipeline shares one post-condition — *the output exists, go read it* — and inverting it
  at this seam is the exact class of surprise the flag exists to prevent.
- Refusing would not work anyway. A refusal leaves the **previous** run's `plan.md` on disk, and a stale
  flattened plan carries no open-question markers at all: internally consistent, passes any lint, and
  `plan-breakdown` only checks the file extension. Failing "closed" would hand a downstream something it
  cannot tell is wrong.

**The predicate runs AFTER `--answers` is merged** — the whole point is that a pipeline supplies an answers
file and wants to know it was complete.

### What blocks

| Blocker | Why |
|---|---|
| `unanswered-human-question` | An open question routed to a human, with nobody there to answer it. |
| `undecidable-agent-question` | An open `target: agent` question carrying **neither `options` nor `recommended`** — there is nothing to decide it with. |
| `malformed-question` | A `:::question` body that will not parse. Its `target` is unknown, **and** the flatten collapses the whole block to `> **Malformed question …**`, deleting its id, title and target from the handed-off document. |
| `unknown-directive` | An unrecognized `:::foo`. Charter cannot tell a misspelled `:::questoin` from a container the catalog genuinely does not define, so the strict gate resolves toward the human. |
| `duplicate-question-id` | An answer resolves into every block sharing the id, and `poll --apply` / `resolve` refuse the write. |

**The `target: agent` carve-out is narrowed, not absent.** #172 as filed assumed agent questions never
count, on the grounds that the flattened path branches on `target` — it does not (see the metadata-line note
above). Delegating is not a routing decision some downstream honours; it is prose asking the next agent to
decide. An agent question with `options` (or a `recommended` naming one) gives it something to decide
**with**, and passes. A bare `free-text` / `bool` / `number` agent question with no options gives it
nothing, is invisible to both of Charter's other gates, and would have Charter certify "no human needed"
while the downstream invents an answer. Three honest remedies: give the question options, answer it inline,
or accept that a person should see it.

### What stderr says

Each blocker is named with its `id`, `title`, `target` and line. Separately — and **without** changing the
exit code — any `--answers` id that matched **no** `:::question` in the plan is reported:

```
charter handoff: warning: 2 --answers id(s) matched no :::question in the plan and were discarded: gone, also-gone.
charter handoff: 1 item(s) need a human -- exit 2. The handoff WAS written to plan.md; this is an escalation, not a failure. Settle these in the .charter.md (or in --answers) and re-run.
  unanswered-human-question 'db' (line 7) [target: human]: Which database? -- an open question routed to a human, with nobody there to answer it
```

*"Your answers file had three ids and none of them matched"* is a signal the pipeline needs — a stale id, a
renamed question, or a generator written against a different plan all looked identical before, because
Charter discarded them in silence. It is reported rather than vetoed: the questions those ids failed to
answer already block on their own account.

## Exit codes

| Code | Meaning | What you do |
|---|---|---|
| **0** | The handoff was written and nothing is outstanding. | Proceed. |
| **2** | *(only with `--fail-if-needs-human`)* The handoff **was still written** AND something needs a human. An **escalation, not a failure**. | Read stderr, then the file. Settle the items and re-run. |
| **1** | Verb error — plan not found, unreadable `--answers`, an **`--answers` entry rejected** against its question's `mode`, `options` or already-recorded answer, or a `--manifest` name that would collide with the plan or with `--out`. | Fix the invocation. |

> **What a `1` promises about the disk.** Every check above runs *before* the write, so on all of those
> nothing was written. But a `1` says the **invocation** failed, not that the disk is untouched: under
> `--manifest` the handoff is written **first** and the manifest second, so a failure at the second write
> leaves a valid `plan.md` with no manifest beside it. That order is deliberate — a handoff with no manifest
> is an honest degraded state you can see and re-run, while a manifest describing a file that does not exist
> is a lie. Both are written atomically (temp file, then rename), so neither is ever half-written.

> **Why a rejected answer is `1` and not `2`.** It is not a plan defect; it is a bad **invocation** — the
> same class as the unparseable answers file that has always exited `1` here, and drawing the line at "is it
> syntactically JSON" would be arbitrary. Every `2` in this pipeline means *the output exists, go read it*,
> and every "write it anyway" variant of this rule would produce a `plan.md` that silently differs from the
> resolution you asked for, with the difference living only on stderr.
>
> The cost is stated rather than hidden: a refusal leaves a **previous** run's `plan.md` in place, and the
> `plan-sha256` stamp cannot expose that (same plan, different answers file). Closing it needs a hash of the
> answers beside it — Charter #187's chain-of-custody manifest.

> **NOTE ON `2`.** A `2` here means the same thing it means in `charter headless` and in **Guardrails** —
> *the output exists, go read it*. Guardrails' `BreakdownCommand` comments its own `2` as "a 2 means READ
> THE FOLDER", and its `ExitCodes.TaskFailed` as "the run completed but at least one task needs a human".
> A harness treating every `2` in this pipeline alike is doing the right thing.
>
> **The outlier is Charter's own drain.** `charter poll` / `charter resolve` return `2` for *"a queue was
> found and it was empty"* — close to the opposite. Do not read one vocabulary as the other.

## The provenance stamps

Every flattened plan ends with **two** lines:

```
<!-- charter: answers-sha256=none -->

<!-- charter: plan-sha256=870aed2f70841b927516f442ff4febd6d6002f13ad5a931db02a5bb69cfa78c6 -->
```

The **plan** stamp is the SHA-256 of the `.charter.md` **exactly as Charter read it** — byte-identical to
`planSha256` in the same plan's `charter headless` record. That pairing is what answers *"did the plan
Charter recorded match the plan Guardrails consumed"*, which the flattened file could not answer even in
principle before: front matter is stripped, so the output self-identified as nothing. It is still the **last**
line, so a consumer already matching on that keeps working.

The **answers** stamp is the SHA-256 of the `--answers` file's text, or the word **`none`** when no answers
file was supplied. One stamp identifies the plan; **the pair identifies the resolution inputs**, which is a
different and necessary question:

> Run once with `--answers v1.json --manifest --fail-if-needs-human` (exit `0`), then re-run as a plain
> `charter handoff plan.charter.md -o plan.md`. The write is unconditional, so `plan.md` becomes the
> all-questions-open flatten. No manifest is written, because `--manifest` is opt-in — so the **old** manifest
> survives, and `planSha256`, the plan stamp, `.headless.json`'s `planSha256` and `charterVersion` **all four
> match**. Without the answers stamp that is a manifest certifying decisions that are not in the file beside
> it, with every documented join green.

`none` is a word rather than a missing line on purpose: *"this run merged no answers file"* is a positive
fact, and an omitted line would be indistinguishable from a producer too old to write one.

An HTML comment was chosen because it is CommonMark-safe, **invisible** in a rendered diff, deterministic,
and — the property that decides it — it **survives a consumer that ignores exit codes and side files**.
Out-of-band signalling cannot fix a failure of out-of-band signalling. Both stamps are also **CRLF-immune**,
which the manifest's `handoffSha256` byte-hash is not.

(Charter's *own* renderer escapes them, because the pipeline sets `DisableHtml` so bare prose HTML can never
run. That is the security posture working; the flattened `.md` is Guardrails' input, not a `.charter.md`.)

## `--manifest` — the chain-of-custody manifest

```
charter handoff plan.charter.md -o plan.md --answers answers.json --manifest
```

`--manifest` also writes **`<out-stem>.manifest.json`** — for `-o plan.md`, `plan.manifest.json` beside it —
recording **what this run resolved, from the same resolution pass that wrote the CommonMark**. It is a
**separate artifact from `<plan>.headless.json`**, with its own `schema` and its own rules, because that
record is written by a different verb from a different resolution (the plan on disk, with **no `--answers`
merged at all**). The two have been reproduced disagreeing on the same plan in the same session, both exiting
`0`: the record said `"answered": true, "answer": ["Postgres"]` while the handoff said `Answered: Cassandra`.

**It is a boolean, not a path.** The name is derived from `--out` so a harness computes it without being told.
A derived name that would collide with the plan or with `--out` is **refused with exit 1**.

**Neither flag implies the other.** `--fail-if-needs-human` does not write a manifest (a gate flag must not
leave an unbidden file), and `--manifest` does not change an exit code (asking for a file must not). The gate
is evaluated either way when a manifest is written; `gate.flagPassed` records which.

```json
{
  "schema": 1,
  "charterVersion": "0.24.0",
  "plan": "plan.charter.md",
  "planSha256": "870aed2f70841b927516f442ff4febd6d6002f13ad5a931db02a5bb69cfa78c6",
  "answers": "answers.json",
  "answersSha256": "9f2c0e4a…",
  "handoff": "plan.md",
  "handoffSha256": "1d7be0c3…",
  "malformedQuestions": 0,
  "gate": {
    "flagPassed": true,
    "needsHuman": false,
    "exitCode": 0,
    "blockers": [],
    "unmatchedAnswerIds": []
  },
  "questions": [
    { "id": "db-choice", "title": "Which datastore should the service use?", "sourceLine": 9,
      "answered": true, "answer": ["Postgres"], "answerSource": "inline" },
    { "id": "cache", "title": "Which cache should front it?", "sourceLine": 13,
      "answered": true, "answer": ["Redis"], "answerSource": "answers-file" }
  ]
}
```

Top-level fields: `schema` · `charterVersion` · `plan` · `planSha256` · `answers` · `answersSha256` ·
`handoff` · `handoffSha256` · `malformedQuestions` · `gate` · `questions`. The `gate` object carries
`flagPassed` · `needsHuman` · `exitCode` · `blockers` · `unmatchedAnswerIds`; each `questions` entry carries
`id` · `title` · `sourceLine` · `answered` · `answer` · `answerSource`.

Each `blockers` entry carries `kind` · `id` · `title` · `target` · `sourceLine` — the same `kind` tokens the
gate's stderr prints. **`detail` is deliberately not serialized**: `HandoffGate` documents it as *"not a
contract; do not parse it"*, and putting a sentence into a versioned schema makes it a contract the first time
a harness greps it.

### Stable core — what you may assert on

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | The manifest's shape version. Currently **1**. |
| `charterVersion` | string | The tool that produced this. |
| `planSha256` | string | The plan's hash — the same value the in-band plan stamp and the headless record carry. |
| `answersSha256` | string\|null | The `--answers` file's hash, or null when none was passed. |
| `handoffSha256` | string\|null | The flattened output's hash, stamps included. **Advisory** — see below. |
| `malformedQuestions` | int | How many `:::question` bodies would not parse. `> 0` is a one-field detection. |
| `gate.flagPassed` | bool | Whether `--fail-if-needs-human` was on the command line. |
| `gate.needsHuman` | bool | The gate's verdict, computed whether or not the flag was passed. |
| `gate.exitCode` | int | What this run returned: `flagPassed && needsHuman ? 2 : 0`. |
| `gate.unmatchedAnswerIds` | string[] | `--answers` ids matching no `:::question`. Reported, never a veto. |
| `questions[].id` | string | The question's declared id. |
| `questions[].answered` | bool | The **merged** answer records a decision. Narrower than it looks — see below. |
| `questions[].answer` | string[] | The values the flatten printed for it. |
| `questions[].answerSource` | string\|null | `inline` · `answers-file` · null. |

`questions` is in **document order**.

### Explicitly NOT the contract

- **The three file-NAME fields — `plan`, `answers`, `handoff`.** They are bare names, never paths (`-o
  ../gr/plan.md` records `"plan.md"`), and effectively every Guardrails handoff is named `plan.md`, so they
  discriminate almost nothing. **The hashes are the join key; the names are decoration.**
- **`questions[].title`** — presentational, and reworded whenever the plan is.
- **`blockers` ordering**, and **JSON key order**.

### What is deliberately ABSENT, and why it matters

**No `artifact`. No `sourceMap`. No `anchorId`.** Those are the headless record's fields, and all three are
*wrong* on this path: the artifact is not rendered here, and a source map maps anchors to lines of the
**`.charter.md`** — line numbers that bear **no relation** to the flattened output. A consumer joining one
into `plan.md` would be joining against the wrong file. The rule, stated so nothing re-adds them helpfully:

> **Every line number in the manifest is a line in `plan`.**
> **The manifest carries no map into the handoff output at all.**

### Absence semantics

| You see | It means | It does NOT mean |
|---|---|---|
| `answers: null` **and** `answersSha256: null` | **No `--answers` was passed.** | "the answers file was empty" — an empty file is a file, and `{}` hashes to the hash of its text |
| a question missing from `questions` | Its body would not parse, so Charter has no id for it. It appears only as a `malformed-question` blocker with `id: null`. | "the plan has no such question" — check `malformedQuestions` |
| two `questions` entries with one `id` | The plan really carries two blocks; `sourceLine` tells them apart. `questions` is **not a map**. | "the manifest is corrupt" |
| `blockers: []` | **Nothing blocks.** Nothing here is computed conditionally. | — |
| `answerSource: null` | The question is unanswered, so there is no source. | a third source token |
| `gate.exitCode: 0` with `needsHuman: true` | The gate ran and found blockers, and `--fail-if-needs-human` was **not** passed, so the verdict went to this file and not to `$?`. | "nothing blocks" |

### `answerSource` — and the one thing it cannot tell you

`inline` means the value came from the `answer` recorded in the plan; `answers-file` means it came from the
`--answers` file. **Two values, and no more.** An answers file may FILL a question and may re-state an answer
verbatim, but it may **never** replace one (#186 — the run exits `1` instead), so *"the file overrode the
plan"* is not a state that can occur and there is no token for it.

**It does NOT distinguish "a human decided this" from "the automation supplied this."** `inline` covers a
reviewer's answer folded in by `poll --apply` / `resolve`, the drafting agent's own edit of the `.charter.md`,
and anything else that wrote the key — and **`handoff` never reads the review log**, so it holds no evidence
about who decided anything. What the field carries is **which hash reproduces this decision**: `inline` ⇒
`planSha256` covers it; `answers-file` ⇒ reproducing it also needs `answersSha256`. It is defined mechanically
— *which input the merge took the value from* — so a value re-stated verbatim by the file reads `answers-file`
even though both hashes would reproduce it.

**The limit that follows:** the manifest can certify **where a decision lived**, never that a human made it.

### `answered` here is NOT `answered` in the headless record

Same word, two scopes, and mixing them up is the defect #188 was:

- the record's `answered` = *the plan's own inline `answer` records a decision* (a pure function of the plan
  text — an `--answers` file is invisible to it);
- the manifest's `answered` = *the **merged** answer records a decision*, so it sees the `--answers` file.

Both use the same "records a decision" rule: at least one value, none of them blank. `[""]` is not an answer.

### The three hashes — read this before comparing one to `sha256sum`

All three use **one recipe**, and it is not the one a reader assumes:

> Charter reads the file with `File.ReadAllText`, which **strips a UTF-8 byte order mark** and **decodes
> UTF-16/UTF-32 per the mark**; the hash is then the SHA-256 of that decoded string **re-encoded as UTF-8**.

**So none of them equals `sha256sum` of the file's bytes unless the file is BOM-less UTF-8.** Charter writes
the handoff itself, so that one matches; your `answers.json` may not. A generator using **Windows PowerShell
5.1**'s `>` or `Out-File` writes UTF-16LE and gets a permanent, unexplainable mismatch — so `charter handoff`
**warns on stderr** when the answers file is not BOM-less UTF-8. It is a warning, not a rejection: the file
decodes correctly and the run is honest; only the comparison is surprising.

`answersSha256` covers the file's **own text**, not a canonicalized dictionary — two JSON files that parse to
the same answers hash differently, deliberately.

### `handoffSha256` is ADVISORY, and has no consumer today

Guardrails' own `PlanHash` hashes `guardrails.json` plus every `task.json`; it does **not** hash the markdown
the folder was broken down from, and nothing there records the source plan's hash (filed as Guardrails #505).
So this field is the only tamper detector Charter can offer, and a mismatch means **either tampering or a
line-ending rewrite in transit** — the hash alone cannot separate them. That is the other half of why the
in-band stamps matter: they survive a CRLF rewrite and this does not.

### Two more things the manifest cannot tell you

- **Whether a human ever reviewed the plan.** `charter handoff` does not read the review log at all.
- **Whether the caller honoured the exit code.** `gate.flagPassed` records the **argv**, not obedience — which
  is why it is named `flagPassed` and not `enforced`.

### The shape is bound by a test, not by this prose

`HandoffManifestContractTests` (Charter.Core.Tests) holds the manifest's emitted field set against *this
file*, and — unlike the first version of the record's drift test, which bound names only and let `answered`
change meaning while every assertion stayed green — it also binds the **meanings** most likely to move
silently: that `answers-file` can never mean override, what `handoffSha256` is computed over, and what
`answersSha256` covers.

### The end-to-end shape

1. Author `plan.charter.md` (`references/authoring-from-source.md`, `references/authoring-plans.md`).
2. `charter render` to check, `charter review` to get in-browser feedback — drain it with `charter poll`
   and fold answers inline via `poll --apply` / `charter resolve` (`references/review-loop.md`); revise
   until approved.
3. *(Optional)* Build `answers.json` for any `:::question` **not already resolved inline**. It cannot
   override one — see *What an `--answers` entry may do*.
4. Optionally `charter export plan.charter.md -o plan.html` for a shareable offline snapshot.
5. `charter handoff plan.charter.md -o plan.md [--answers answers.json]` → hand `plan.md` to the headless
   Guardrails `plan-breakdown` path. (The interactive `/plan-breakdown` skips this and reads the
   `.charter.md` directly.) **With no human in the loop, add `--fail-if-needs-human` and branch on the exit
   code** — `0` proceed, `2` stop and read stderr, `1` fix the invocation. **Add `--manifest` when the run has
   to be auditable afterwards**: it writes `plan.manifest.json` recording which inputs produced this exact
   `plan.md`, which is the question a post-mortem asks and the one the flattened file alone cannot answer.
