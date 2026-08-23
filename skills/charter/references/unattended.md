# Unattended runs — `charter headless`

```
charter headless plan.charter.md [--out-dir <dir>]
```

`charter headless` is the **unattended sibling of `charter review`** — for a firstmate crewmate, a CI job,
or any run with no human present. It **neither serves nor waits**: no server, no browser, no long-poll. It
renders the artifact, writes a forensic record beside it, and exits.

It is **not a replacement for review**. Review *elicits* decisions; headless *records that they were never
made*. If a human is available, run `charter review` — headless will not get you an answer.

> **It is also not the verb that feeds Guardrails.** `headless` writes no CommonMark; `charter handoff`
> writes no record. See *What `charter headless` is not*, below — and if you arrived here looking for the
> unattended **pipeline**, the verb you want is `charter handoff --fail-if-needs-human`
> (`references/handoff.md`).

## What it writes — two files, at derived names

- **The artifact.** The **same self-contained, offline, SDK-free** HTML `charter export` produces — the same
  exporter, **byte-identical**, pinned by a test. There is no second render path.
- **The record.** A JSON file holding what the review server would have held only in memory.

There is **no `-o`**. Both names are derived from the plan's own file name, so a collecting harness computes
them from the plan path with nothing passed and nothing configured:

| Plan | Artifact | Record |
|---|---|---|
| `storage.charter.md` | `storage.charter.html` | `storage.charter.headless.json` |

The stem is the plan's file name minus its **final** extension. They land **beside the plan** by default;
`--out-dir` **relocates the pair without renaming it** (the directory is created if missing). A derived name
that would land on the plan itself is **refused with exit 1**, never rendered over your source.

stdout is two plain lines; anything a human must deal with goes to stderr:

```
Exported plan.charter.md -> /abs/path/plan.charter.html
Recorded plan.charter.md -> /abs/path/plan.charter.headless.json
```

## The record — `<plan>.headless.json`

Real output, from a plan with one answered question, one open one, an unknown directive and a deferral that
names no issue:

```json
{
  "schema": 2,
  "charterVersion": "0.24.0",
  "plan": "plan.charter.md",
  "planSha256": "870aed2f70841b927516f442ff4febd6d6002f13ad5a931db02a5bb69cfa78c6",
  "artifact": "plan.charter.html",
  "planFormatVersion": { "status": "ok", "marker": "1", "version": 1 },
  "needsHuman": true,
  "questions": [
    { "id": "db-choice", "title": "Which datastore should the service use?",
      "mode": "single", "target": "human", "options": ["Postgres", "DynamoDB"],
      "answered": true, "answer": ["Postgres"], "recommended": "Postgres",
      "anchorId": "b96f2fd8a51c079c37c8c", "sourceLine": 9 },
    { "id": "cache", "title": "Which cache should front it?",
      "mode": "single", "target": "human", "options": ["Redis", "in-memory"],
      "answered": false, "answer": [], "recommended": null,
      "anchorId": "bfe0fbbd504a73f92c7f0", "sourceLine": 13 }
  ],
  "notes": [
    { "kind": "unknown-directive", "message": "…", "sourceLine": 17 },
    { "kind": "missing-recommendation", "message": "…", "sourceLine": 13 },
    { "kind": "untracked-deferral", "message": "…", "sourceLine": 7 }
  ],
  "sourceMap": { "b384efaa696809f3dd204": 5, "b96f2fd8a51c079c37c8c": 9 }
}
```

Top-level fields: `schema` · `charterVersion` · `plan` · `planSha256` · `artifact` · `planFormatVersion` ·
`needsHuman` · `questions` · `notes` · `sourceMap`. Each `questions` entry carries `id` · `title` · `mode` ·
`target` · `options` · `answered` · `answer` · `recommended` · `anchorId` · `sourceLine`; each `notes` entry
carries `kind` · `message` · `sourceLine`.

**The record is a pure function of the plan text and the tool version.** No clock, no random, no local path
(the plan and artifact appear as bare file names). So two runs over the same plan are **byte-identical** —
a harness can diff them cleanly — and the file is as safe to collect and hand on as the artifact beside it.

