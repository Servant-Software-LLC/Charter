---
name: charter
description: Use when you must turn a task into a reviewable plan with Charter — author a block-structured .charter.md, render/serve it for a human to annotate in the browser, then hand off plain CommonMark to Guardrails. Triggers on CHARTERING WORK ("charter this work", "charter the refactor", "charter a plan for this"), on CHARTING WORK ("chart this out", "make me a chart for this feature"), and on planning language ("draft a plan I can review", "write this up before you build it") — in every case the object is a piece of WORK a human will approve before it happens, NOT drawing a data visualization from existing data. Covers the author → review → handoff workflow and the block catalog.
---

# Charter — author → review → handoff

Charter is the front door of an agentic delivery pipeline (`Charter → Guardrails → delivery`). **You**
(the drafting agent) author a rich, block-structured **plan** as markdown-with-directives; a **human**
reviews it in the browser and **comments in place** (notes anchored to the exact block); you drain that
feedback and revise; then you hand the approved plan to Guardrails, which breaks it into a task DAG.

This skill teaches you to *drive* Charter. You interact with it through a small set of CLI verbs over a
loopback review server — ground anything ambiguous in `src/Charter.Cli/Program.cs` (the verb list). The
block catalog and `:::question` schema are normative in the **`charter-format`** skill — the single source
of truth this skill cites, never forks; the architecture and load-bearing invariants live in
`docs/plans/01-combine-lavish-and-visual-plan.md`.

## When to use

Use this skill when the human asks you to **plan** a change and wants to **review it visually before it
executes** — anything of the form "draft a plan I can look over," "write this up as a reviewable plan,"
"put this in front of me before you build it," or "get this ready for Guardrails." Reach for it whenever
the deliverable is a *plan a human approves in the browser*, not code you write directly.

**"Charter" and "chart" are first-class triggers too**, and in practice they are the *more reliable* ones —
see the note on plan-mode contention below. Three layers, strongest first:

| Trigger | Example | Why it's here |
|---|---|---|
| **charter** (verb) | "charter this work," "charter the refactor," "charter a plan for this" | Collision-free. Nobody says *charter* for a graph, and it doesn't trip plan mode. |
| **chart** (verb) | "chart this out," "make me a chart for this feature," "let's chart it" | What people reach for once they know the tool by name. |
| **plan** (verb/noun) | "draft a plan I can look over," "write this up as a reviewable plan" | Covers first-timers who don't know the product name. |

All three mean the same request and produce the same artifact — the word choice signals only how familiar
the human is with the tool. **The noun never changes: the deliverable is always "the plan"**, the file is
always `<name>.charter.md`. "Chart" and "charter" are verbs for *making* one, not new names for it.

**The other direction — a change that already happened.** "Recap this branch," "walk me through what you
just did," "let me review this PR as a document" asks for the same deliverable built from a **diff**
instead of an intent. Start with `charter recap` and follow `references/recap.md`; everything after the
seed — review, drain, reply, handoff — is identical. Do not use it to report on an *execution run*
(outcomes, retries, timings): that is Guardrails' `uber-report`, not Charter's surface.

On *"charter a plan"* specifically: strictly you charter the **work**, not the plan — the plan is the
artifact chartering produces, the same way a *project charter* is the document that authorizes a project to
begin. Prefer "charter the work" when you phrase it yourself. But **accept "charter a plan" without
hesitation** when a human says it; the intent is unmistakable and correcting their grammar is not your job.

> **Why not rely on "plan" alone?** In an agentic harness, planning language is heavily contested — Claude
> Code's own **plan mode**, a `Plan` agent, `plan-reviewer`, and Guardrails' `plan-breakdown` all compete for
> it, and plan mode is a *harness* feature that can preempt skill matching entirely. "Charter" and "chart"
> are the triggers that reliably reach this skill; "plan" is kept as a fallback, not depended on.

> **Do not confuse charting *work* with charting *data*.** "Chart" is heavily overloaded with data
visualization, and this skill must stay out of the way for that. The signal is the **object** of the verb,
not the verb:

| This skill | Not this skill |
|---|---|
| chart **work that hasn't happened yet** — a feature, a change, a task, a migration | chart **data that already exists** — numbers, metrics, a series, a column |
| "chart out how we'd add auth" | "chart our signup numbers by region" |
| "chart this refactor before you start" | "chart this CSV" / "add a bar chart to the dashboard" |

