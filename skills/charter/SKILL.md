---
name: charter
description: Use when you must turn a task into a reviewable plan with Charter — author a block-structured .charter.md, render/serve it for a human to annotate in the browser, then hand off plain CommonMark to Guardrails. Covers the author → review → handoff workflow and the block catalog.
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

This also covers **"convert this into a plan"** — the human hands you a document, a PDF, a link, a
Confluence page, or pasted prose and asks you to turn it into a reviewable Charter plan. That is an
**agent** task (choosing what becomes a diagram, a comparison, or an open question is judgment, and the
LLM lives in *you*, never in the binary) — `references/authoring-from-source.md` is the on-ramp, and the
`charter convert` verb below is the mechanical seed it builds on. The human won't type a file path at a
shell; they ask *you*, and you drive the skill.

Do **not** use it for work that should just be done, for prose with no decisions to elicit, or for
reporting on work already finished.

## The CLI surface (the only verbs that exist)

| Verb | What it does |
|---|---|
| `charter convert <input.md> -o <plan.charter.md>` | **Seed** a `.charter.md` from a plain Markdown doc: pass every block through unchanged, promote a section whose heading names open questions / risks / decisions (including a numbered heading like `9. Open questions / risks`) over a bullet **or numbered** list into `:::question` blocks, and stamp the format marker. Only **simple** items (one paragraph, no nested structure) promote; a **complex/nested** item (sub-bullets, a trailing decision paragraph) is left **verbatim as prose** and **reported on stderr** (`promoted X of Y`, plus a warning naming each item left) — never silently dropped. The deterministic floor — you then **enrich** it (diagrams, comparisons, more questions), including hand-promoting or enriching any item convert reported it left as prose. |
| `charter render <plan.charter.md> -o <out.html>` | Render the plan to **one portable** HTML artifact. |
| `charter review <plan.charter.md> [--no-open] [--keep-annotations]` | Serve the rendered + SDK-injected plan over the **loopback** review server and open the browser for in-place annotation. If the plan was **replaced** at the same path (no queued annotation's anchor still resolves), the old queue is **set aside, never deleted** — Charter says where on stderr; `--keep-annotations` restores it. |
| `charter poll [<plan.charter.md>] [--wait] [--apply]` | Drain the running review session's queued annotations + `:::question` answers; `--apply` writes the answers **inline** into the plan's `:::question` blocks. With **no** live session, `charter poll <plan>` instead folds the **committed review logs** beside the plan — how you read a teammate's comments while executing. **Branch on its exit code** (`0` drained · `2` clean-empty · `3` no session/log · `4` drain FAILED, state unknown · `5` apply refused), never on an empty array. |
| `charter resolve <plan.charter.md>` | Solo-reviewer companion to `poll --apply`: fold a human reviewer's queued answers **inline** into the plan when no agent is looping `poll`. |
| `charter export <plan.charter.md> -o <out.html>` | Write a **self-contained, offline** HTML artifact (local assets inlined, local paths scrubbed, SDK-free). |
| `charter handoff <plan.charter.md> -o <out.md> [--answers <answers.json>]` | Convert the plan's `:::` directives to **plain CommonMark** for the headless Guardrails `plan-breakdown` path. |
| `charter skills install [--project] [--force]` | Install the bundled `charter` + `charter-format` skills so Guardrails `plan-breakdown` can discover them. |
| `charter --version` | Print the version. |

You read the human's feedback with **`charter poll`** — it drains the queued annotations and `:::question`
answers from the running review server, discovering the session from a per-user registry so the capability
key never crosses your command line. `charter poll --apply` (agent-in-the-loop) and `charter resolve` (solo
human reviewer) then fold the answers **inline** into the plan's `:::question` blocks. The loopback HTTP
endpoints still sit beneath `poll` — see [The review loop](#the-review-loop) and `references/review-loop.md`.

## The workflow: AUTHOR → REVIEW → HANDOFF

### 1. AUTHOR — write the plan, then `charter render`

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

`charter review` also writes each comment to a durable per-author log at `<plan>.review/*.jsonl` beside the
plan, and says so on stderr. Those records travel to teammates **by git** and are permanent in history —
Charter reads git for the author's identity but never commits, pushes, or stages. If the human wants review
kept local, tell them to gitignore `*.review/`.

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
| diagram | `:::diagram` (Mermaid body) — rendered theme-aware as inline SVG, annotatable per node |
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
- **`references/review-loop.md`** — running `charter review`, in-browser annotation, and draining
  feedback with `charter poll` (`--apply` / `charter resolve` fold answers inline) on the loopback server;
  **the exit codes**, `drainError`, the **Send to agent** round hand-off, and reading a teammate's
  committed comments with no server running.
- **`references/handoff.md`** — `charter export` (offline artifact) and `charter handoff` (→ plain
  CommonMark; the `--answers` JSON shape; Open-question vs Answered).
