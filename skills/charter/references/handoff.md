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
to resolve them (a matching `id` in `--answers` takes precedence over an inline answer). Omit it, and any
question with no inline answer hands off as an **open question** — a legitimate, common case when the human
hasn't decided yet.

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

- **Answered** (a matching `id` in `--answers`, or an `answer` already filled in inline) → an
  **"Answered:"** line carrying the chosen value(s).
- **Open, `target: human`** (no inline `answer` and no matching `--answers` id) → an
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
| **1** | Verb error — plan not found, unreadable `--answers`. **Nothing was written.** | Fix the invocation. |

> **NOTE ON `2`.** A `2` here means the same thing it means in `charter headless` and in **Guardrails** —
> *the output exists, go read it*. Guardrails' `BreakdownCommand` comments its own `2` as "a 2 means READ
> THE FOLDER", and its `ExitCodes.TaskFailed` as "the run completed but at least one task needs a human".
> A harness treating every `2` in this pipeline alike is doing the right thing.
>
> **The outlier is Charter's own drain.** `charter poll` / `charter resolve` return `2` for *"a queue was
> found and it was empty"* — close to the opposite. Do not read one vocabulary as the other.

## The provenance stamp

Every flattened plan ends with one line:

```
<!-- charter: plan-sha256=870aed2f70841b927516f442ff4febd6d6002f13ad5a931db02a5bb69cfa78c6 -->
```

It is the SHA-256 of the `.charter.md` **exactly as Charter read it** — and it is byte-identical to
`planSha256` in the same plan's `charter headless` record. That pairing is what answers *"did the plan
Charter recorded match the plan Guardrails consumed"*, which the flattened file could not answer even in
principle before: front matter is stripped, so the output self-identified as nothing.

An HTML comment was chosen because it is CommonMark-safe, **invisible** in a rendered diff, deterministic,
and — the property that decides it — it **survives a consumer that ignores exit codes and side files**.
Out-of-band signalling cannot fix a failure of out-of-band signalling.

(Charter's *own* renderer escapes it, because the pipeline sets `DisableHtml` so bare prose HTML can never
run. That is the security posture working; the flattened `.md` is Guardrails' input, not a `.charter.md`.)

### The end-to-end shape

1. Author `plan.charter.md` (`references/authoring-from-source.md`, `references/authoring-plans.md`).
2. `charter render` to check, `charter review` to get in-browser feedback — drain it with `charter poll`
   and fold answers inline via `poll --apply` / `charter resolve` (`references/review-loop.md`); revise
   until approved.
3. *(Optional)* Build `answers.json` for any `:::question` not already resolved inline (or to override one).
4. Optionally `charter export plan.charter.md -o plan.html` for a shareable offline snapshot.
5. `charter handoff plan.charter.md -o plan.md [--answers answers.json]` → hand `plan.md` to the headless
   Guardrails `plan-breakdown` path. (The interactive `/plan-breakdown` skips this and reads the
   `.charter.md` directly.) **With no human in the loop, add `--fail-if-needs-human` and branch on the exit
   code** — `0` proceed, `2` stop and read stderr, `1` fix the invocation.
