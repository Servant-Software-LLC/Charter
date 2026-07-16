---
name: charter-domain-knowledge
description: |
  Charter product knowledge for all agents working in this repo. Use when working on
  anything related to Charter:
  - The deliverable model: block-structured markdown, the block catalog, the question block
  - The comment-in-place review loop, anchors, and the anchor→markdown source-map
  - The author → review → handoff workflow and the Guardrails handoff contract
  - The settled format decision (markdown+directives) and why not the alternatives
  - Load-bearing invariants, where-truth-lives, and roadmap/status

  Provides: the mental model, review-loop semantics, the format rationale, and pointers
  to the single-source-of-truth documents.

  SELF-UPDATING: When your work changes the deliverable model, block catalog, review-loop
  semantics, format decision, handoff contract, or roadmap, you MUST update the affected
  section(s) here before completing your task.
---

# Charter Domain Knowledge

## Quick Reference

**What is Charter?** The front door of an agentic delivery pipeline (`Charter → Guardrails →
firstmate/gnhf`). An AI authors a rich, block-structured **plan deliverable**; a human reviews it in
the browser and **comments in place** (notes anchored to the exact block); the reviewed plan feeds
Guardrails, which breaks it into a verified task DAG.

**The bet:** the agent should be *visually expressive* (diagrams, tables, comparisons, code) **and**
able to *elicit structured decisions* from the human, and the human's feedback should carry the
context of exactly what it points at. Charter combines **Lavish**'s comment-in-place review loop with
**visual-plan**'s block authoring, C#-native.

## The model

- **Deliverable = block-structured markdown** (`.mdx`), rendered to one portable HTML artifact. Blocks
  are CommonMark prose plus `:::` directive containers (Markdig `CustomContainer`), each validated
  against a C# record.
- **Block catalog:** prose/heading/list, `:::note`/`:::warn`, tables + `:::comparison`, fenced code +
  `:::diff`, `:::annotated-code`, `:::file-tree`, `:::diagram` (Mermaid), `:::custom-html` (escape
  hatch), and **`:::question`** (the elicitation block).
- **`:::question` (elicitation):** body is a validated payload — each question has `id`, `title`,
  `mode` (single-select / multi-select / free-text / boolean / number), `options`, and a `target`
  (`human` / `agent`). Renders to a native HTML `<form>`; submitting posts structured answers back
  through the review loop.
- **Anchors + source-map:** every block gets a content-derived **stable ID**. The renderer carries a
  **source-map (anchor ID → markdown line range)** so a human annotation on the *rendered HTML*
  round-trips to the *markdown source* the agent edits. This is the deepest correctness concern —
  Charter splits source (markdown) from render (HTML), which Lavish never did.
- **Session:** keyed by canonicalized artifact path; holds queued prompts + annotations. Loopback-only,
  guarded by a per-session capability key.

## Review-loop semantics

Author `.mdx` → `charter render` → `charter <file>` serves the artifact on `127.0.0.1` (SDK injected
at serve time) and opens the browser → human annotates elements / text ranges / diagram nodes and
submits question answers → those post to the local server → `charter poll` long-polls and returns them
(with the source anchor) to the agent → agent edits the markdown → live reload re-renders. Loop until
the human approves. The saved artifact never contains the SDK, so it opens standalone.

## The workflow

**AUTHOR → REVIEW → HANDOFF.** The reviewed, approved deliverable is emitted as **canonical reviewed
markdown** (plain markdown + resolved decisions) and handed to Guardrails `plan-breakdown`. The handoff
is plain markdown by design — Guardrails is NOT extended to parse Charter's directives (that would
couple two independently-versioned formats for no benefit).

## Format decision (settled)

**markdown + directives (Markdig), as a deliberate hybrid** — chosen over MDX, Adaptive Cards, JSON
Forms, raw HTML, notebooks, AsciiDoc/RST, and slides. Key rationale: the essence of "MDX blocks" is a
validated block *schema* (Builder.io validates with Zod), not JSX; real MDX cannot run in C#; so
markdown+Markdig validated against C# records is the correct C# reproduction. Narrative stays free-form
(strict format degrades LLM reasoning); the rigid schema is confined to `:::question` where reliability
matters; `:::custom-html` is the raw-HTML escape hatch. No more-expressive *viable* standard exists.
Full study: `docs/plans/01-combine-lavish-and-visual-plan.md` (decision D1).

## Load-bearing invariants

1. **Portable artifact** — opens standalone; SDK injected only at serve time.
2. **Comment-in-place with round-trip** — annotations anchor to stable block IDs and map back to
   markdown source lines; they survive re-render of unrelated blocks.
3. **Format single-sourced** — the block schema lives in one place; renderer, SDK, and skill cite it.
4. **Loopback + capability** — `127.0.0.1` default, per-session capability key, path-confined serving.
5. **Feeds Guardrails via plain markdown** — no MDX crosses the handoff.
6. **Narrow C#↔JS boundary** — browser logic isolated in `sdk/`.
7. **Telemetry off / opt-in** — deliberate departure from Lavish's default-on.

## Where truth lives

| Question | Authoritative source |
|---|---|
| Architecture, milestones, decisions D1/D2 | `docs/plans/01-combine-lavish-and-visual-plan.md` |
| Build / test / package / distribution / gotchas | skill `charter-dev-knowledge` |
| Format rationale (vs alternatives) | plan D1 + the format-research verdict it cites |
| Guardrails handoff shape | to be pinned as a fixture in M0 |

## Status (update as milestones complete)

- **Scaffold complete** — .NET solution, CI/release/tap pipeline (validated green), installers.
- **Decisions made** — D1 (markdown+directives hybrid), D2 (reimplement lean in C#).
- **Not yet built** — renderer, source-map, review server, annotation loop (M0 spike is next).
