---
name: charter
description: Use when you must turn a task into a reviewable plan with Charter — author a block-structured .mdx, render/serve it for a human to annotate in the browser, then hand off plain CommonMark to Guardrails. Covers the author → review → handoff workflow and the block catalog.
---

# Charter — author → review → handoff

Charter is the front door of an agentic delivery pipeline (`Charter → Guardrails → delivery`). **You**
(the drafting agent) author a rich, block-structured **plan** as markdown-with-directives; a **human**
reviews it in the browser and **comments in place** (notes anchored to the exact block); you drain that
feedback and revise; then you hand the approved plan to Guardrails, which breaks it into a task DAG.

This skill teaches you to *drive* Charter. You interact with it through exactly four CLI verbs plus two
HTTP endpoints on the review server — nothing else exists. Ground anything ambiguous in
`src/Charter.Cli/Program.cs` (the verb list) and `docs/plans/01-combine-lavish-and-visual-plan.md` (the
block catalog and load-bearing invariants — the single source of truth this skill cites, never forks).

## When to use

Use this skill when the human asks you to **plan** a change and wants to **review it visually before it
executes** — anything of the form "draft a plan I can look over," "write this up as a reviewable plan,"
"put this in front of me before you build it," or "get this ready for Guardrails." Reach for it whenever
the deliverable is a *plan a human approves in the browser*, not code you write directly.

Do **not** use it for work that should just be done, for prose with no decisions to elicit, or for
reporting on work already finished.

## The CLI surface (the only verbs that exist)

| Verb | What it does |
|---|---|
| `charter render <plan.mdx> -o <out.html>` | Render the plan to **one portable** HTML artifact. |
| `charter review <plan.mdx> [--no-open]` | Serve the rendered + SDK-injected plan over the **loopback** review server and open the browser for in-place annotation. |
| `charter export <plan.mdx> -o <out.html>` | Write a **self-contained, offline** HTML artifact (local assets inlined, local paths scrubbed, SDK-free). |
| `charter handoff <plan.mdx> -o <out.md> [--answers <answers.json>]` | Convert the plan's `:::` directives to **plain CommonMark** for Guardrails `plan-breakdown`. |
| `charter --version` | Print the version. |

There is **no** CLI verb for reading the human's feedback. The review server exposes it over HTTP, and
you drain it there — see [The review loop](#the-review-loop) and `references/review-loop.md`.

## The workflow: AUTHOR → REVIEW → HANDOFF

### 1. AUTHOR — write the plan, then `charter render`

Write the plan as a `.mdx` file using the [block catalog](#block-catalog). Then render it to check the
artifact:

```
charter render plan.mdx -o plan.html
```

This produces **one portable HTML file** — it opens standalone in any browser. The annotation SDK is
injected **only at serve time**, never baked into this file (invariant 1: *portable artifact*). Rendering
is your fast inner loop while drafting; open `plan.html` yourself to sanity-check layout before you put
it in front of the human.

### 2. REVIEW — `charter review`, then drain feedback over HTTP

```
charter review plan.mdx
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
`:::question` blocks. You read that feedback back by **draining the server's HTTP endpoints** — there is
no CLI verb for this:

- **`GET /api/poll?key=<key>`** — long-polls (~30 s) and returns the queued **annotations** as a JSON
  array; each carries the resolved **markdown source line** so you know exactly which line to edit.
- **`GET /api/answers?key=<key>`** — drains the queued **`:::question` answers** as a JSON array.

Edit the markdown source in response; the server re-renders from source on the next request (live
reload), so the human sees your revision without restarting. Loop — poll, revise, let them re-review —
until the plan is approved. The exact JSON shapes, the long-poll semantics, and a concrete drain loop are
in `references/review-loop.md`.

### 3. HANDOFF — `charter export` (optional) then `charter handoff`

Optionally capture a shareable snapshot of the approved plan:

```
charter export plan.mdx -o plan.html
```

`export` writes a **truly offline** artifact — local assets inlined as `data:` URIs, local paths
scrubbed, SDK-free — so it can be attached or archived and still opens with no server (distinct from
`render`, which leaves local asset references as-is).

Then convert the approved plan to the shape Guardrails consumes:

```
charter handoff plan.mdx -o plan.md --answers answers.json
```

`handoff` rewrites every `:::` directive (`:::note`, `:::warn`, `:::comparison`, `:::diagram`,
`:::question`, …) into **plain CommonMark** — Guardrails does *not* parse Charter's directives (invariant
5: *feeds Guardrails via plain markdown*). Each `:::question` resolves against the optional `--answers`
JSON: supplied answers become an **"Answered:"** line; anything left unanswered becomes an **"Open
question"** line. Omit `--answers` and every question hands off as open. The `--answers` shape and the
Open-vs-Answered rendering are in `references/handoff.md`.

## Block catalog

Blocks are **CommonMark prose plus `:::` directive containers**. The catalog is single-sourced in
`docs/plans/01-combine-lavish-and-visual-plan.md` (§ *Format & block catalog*, invariant 3: *format
single-sourced*) — this table cites it; the renderer owns it. Do not fork or invent directives.

| Block | Directive |
|---|---|
| prose / heading / list | plain markdown |
| callout | `:::note` / `:::warn` |
| table / comparison | pipe tables · `:::comparison` |
| code / diff | fenced ` ```lang ` · `:::diff` |
| annotated code | `:::annotated-code {#id}` |
| file tree | `:::file-tree` |
| diagram | `:::diagram` (Mermaid body) — annotatable per node, pan/zoom |
| wireframe / escape hatch | `:::custom-html` (sanitized inline HTML) |
| **question (elicitation)** | **`:::question`** |

**`:::question`** is the elicitation block — how you ask the human to *decide* something inside the plan.
Its body is a validated payload: each question has an `id`, a `title`, a `mode`
(`single-select` / `multi-select` / `free-text` / `boolean` / `number`), `options`, and a `target`
(`human` or `agent`). It renders to a native HTML `<form>`; submitting posts structured answers that you
drain from `GET /api/answers` and later resolve at handoff. Every block also gets a content-derived
**stable ID** and a **source-map** back to its markdown line range, which is what lets an annotation on
the rendered HTML round-trip to the source line you edit.

The full catalog with each block's syntax, the `:::question` schema in depth, and a sample `.mdx`
skeleton are in `references/authoring-plans.md`.

## References

Keep this file lean; the depth lives in `references/`:

- **`references/authoring-plans.md`** — the block catalog in depth + a short sample `.mdx` skeleton.
- **`references/review-loop.md`** — running `charter review`, in-browser annotation, and draining
  feedback via `GET /api/poll` and `GET /api/answers` on the loopback server.
- **`references/handoff.md`** — `charter export` (offline artifact) and `charter handoff` (→ plain
  CommonMark; the `--answers` JSON shape; Open-question vs Answered).
