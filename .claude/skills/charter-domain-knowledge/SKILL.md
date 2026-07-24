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

- **Deliverable = block-structured markdown** (`.charter.md`), rendered to one portable HTML artifact.
  Blocks are CommonMark prose plus `:::` directive containers (Markdig `CustomContainer`), each validated
  against a C# record.
- **Block catalog:** prose/heading/list, `:::note`/`:::warn`, tables + `:::comparison`, fenced code +
  `:::diff`, `:::diagram` (Mermaid), `:::custom-html` (escape hatch), and **`:::question`** (the
  elicitation block). The normative catalog — including the fact that there is **no** `:::annotated-code`
  or `:::file-tree` (they have no renderer) — is single-sourced in the **`charter-format`** skill; cite it,
  don't fork it.
- **`:::question` (elicitation):** body is a validated **JSON** payload — `id`, `title`, `mode`, `options`,
  `target`, and an optional `answer` (its presence marks the question resolved). The `mode` tokens and full
  schema are normative in the **`charter-format`** skill (cite it, don't restate). Renders to a native HTML
  `<form>`; submitting posts structured answers back through the review loop. Reproduces **visual-plan's
  `question-form`** — the input gap this fills is in *base markdown* (CommonMark has no input primitive),
  not in visual-plan (which elicits via `question-form` and its `visual-intake` mode).
- **Anchors + source-map:** every block gets a content-derived **stable ID**. The renderer carries a
  **source-map (anchor ID → markdown line range)** so a human annotation on the *rendered HTML*
  round-trips to the *markdown source* the agent edits. This is the deepest correctness concern —
  Charter splits source (markdown) from render (HTML), which Lavish never did.
- **Session:** keyed by canonicalized artifact path; holds queued prompts + annotations. Loopback-only,
  guarded by a per-session capability key.

## Review-loop semantics

Author `.charter.md` → `charter render` → `charter review <file>` serves the artifact on `127.0.0.1` (SDK
injected at serve time) and opens the browser → human annotates elements / text ranges / diagram nodes and
submits question answers → those post to the local server → `charter poll` drains them (with the source
anchor) to the agent, and `poll --apply` / `charter resolve` fold answers **inline** into the `:::question`
blocks → agent edits the markdown → live reload re-renders. Loop until the human approves. The saved
artifact never contains the SDK, so it opens standalone.

## The workflow

**AUTHOR → REVIEW → HANDOFF.** The handoff to Guardrails is **dual** (Architecture B, of record): the
**interactive** `/plan-breakdown` consumes the `.charter.md` **directly**, interpreting the `:::` blocks
via the `charter-format` skill; the **headless/autonomous** path consumes the retained **flattened**
`charter handoff` output — plain CommonMark with each `:::question` resolved from its inline `answer` (or a
`--answers` file) and open questions clearly flagged. So Charter's directives DO reach Guardrails on the
interactive path (through the shared `charter-format` skill), while the flattened markdown stays the
contract for the headless path.

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
5. **Dual handoff to Guardrails** — the interactive `/plan-breakdown` reads the `.charter.md` directly
   (interpreting `:::` blocks via the `charter-format` skill); the headless/autonomous path consumes the
   retained flattened `charter handoff` plain-CommonMark output. (Architecture B — flipped from the earlier
   "plain-markdown-only handoff"; see `docs/plans/02-architecture-b-living-document.md`.) **Guardrails
   compatibility:** the direct path requires **Guardrails ≥ `1.0.0-preview.48`** (the release implementing
   #390–393); against any earlier Guardrails, use `charter handoff` → flattened `plan.md` (no version floor,
   supported permanently). This is a **documentation** compat note, not a code pin — Charter never invokes
   Guardrails; the actual gate is the `charter-format` format-version range checked on the Guardrails side.
6. **Narrow C#↔JS boundary** — browser logic isolated in `sdk/`.
7. **Telemetry: none in v1; vendor-neutral if ever** — no vendor-SDK lock-in. A default-*off* flag
   does not prevent lock-in (the dependency compiles in regardless); the safeguard is not adding a
   vendor SDK. Deliberate departure from Lavish's default-on. (Plan → *Trust, security & telemetry*; #6.)

## Where truth lives

| Question | Authoritative source |
|---|---|
| Block catalog + `:::question` schema (normative, drift-tested) | skill `charter-format` |
| Architecture, milestones, decisions D1/D2 | `docs/plans/01-combine-lavish-and-visual-plan.md` |
| Living-document / dual-handoff design (Architecture B) | `docs/plans/02-architecture-b-living-document.md` |
| Build / test / package / distribution / gotchas | skill `charter-dev-knowledge` |
| Format rationale (vs alternatives) | plan D1 + the format-research verdict it cites |
| Guardrails handoff shape | to be pinned as a fixture in M0 |

## Status (update as milestones complete)

- **Released — v0.2.0 GA** on all channels (Homebrew, NuGet `dotnet` tool, native binaries): the Architecture B living-document release. Ships the renderer + source-map, loopback review server, in-place annotation loop, offline export, and the full **living-document loop** — `.charter.md` format + `charter-format` skill + `charter-format-version` marker; `charter skills install`; `charter poll --apply` / `charter resolve` fold reviewer answers back **into the `.charter.md`** (durable sidecar + peek→apply→commit, nothing lost); `charter handoff` (flatten); and **`charter convert`** (#17), the mechanical Markdown→`.charter.md` seed the agent-driven `authoring-from-source` on-ramp enriches (the rich "any source → plan" path is a **skill**, not a CLI — the LLM stays out of the binary). (v0.1.0 was the prior GA, pre-Architecture-B.)
- **Pending** — Guardrails' interactive direct-ingestion of `.charter.md` (Guardrails #390–393, their team; targeted for Guardrails `1.0.0-preview.48` — Charter's producer side is complete, so this is unblocked on their schedule); macOS signing (#9); v2 features (#1–#6).
- **Decisions made** — D1 (markdown+directives hybrid), D2 (reimplement lean in C#).