The test: is the thing being charted **work a human should approve before it happens**? Then it's Charter.
Is it **data to be visualized**? Then it is a plotting task and this skill does not apply — reach for a
charting library, not `.charter.md`. When a request is genuinely ambiguous ("chart the response times"),
ask which one they mean rather than guessing; authoring a plan for someone who wanted a graph wastes their
time in a way that is obvious and annoying.

This also covers **"convert this into a plan"** — the human hands you a document, a PDF, a link, a
Confluence page, or pasted prose and asks you to turn it into a reviewable Charter plan. That is an
**agent** task (choosing what becomes a diagram, a comparison, or an open question is judgment, and the
LLM lives in *you*, never in the binary) — `references/authoring-from-source.md` is the on-ramp, and the
`charter convert` verb below is the mechanical seed it builds on. The human won't type a file path at a
shell; they ask *you*, and you drive the skill.

Do **not** use it for work that should just be done, for prose with no decisions to elicit, for
reporting on work already finished, or for **drawing a chart from data** (see the warning above —
that is a plotting task, not a Charter deliverable).

## The CLI surface (the only verbs that exist)

| Verb | What it does |
|---|---|
| `charter convert <input.md> -o <plan.charter.md>` | **Seed** a `.charter.md` from a plain Markdown doc: every block passes through unchanged, the **simple** items of a section whose heading names open questions / risks / decisions become `:::question` blocks, and the format marker is stamped. The deterministic floor you then **enrich** — see step 1 below for what it leaves behind and why you must read its stderr. |
| `charter recap <range> -o <plan.charter.md> [--repo <dir>] [--max-diff-lines <n>]` | **Seed** a `.charter.md` from a **git diff** — `convert`'s mirror image, for reviewing a change that already happened. Emits an overview, a commit table, and one per-line-annotatable `:::diff` per file; git is read **only**. Like `convert` it is the deterministic floor, not a generator: no summary, no grouping, no `:::diagram`, no `:::question` — those are yours. **Read its stderr**: it names what is still missing, plus any capped or binary file. See `references/recap.md`. |
| `charter render <plan.charter.md> -o <out.html>` | Render the plan to **one portable** HTML artifact. |
| `charter review <plan.charter.md> [--no-open] [--keep-annotations]` | Serve the rendered + SDK-injected plan over the **loopback** review server and open the browser for in-place annotation. If the plan was **replaced** at the same path (no queued annotation's anchor still resolves), the old queue is **set aside, never deleted** — Charter says where on stderr **and in the review panel**; `--keep-annotations` restores it. |
| `charter poll [<plan.charter.md>] [--wait] [--watch] [--for <dur>] [--apply]` | **`--watch` is what you want for a real review**: it re-arms across long-poll cycles in ONE invocation until `--for` elapses (default 2h), so a single command covers the whole session — `--wait` alone is just ONE ~30s cycle and returns exit `2` on silence, which means *nothing arrived yet*, not *the review is over*. Drain the running review session's queued annotations + `:::question` answers; `--apply` writes the answers **inline** into the plan's `:::question` blocks. With **no** live session, `charter poll <plan>` instead folds the **committed review logs** beside the plan — how you read a teammate's comments while executing. **Branch on its exit code** (`0` drained · `2` clean-empty · `3` no session/log · `4` drain FAILED, state unknown · `5` apply refused), never on an empty array. |
| `charter resolve <plan.charter.md> [--apply-stale-answers]` | Solo-reviewer companion to `poll --apply`: fold a human reviewer's queued answers **inline** into the plan when no agent is looping `poll`. An answer whose `:::question` has **changed shape** since it was given (title/mode/target/options) is reported and left queued (exit `5`), never written — `--apply-stale-answers` is the human's explicit "apply it anyway". |
| `charter headless <plan.charter.md> [--out-dir <dir>]` | The **unattended** sibling of `charter review` — for a crewmate or any run with no human present. Serves nothing and waits for nothing: writes `export`'s artifact (**same exporter, byte-identical**) plus a **forensic JSON record**, at names **derived** from the plan (`storage.charter.md` → `storage.charter.html` + `storage.charter.headless.json`), then exits. **Its exit codes are their own vocabulary, not the drain's**: `0` nothing outstanding · `2` both files are on disk **and** a human must decide or fix something (an **escalation**, not a failure) · `1` verb error. |
| `charter export <plan.charter.md> -o <out.html>` | Write a **self-contained, offline** HTML artifact (local assets inlined, local paths scrubbed, SDK-free). |
| `charter handoff <plan.charter.md> -o <out.md> [--answers <answers.json>]` | Convert the plan's `:::` directives to **plain CommonMark** for the **autonomous** Guardrails `plan-breakdown` path. (That path is also called "headless" — an unrelated sense of the word from the `charter headless` verb above. `handoff` writes no record; `headless` writes no CommonMark.) |
| `charter reply <plan.charter.md> --to <comment-id> --body <text>` | **Answer a review comment in its thread** — your voice back to the reviewer. Accept it, **push back on it**, or ask what was meant. Writes one `reply` record to your own author log: it does **not** touch the plan (single-writer), does not contact the review server, and does **not** settle the comment (that stays a deliberate `resolve`). A reviewer with the page open sees it arrive over the review-log watch. Attributed to `actor: agent` by default; `--as-human` only if you are writing on the human's behalf. |
| `charter skills install [--project] [--force]` | Install the bundled `charter` + `charter-format` skills so Guardrails `plan-breakdown` can discover them. |
| `charter --version` | Print the version. |

`poll` discovers the running session from a per-user registry, so the **capability key never crosses your
command line**. The loopback HTTP endpoints still sit beneath it — `references/review-loop.md`.

## The workflow: AUTHOR → REVIEW → HANDOFF

### 1. AUTHOR — write the plan, then `charter render`

> **Load the `charter-format` skill BEFORE writing your first `:::` block.** Not "consult if unsure" — a
> required step, the way `plan-breakdown` gates on `guardrails --version`. The catalog below cites the
> schema; it does not carry it, and the fields you have never seen are exactly the ones you will omit.
>
> **Do not derive a block's shape from another block you wrote.** Copying the previous question is the path
> of least resistance, always produces schema-valid output, and silently drops every optional field you have
> not seen — with no feedback signal, because optional fields are optional. Eleven `:::question` blocks were
> authored across two real plans this way, every one missing `recommended`; the omission surfaced only when a
> human noticed the absent *(Recommended)* tags in the review panel. Derive the shape from `charter-format`.
>
> **Before you finish, check each `:::question` carries:** `rationale` (why you are asking, or why you lean
> as you do — it renders inside the box), `target` (`human` or `agent`), and — for a `human`-targeted select
> question — **`recommended`**, verbatim one of the options. If a fork genuinely is 50/50, write
> `"recommended": null` to record that you considered a lean and declined. An *absent* key is
> indistinguishable from never having known the field exists, which is why `charter render`, `review` and
> `handoff` warn about it.

Write the plan as a `.charter.md` file using the [block catalog](#block-catalog). Begin the file with a
plain-YAML frontmatter marker declaring the format version (`---` / `charter-format-version: 1` / `---`) —
normative in the `charter-format` skill. Starting from a prompt, an existing doc, a PDF, or a Confluence
page? `references/authoring-from-source.md` is the on-ramp: how to ingest each source and choose the right
block for its content. If that source is **already Markdown**, don't start from a blank file — run
`charter convert <source.md> -o plan.charter.md` first: it passes every block through, promotes the **simple**
items of a section whose heading names open questions / risks / decisions (numbered headings and numbered lists
included) to `:::question`, and stamps the marker, giving you a valid seed to **enrich**
(add diagrams, comparisons, more questions) rather than author from scratch. A **complex/nested** item is left
**verbatim as prose** and reported on **stderr** (`promoted X of Y`, plus a warning naming each item left) — so
read convert's stderr and hand-promote or enrich anything it reported it left. Then render it to check the
artifact:

```
charter render plan.charter.md -o plan.html
```

This produces **one portable HTML file** — it opens standalone in any browser. The annotation SDK is
injected **only at serve time**, never baked into this file (invariant 1: *portable artifact*). Rendering
is your fast inner loop while drafting; open `plan.html` yourself to sanity-check layout before you put
it in front of the human.

> **Authoring always terminates in `charter review` — this is not optional.** `render`/`plan.html` is
> *your* private check, not the human's review. When the plan is ready for a human — by **either** path
> (convert-and-enrich or from-scratch) — the single terminal action of authoring is to run `charter review`
> and hand them the printed capability URL. **Never** offer reading the raw `.charter.md` (or `plan.html`)
> as an alternative or an equally-valid option: in the review server a `:::question` is a native `<form>`,
> a `:::diagram` is a rendered graph whose individual nodes are annotatable, and every block is
> annotatable **in place** — none of
> which exists in the raw source. Reading the raw file is not a lighter version of the review; it is not
> the review Charter is built around at all. Do not present a "look at the file vs. start the review"
> choice — start the review.

### 2. REVIEW — `charter review`, then drain feedback with `charter poll`

```
charter review plan.charter.md
```

This renders the plan, injects the SDK, and serves it over the **loopback** review server — bound to
`127.0.0.1` with a **per-session capability key**, path-confined to the plan's directory (invariant 4:
*loopback + capability*). It opens the human's browser at the capability URL and prints the ready line:

```
Charter review server ready: http://127.0.0.1:<port>/?key=<key>
```

The `review` process **keeps serving until stopped** (Ctrl+C), so run it in the background and read the
`<port>` and `<key>` off that ready line — every request you make needs the key. Pass `--no-open` when
no browser should launch (headless/CI).

In the browser the human annotates **elements** (whole blocks), **text ranges** (a selection inside a
block), and **diagram nodes** (a node inside a rendered diagram), and submits answers to any
`:::question` blocks. You read that feedback back by running **`charter poll`**, which drains the review
session's two streams:

- **annotations** — each carries the resolved **markdown source line** so you know exactly which line to
  edit; act on them by editing the source.
- **`:::question` answers** — fold them into the plan with `charter poll --apply` (or `charter resolve`),
  which writes each chosen answer **inline** into its `:::question` block (the living-document write).

The reviewer can also click **Send to agent** in the review panel to say *"I'm done with this round."* That
rides the poll envelope as `reviewSubmitted: true` — the signal to do the substantial rewrite, as opposed to
absorbing one more comment mid-review. **Check it on every poll.** It signals only; you remain the sole
writer of the plan file.

Edit the markdown source in response; the server re-renders from source on the next request (live
reload), so the human sees your revision without restarting. Loop — poll, revise, let them re-review —
until the plan is approved. The JSON envelope shapes, the exit codes, the long-poll semantics, and a
concrete drain loop are in `references/review-loop.md`.

**Say something back — silence is indistinguishable from failure.** Revising the plan is not a reply. From
the reviewer's side, "I agreed and changed it", "I disagreed and left it", "I misread you and changed the
wrong thing", and "nobody was listening" all look identical. Use `charter reply` on any comment you do not
simply action:

```
charter reply plan.charter.md --to <comment-id> --body "Disagree — the read path is append-only, so Postgres buys nothing here. Left as-is; say the word and I'll change it."
```

Reply when you **push back**, when you **need clarification**, or when you acted in a way the diff alone
won't explain. It costs one line and it is the difference between a review loop and a suggestion box. It
writes only to your own author log — the plan stays single-writer, and you remain its only writer.

`charter review` also writes each comment to a durable per-author log at `<plan>.review/*.jsonl` beside the
plan. Those records travel to teammates **by git** and are permanent in history — Charter reads git for the
author's identity but never commits, pushes, or stages. The stderr notice saying so fires **once, and only
when that directory is already git-tracked**; a solo reviewer is told nothing, and the directory isn't
created until the first comment lands. **Absence of that line does not mean nothing is being logged.** If the
human wants review kept local, tell them to gitignore `*.review/`.

#### When there is no human — `charter headless`

If the run is genuinely unattended (a firstmate crewmate, CI), review has nobody to elicit anything from.
`charter headless plan.charter.md` is that path: it writes `export`'s artifact **plus a forensic record**
(`planSha256`, every `:::question` with its target and answered state, Charter's own diagnostics, and an
**`anchorId` → markdown-line `sourceMap`** so a human can trace an artifact element back to its source line
**offline, after the fact**), then exits. **Branch on its exit code** — `2` means everything is on disk *and*
a human must decide or fix something. It is review's **sibling, not its replacement**: it collects no
feedback and answers no question. Details in `references/unattended.md`.

### 3. HANDOFF — `charter export` (optional) then `charter handoff`

Optionally capture a shareable snapshot of the approved plan:

```
charter export plan.charter.md -o plan.html
```

`export` writes a **truly offline** artifact — local assets inlined as `data:` URIs, local paths
scrubbed, SDK-free — so it can be attached or archived and still opens with no server (distinct from
`render`, which leaves local asset references as-is).

Then convert the approved plan to the shape Guardrails consumes:

```
charter handoff plan.charter.md -o plan.md --answers answers.json
```

`handoff` rewrites every `:::` directive (`:::note`, `:::warn`, `:::comparison`, `:::diagram`,
`:::question`, …) into **plain CommonMark** — the flattened form the **headless** Guardrails
`plan-breakdown` path consumes (invariant 5: *dual handoff* — the **interactive** `/plan-breakdown` instead
reads the `.charter.md` directly via the `charter-format` skill). Each `:::question` resolves against the
optional `--answers` JSON: supplied answers become an **"Answered:"** line; anything left unanswered becomes
an **"Open question"** line. Omit `--answers` and every question hands off as open. The `--answers` shape and
the Open-vs-Answered rendering are in `references/handoff.md`.

## Block catalog

Blocks are **CommonMark prose plus `:::` directive containers**. The catalog is single-sourced in the
**`charter-format`** skill (invariant 3: *format single-sourced*) — this table cites it, the renderer owns
it, and a drift test binds them. Do not fork or invent directives.

| Block | Directive |
|---|---|
| prose / heading / list | plain markdown |
| callout | `:::note` / `:::warn` |
| table / comparison | pipe tables · `:::comparison` |
| code / diff | fenced ` ```lang ` · `:::diff` |
| diagram | `:::diagram` (Mermaid body) — rendered theme-aware as inline SVG; annotatable per node and as a whole; pan/zoom in review when oversized |
| wireframe / escape hatch | `:::custom-html` (sanitized inline HTML) |
| **question (elicitation)** | **`:::question`** |

**`:::question`** is the elicitation block — how you ask the human to *decide* something inside the plan.
Its body is a validated **JSON** payload — `id`, `title`, `mode`, `options`, `target`, and an optional
`answer` (whose presence marks the question resolved). The `mode` tokens and the full schema, including the
open-vs-resolved rule, are normative in the **`charter-format`** skill — cite it, don't restate it here. It
renders to a native HTML `<form>`; submitting posts structured answers that you drain with `charter poll`
and fold in with `poll --apply` / `charter resolve`. Every block also gets a content-derived **stable ID**
and a **source-map** back to its markdown line range, which is what lets an annotation on the rendered HTML
round-trip to the source line you edit.

The full catalog with each block's syntax, the `:::question` schema in depth, and a sample `.charter.md`
skeleton are in `references/authoring-plans.md`.

## References

Keep this file lean; the depth lives in `references/`:

- **`references/authoring-from-source.md`** — the on-ramp: turn any source (a prompt, a markdown file, a
  PDF, a link, a Confluence page) into a rich `.charter.md` — how to ingest each, and how to choose the
  right block for its content.
- **`references/authoring-plans.md`** — the block catalog in depth + a short sample `.charter.md` skeleton.
- **`references/recap.md`** — the OTHER direction: `charter recap` builds the same deliverable from a
  **diff** instead of an intent, for reviewing a change that already happened. The seed → enrich → review
  flow, what to add and what to delete, the Guardrails `uber-report` boundary, and why a generated diff
  block sometimes needs a widened fence.
- **`references/review-loop.md`** — running `charter review`, in-browser annotation, and draining
  feedback with `charter poll` (`--apply` / `charter resolve` fold answers inline) on the loopback server;
  **the exit codes**, `drainError`, the **Send to agent** round hand-off, and reading a teammate's
  committed comments with no server running.
- **`references/unattended.md`** — `charter headless`: the unattended sibling of review. The derived output
  names, the forensic record's shape, the **separate** exit-code vocabulary, and exactly what raises
  `needsHuman`.
- **`references/handoff.md`** — `charter export` (offline artifact) and `charter handoff` (→ plain
  CommonMark; the `--answers` JSON shape; Open-question vs Answered).