**`anchorId` + `sourceMap` is the point of the record.** Together they let a human trace an artifact element
back to the markdown line it came from **offline, after the fact** — the round-trip the live review server
provides, made durable for a run nobody watched.

### Stable core — what you may assert on

This half will not change meaning without a `schema` bump, so a post-mortem harness may build on it:

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | The record's shape version. Currently **2**. |
| `charterVersion` | string | The tool that produced this. |
| `plan` | string | The plan's **bare file name** — never a path. |
| `planSha256` | string | Lower-case hex SHA-256 of the plan's UTF-8 text **as read**, line endings included. |
| `needsHuman` | bool | The single escalation fact; equals the exit code (`false`→0, `true`→2). |
| `questions[].id` | string | The question's document-unique id. |
| `questions[].target` | string | `human` or `agent`. |
| `questions[].answered` | bool | See the absence table — it is narrower than it looks. |

`planSha256` is the value **`charter handoff` also stamps into the flattened plan** as a trailing
`<!-- charter: plan-sha256=… -->` comment. That is the join that answers *"did the plan Charter recorded
match the plan Guardrails consumed"*: the two are the same hash of the same bytes, deliberately.

### Explicitly NOT the contract

- **`message`** — written for a human, embeds ids, and is reworded whenever the wording is improved. Branch
  on `kind`, never on the sentence.
- **`sourceMap` values (the anchor ids).** The **shape** is stable — an object of `{anchorId: line}`, in
  **ascending line order** — and so is the guarantee that every `questions[].anchorId` is a key of it. **The
  anchor id VALUES are not**: which anchors exist and what they are called is renderer-derived and moved
  **twice inside v0.24.0 alone** (#166 stopped an id inside an opaque region being an anchor; #171 stopped a
  link-reference group stealing the plan title's slot). So: join `questions[].anchorId` to `sourceMap`
  **within one record**, and never across records produced by different Charter versions.
- **`notes[]` ordering**, and the presence of any *particular* note. Read `kind` and `sourceLine`.
- **`title`, `mode`, `options`, `answer`, `recommended`, `anchorId`, `sourceLine`, `artifact`** — present
  and useful, but they may gain nuance within a schema version.

### Absence semantics — what an empty or missing value means

Every row here is a place where *empty*, *not applicable* and *too old to know* look identical on the wire.
The rows worth reading twice are `options: []`, `recommended`, `sourceLine` and `answered`.

| You see | It means | It does NOT mean |
|---|---|---|
| `options: []` | Not applicable — the mode is `free-text`, `bool` or `number`, which declare none. | "the author forgot the options" |
| `recommended: null` | The author **considered a lean and declined to give one**. | "no lean was possible" |
| `recommended` **key absent** | The producing Charter predates the field (< #142). Never emitted by a current build, which always writes the key. | "the author declined a lean" |
| `sourceLine: null` on a note | The note is **document-wide** (a missing marker, a duplicate id). | "Charter could not find the line" |
| `answered: false` | **No inline `answer` that records a DECISION**, at the moment the record was built — the key is absent, the array is empty, or *any* value in it is blank. `[""]` is **not** an answer. | "unresolved at handoff" — an `--answers` file supplied to `charter handoff` is not visible here at all |
| `notes: []` | Charter raised no diagnostic **of a kind it knows**. | "the plan is clean" — a *newer* Charter may know kinds this one did not |

### `notes[].kind` — the tokens, and the rule for one you do not know

Hyphenated, matching the token style the annotation wire uses. **A consumer must ignore an unrecognised `kind`,
never reject the file**: new kinds are the compatible change this contract is built to absorb, and refusing
the whole record over one turns a diagnostic into an outage.

| `kind` | Raised when | Raises `needsHuman`? |
|---|---|---|
| `missing-version-marker` | The plan carries no `charter-format-version`. | no |
| `unsupported-version-marker` | The marker is present but outside the supported range. | no |
| `duplicate-question-id` | Two or more `:::question` blocks share an id. | **yes** |
| `malformed-question` | A `:::question` body would not parse, so its `target` is unknown. | **yes** |
| `unknown-directive` | An unrecognized `:::foo`: rendered visibly, but nothing interprets it. | no |
| `missing-recommendation` | An open, human, select-mode question carries no `recommended` key at all (#142). | no |
| `untracked-deferral` | A paragraph defers work without naming an issue, ticket or URL (#156). | no |
| `nested-question` | A `:::question` inside a container that renders its children — drawn as a live, answerable form, invisible to the block model (#203). | **yes** |
| `nested-diff` | A `:::diff` nested the same way: it flattens as blockquoted prose, where line-initial `+`/`-` are read as bullet markers (#203). | no |
| `nested-unknown-directive` | An unrecognized `:::foo` nested the same way — it may be a misspelled `:::question` (#203). | no |
| `nested-directive` | Any other nested `:::` directive — `comparison`, `diagram`, `note`, `warn` (#203). | no |

**Why four nesting tokens and not one.** `charter handoff --fail-if-needs-human` blocks on `nested-question`,
`nested-diff` and `nested-unknown-directive` but **not** on `nested-directive`, and the record escalates only on
`nested-question`. A consumer branches on `kind`, so a single token carrying three tiers would make the gate's
verdict unreproducible from the record. The tiers are not a guess: each kind's flattened body was read back as
plain CommonMark (`NestedDirectiveFlattenTests`). A nested `:::diff`'s markers really are eaten by bullet
parsing; a nested `:::comparison`, `:::diagram`, `:::note` and `:::warn` really do survive intact, losing only
the block's framing and its anchors.

**Nesting is reported only where the renderer draws it LIVE** — inside `:::note` / `:::warn` / `:::comparison`,
a list item, or a blockquote, along the whole ancestor chain. A directive inside `:::custom-html`, `:::diagram`,
`:::diff` or an unknown `:::foo` renders as inert text, asserts nothing false, and is the author's own markup;
none of those is reported.

The last two arrived in `schema` 2 and are the reason it is 2. Before them, `notes: []` did **not** mean
"Charter noticed nothing" — `charter handoff` printed both lints to stderr with no matching note kind — so
the query *"did Charter raise diagnostics nobody read"* was silently unanswerable. Adding them changes what
an existing field means, which is a bump by the record's own rule.

**`answered` also narrowed inside `schema` 2** (#188). It used to count array ELEMENTS, so `[""]` reported
`"answered": true` and the flattened plan emitted `Answered:` with nothing after it — a blank certified as a
decision. It now means *the array records a decision*: at least one value, none of them blank. That is
another change of an existing field's meaning, and it would have forced a `schema` 3 had 2 already shipped —
but 2 was raised **after** 0.24.0 was released, so **no consumer has ever seen a schema-2 record** and both
changes ride the same number. Do not read that as licence to change a meaning under a released version.

These are **not** auto-generated review comments — synthesizing review prose is your job, not Charter's.

### `planFormatVersion` — a pair, not an int

```json
"planFormatVersion": { "status": "ok", "marker": "1", "version": 1 }
```

- `status` — `ok` · `missing` · `unsupported`.
- `marker` — the declared value **verbatim** (`"1"`, `"1.0"`, `"draft"`), or `null` when there is no marker.
- `version` — the parsed integer, or `null` when the marker is absent or not an integer.

A bare integer could not carry this: Charter reports *no version* for a **missing** marker and for a
present-but-non-integer one alike, so `charter-format-version: 1.0` would read identically to an unstamped
plan. `status` plus `marker` separates them.

### The shape is bound by a test, not by this prose

`HeadlessRecordContractTests` (Charter.Core.Tests) holds the record's **emitted field set and every
`notes[].kind` token** against *this file*, and the example's `"schema"` against `HeadlessRecord.Schema`. A
field added without documenting it fails the build, naming the field.

That mechanism exists because the prose promise **already failed once**: `recommended` was added in #142 with
`schema` left at 1, so records in the wild both say `"schema": 1` while carrying different question shapes,
and this document went on showing the pre-#142 example. `schema` bumps only on an **incompatible** change —
a field removed, retyped, or given a new meaning. **Adding a field, or a new `kind`, is not a bump**, which
is why tolerating both is your side of the contract.

## Exit codes — a separate vocabulary from the drain's

`charter headless` has **its own** exit codes (`src/Charter.Cli/HeadlessExitCodes.cs`), shared with
`charter handoff --fail-if-needs-human`. They are **deliberately not** `charter poll`/`charter resolve`'s
`ReviewExitCodes`.

| Code | Meaning | What you do |
|---|---|---|
| **0** | Both files are on disk and **nothing is outstanding**. | Proceed. |
| **2** | Both files are on disk **AND a human must decide or fix something**. An **escalation, not a failure** — nothing was lost, and the evidence was persisted *before* the code was decided. | Stop and escalate. Read `needsHuman`, `questions[]` and `notes[]` to learn which decisions went unmade. |
| **1** | Verb error — plan not found, a derived name that would overwrite the plan, an I/O failure. **The files may not exist.** | Fix the invocation. |

**This `2` agrees with Guardrails' `2`.** Guardrails' `BreakdownCommand.NotCleanExitCode` is commented *"a 2
means READ THE FOLDER"* and its `ExitCodes.TaskFailed` is *"the run completed but at least one task needs a
human"* — the same post-condition: **the output exists, go read it.** A harness treating every `2` in this
pipeline alike is doing the right thing.

**The outlier is Charter's own drain.** `charter poll` / `charter resolve` return `2` for *"a queue was found
and it was empty"* — close to the opposite. Do not read one vocabulary as the other.

`needsHuman` in the record and the exit code are the **same fact**, so a harness reading the file and one
reading `$?` cannot disagree. On exit `2`, stderr also names each outstanding item with its line.

## What raises `needsHuman` — exactly four things

1. An **open `:::question` whose `target` is `human`** — the decision review existed to elicit, with nobody
   there to make it.
2. A **`:::question` whose body will not parse** — its `target` is unknown, and assuming `agent` would let a
   crew sail past a decision nobody can even read.
3. A **`:::question` nested inside a container that renders its children** (#203) — it is drawn on the page as
   a real, answerable form, so a human may already have answered it, while it appears nowhere in `questions[]`
   and no answer to it can ever be folded back. The record cannot report the decision, so it must not certify
   that none is needed.
4. **Duplicate `:::question` ids** — an answer would resolve into every block sharing the id, so both
   `poll --apply` and `resolve` refuse the write and the plan cannot be settled unattended at all.

Nothing else raises it. A **missing or unsupported format-version marker**, an **unknown `:::foo`
directive**, a missing `recommended`, an untracked deferral and a nested directive that is not a question land
in `notes[]` and **do not** change the exit code — every other verb treats those as warnings too, and widening
the rule would make the flag almost always true and therefore worthless.

**That base-rate argument is why #3 is an exception rather than a breach of the rule.** It is what excludes
`unknown-directive`: unknown directives occur in ordinary plans, so escalating on them would fire constantly.
A correct plan has **zero** nested questions, so #3 is false on every healthy document — it costs nothing on
the plans the flag exists to wave through, and it is the only signal that a decision a human can see is one
Charter cannot.

> **Exit `0` does not mean every question is answered.** An open `:::question` with `target: agent` raises
> nothing and changes no exit code — by design, because **you** are the agent it is addressed to. Read
> `questions[]` on a `0` as well as on a `2`, and answer the `target: agent` ones yourself.
>
> `charter handoff --fail-if-needs-human` draws this line **differently and more strictly** — it counts an
> agent question that carries neither `options` nor `recommended`, and it counts an unknown directive.
> The two verbs deliberately do not share a predicate; see `references/handoff.md`.

## What `charter headless` is not

- **Not a review.** Nothing is served, nobody answers a `:::question`, and no annotation is ever collected.
- **Not a handoff.** It writes no CommonMark. Beware the collision: *"the headless Guardrails path"* elsewhere
  in this skill means **which ingestion path Guardrails uses** (`charter handoff`'s flattened output). The
  `charter headless` verb means **how Charter runs with no human present**. They meet nowhere —
  `headless` emits no handoff markdown, and `handoff` writes no record.
- **Not a substitute for `export`.** If all you want is the artifact, `charter export -o <path>` still names
  its own output. `headless` adds the derived-path convention, the record, and the escalation code.
