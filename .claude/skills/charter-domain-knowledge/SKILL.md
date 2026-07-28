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
- **The renderer emits a COMPLETE, STYLED document, shared by render/review/export.** `CharterRenderer.Render`
  wraps the block body in the single shared shell (`CharterDocument.Wrap`: doctype/html/head/body + one inline
  `<style>` from the bundled `assets/charter.css`); `render`, `review`, and `export` all go through it, so all
  three are complete and styled — never a bare fragment. Only `export` stamps a CSP meta (its strict offline
  policy); the review server supplies the served-page CSP as an HTTP header. Mermaid is inlined parse-safe and
  inits with `securityLevel: 'antiscript'` (inline SVG under CSP, no sandboxed iframe). (Was Charter #37/#38.)
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
  Charter splits source (markdown) from render (HTML), which Lavish never did. Two rules keep it honest,
  both of them "**orphan loudly rather than misattribute silently**":
  - **The handoff line is resolved at DRAIN time, never at submit time** (was Charter #49). One kernel,
    `AnchorResolution` (`src/Charter.Server/AnchorResolution.cs`), re-binds every anchor to the plan **as it
    is at handoff** — on the `/api/poll` drain, and again in `charter poll --apply` after its own write. The
    line stored at submit is a snapshot for the reviewer's in-page panel only. A stale line makes the agent
    edit the wrong block, confidently.
  - **Duplicate-content anchors are discriminated CONTEXTUALLY, not by occurrence index** (was Charter #50).
    Identical content recurring in a document is disambiguated by a hash of the preceding slot's assigned id
    (plus the length of its run of adjacent identical siblings) — so inserting an identical block elsewhere no
    longer renumbers the existing ones onto each other's notes. Cost: a duplicate's id can change when a
    *neighbour* changes, which orphans (detectable) rather than misattributes (not).
  - **Chrome around a block must stay anchor-invisible.** A wide table renders inside
    `<div class="table-scroll" tabindex="0" role="region" aria-label="Table">` (was Charter #68), and that
    wrapper deliberately carries **no** anchor id — the `<table id="…">` keeps it, so annotation targeting is
    unchanged. Any future wrapper must follow the same rule.
- **Session:** keyed by canonicalized artifact path; holds queued prompts + annotations. Loopback-only,
  guarded by a per-session capability key.

## Review-loop semantics

Author `.charter.md` → `charter render` → `charter review <file>` serves the artifact on `127.0.0.1` (SDK
injected at serve time) and opens the browser → human annotates elements / text ranges / diagram nodes and
submits question answers → those post to the local server → `charter poll` drains them (with the source
anchor) to the agent, and `poll --apply` / `charter resolve` fold answers **inline** into the `:::question`
blocks → agent edits the markdown → live reload re-renders. Loop until the human approves. The saved
artifact never contains the SDK, so it opens standalone.

**The drained annotation's wire shape.** Each drained annotation carries `anchorId`, `kind`, `note`, the
sub-part fidelity fields (`quote`/`start`/`end`/`nodeId`), `sourceLine` — **resolved at drain time** — and
**`anchorStatus`**: `"resolved"` (with a line) or `"orphaned"` (`sourceLine: null`, meaning *the block you
commented on has changed*; the stored `quote`/`nodeId`/note are the recovery hints). `anchorStatus` is
*derived* from `sourceLine`, so the two can never disagree, and it is additive — a consumer that ignores it is
unaffected. An agent must never treat an orphaned annotation as "no feedback"; it is feedback whose target
moved.

**In-page annotation UI (the reviewer's surface).** Notes are written in a styled, near-target composer
(never a native `window.prompt`), and the SDK renders a **review panel** listing the notes plus an on-block
marker + count badge, so the reviewer can see and manage what they have already said. The panel is the
**pre-drain queue**: an annotation it lists is by definition *not yet handed off*. It is backed by three
loopback routes over the same pending buffer — `GET /api/annotations` (non-destructive list, key on the
query string), `POST /api/{key}/annotations/{id}` (edit the note) and
`POST /api/{key}/annotations/{id}/delete` (retract it), both writes key-in-path + CSRF-gated. Once
`charter poll` drains a note it belongs to the agent: with **no review-log writer** configured, edit/delete
then answer **404**, which the UI reports as "already handed off", not as an error. (With a writer — the
production `charter review` path — the durable log still knows the id, so the write succeeds and appends an
`edit`/`retract` record instead; see the team-review section below, and note a live-session agent polling
`/api/poll` does **not** read the log, so a retraction after the drain does not reach it.) The drain contract
(`/api/poll`, `charter poll`, `PollEnvelope`) is unchanged, and the panel/markers/composer are runtime-only
DOM — invariant 1 still holds.

**A `:::diagram` has exactly TWO annotatable granularities, and BOTH anchor to the block.** A diagram renders
as `<pre class="mermaid" id="<stable charter id>">` whose content Mermaid then replaces with an `<svg>`
carrying **its own** generated ids (on the svg and on every `g.node`). Those are not Charter anchors —
`SourceMap.LineForAnchor` cannot map one, and they change on every render. So: Alt+click a **node** ⇒ a
`diagram-node` note whose `anchorId` is the **block** and whose `nodeId` carries the Mermaid node
(was Charter #48, where `anchorId` was the Mermaid id and the agent got **no `sourceLine` at all**);
Alt+click **anywhere else in the block** — svg background, padding, an edge ⇒ the ordinary `element` note
every other block produces (was Charter #60, where a diagram was the one block type with no whole-block
annotation). The composer's context line is the only thing distinguishing them for the reviewer, so it names
which one explicitly. A diagram is **never** text-range annotatable: it carries no prose, and Chromium's
word-select fallback on its background used to fabricate a text-range note over unrelated text elsewhere on
the page (was Charter #61). The SDK also refuses any text-range whose selection does not include the element
the reviewer's gesture ended on.

**"Unanswered" is a state a reviewer can return a `:::question` to.** Clicking the already-selected radio
clears it (Space does too — Blink dispatches no click for that gesture, so the SDK handles keyup itself).
On an **open** question that just restores "nothing to save"; on an **answered** one it is a real, submittable
retraction — Save renames itself to **Clear answer** and posts `values: []`, which `charter-format` reads as
open again. A reviewer who may freely *change* a settled decision must be able to *withdraw* it, and a form
showing nothing selected while the server still held an answer would be a lying UI (Charter #63).

**The round HAND-OFF ("Send to agent").** The reviewer can say *"I am done with this round"* without leaving
the page: `POST /api/{key}/review/submit` records a hand-off and wakes the long-poll, `GET /api/review`
reports it plus the live pending counts, `POST /api/{key}/review/ack?sequence=N` clears it by
compare-and-clear. It rides the poll envelope as the additive `reviewSubmitted` / `reviewSubmission` pair.
It **signals only** — the drafting agent stays the single writer of the plan. The wake-signal invariant is
stated ONCE, in `PendingSignal`: *the signal is completed iff the owning store has pending work*,
re-established under the owner's lock after **every** mutation. All three review stores (annotations,
answers, hand-off) use it and `ReviewServer.WaitForReviewWorkAsync` waits on all three; a store that skips
the re-sync either hot-loops `poll --wait` or strands it until timeout. That is what makes **answers wake
`poll --wait`** (was Charter #62): waiting on the annotation store alone made the reviewer's *decisions* —
the highest-value signal Charter carries — its slowest, sitting queued until the ~30 s timeout.

**What the AGENT must do with all this** (the consumption contract, and the part most easily missed):

- **Branch on `charter poll`'s exit code, never on an empty array.** `0` drained · `2` clean-empty ·
  `3` no live session *and* no readable review log (also the ambiguous >1-session refusal) · `4` a drain
  **could not complete** — queue state UNKNOWN · `5` `--apply` refused (answers preserved, never
  committed). `1` is the generic verb error. Normative in `src/Charter.Cli/ReviewExitCodes.cs`;
  `charter resolve` shares them. **A `4` still emits `"annotations": []`** — the envelope's `drainError`
  (non-null) is what distinguishes "nothing queued" from "we don't know", and treating them alike is how an
  agent hands off a plan nobody approved.
- **Check `reviewSubmitted` on every poll.** `true` = the human clicked **Send to agent**: *this round is
  complete, do the substantial revision*. `false` = you woke on incremental feedback and the reviewer is
  still working. Onboarding that skips this leaves an agent unable to tell the two apart, which is the whole
  point of the hand-off.
- The marker is **peek + ack**: reported once, acked after the envelope is written, and acked **only on a
  clean drain** (a non-null `drainError` leaves it standing). Delivery is at-least-once — a repeated
  `sequence` is the same round, not a second one.
- **`anchorStatus: "orphaned"` is neutral, not an error and not proof the note was addressed** (§4.3).

**Git-mediated team review (the durable half).** Comments also become **per-author append-only JSONL**
records in `<plan>.review/<slug>.<hash8>.jsonl` beside the plan, so review travels by git instead of dying
in a machine-local sidecar. Normative design: `docs/plans/03-git-mediated-team-review.md` — cite it, don't
restate it. What the code surface is:

- `GET /api/review-log` — every author's logs, folded and projected **server-side**. There is deliberately
  **no static-file branch** for `.review/`: the confinement root is the plan's own directory, so one would
  make every sibling file under `docs/plans/` a key-gated HTTP-readable resource.
- `POST /api/{key}/annotations/{id}/resolve` — appends a `resolve` record. Open to anyone (review is
  collaborative) and always attributed; `retract` (the existing `/delete`) is refused for anyone but the
  comment's own author. Orthogonal to the round hand-off: a resolve settles one comment forever, a hand-off
  marks one round of one live session.
- The `/events` stream now names its frames: **`reload`** (the plan changed — navigate) vs **`review-log`**
  (a teammate's log landed, e.g. a `git pull` — re-read the fold only). Keeping them distinct is what stops
  a pulled log discarding a half-typed note or an unsaved answer.
- Anchors resolve by **exact block-id match or `orphaned`** — no fuzzy ladder — and an orphan is a neutral
  fact, never "addressed".
- `charter poll <plan>` gained a **server-less read path**: with no live session it folds
  `<plan>.review/*.jsonl` and emits the same envelope with the additive `source: "review-log"` (else
  `"session"`) and a per-annotation `review { authorName, authorEmail, actor, status, ts }`. Consumption is
  tracked in a **machine-local** ledger (`StateDirectory.Consumed()`), never as a log record — A's agent
  consuming must not mark a comment handled for B. **A live session always takes precedence**; the log is
  read only when none is live, and only when a `<plan>` is named (bare `poll`, `--url` and `--session` are
  session-discovery paths and never read it). `--apply` is inert here — a log has comments, not an answer
  queue. Exit codes are the same 0/2/4, so this path **returns 0/2/4 where it used to return 3**.
- **`status` (`ReviewStatusTokens`) is load-bearing on the wire:** `open` · `resolved` · `contested` ·
  `retracted`. **`contested` is NOT resolved** — concurrent resolve+reopen, neither having observed the
  other — and execution must treat it as open (§4.2); `retracted` is a withdrawal whose body reads
  `(comment withdrawn by author)`. Note **`charter handoff` does not read the review log at all**: honouring
  "a contested comment blocks handoff" is currently the *agent's* responsibility, not a code gate.
- A later `edit`/`reply`/`resolve`/`reopen`/`retract` mints a new record id, so a comment already delivered
  becomes **deliverable again** with its new status — intended (something new is being said about it), and a
  consumer must not treat the repeat as a duplicate to suppress.
- **Who writes, precisely** (do not conflate the two): the *library* option `ReviewServerOptions.ReviewLog`
  defaults `null`, and with no writer the server behaves bit-identically to the pre-log server. But the
  **`charter review` CLI always supplies one** (`Program.OpenReviewLog`) — it resolves the author from
  `git config user.name`/`user.email` (read-only; falling back to a marked `@localhost` identity with a
  warning), **creates `<plan>.review/` eagerly** via `EnsureDirectory()` before any comment exists, and
  prints one stderr line naming the log and stating the records are meant to be COMMITTED and are permanent.
  An unwritable directory only warns and reviews local-only — a review never fails for want of a log. So
  "opt-in" describes the API, **not** the shipped user experience; the user-facing opt-out is gitignoring
  `*.review/` (§7).
- Per **§5.0 the solo path must not regress**: the §5.1 nag warnings are to fire only when `.review/` is
  actually *tracked*. Those warnings are **step 5 and not built yet** — see Status.

## The workflow

**AUTHOR → REVIEW → HANDOFF.** The handoff to Guardrails is **dual** (Architecture B, of record): the
**interactive** `/plan-breakdown` consumes the `.charter.md` **directly**, interpreting the `:::` blocks
via the `charter-format` skill; the **headless/autonomous** path consumes the retained **flattened**
`charter handoff` output — plain CommonMark with each `:::question` resolved from its inline `answer` (or a
`--answers` file) and open questions clearly flagged. So Charter's directives DO reach Guardrails on the
interactive path (through the shared `charter-format` skill), while the flattened markdown stays the
contract for the headless path.

**The flatten must be LOSSLESS in the two things the breakdown routes on** (Charter #48, found by the first
real end-to-end Charter → Guardrails verification):

- **Every `:::question` emits a status line PLUS a metadata line**, identical in shape whether answered or
  open: `` _Question — id: `x`; mode: `single`; target: `human`; options: `A`, `B`_ ``. `options` are the
  *rationale* a resolved answer is folded in with (the rejected option is what a guardrail can be written
  against), and `target` is what lets the headless path honour `target: agent` instead of halting for a
  human. Both used to be dropped. Shape lives in `skills/charter/references/handoff.md`.
- **`:::diagram` / `:::diff` flatten to EXACTLY ONE fence.** Both body forms — raw, or already wrapped in
  ` ```mermaid ` / ` ```diff ` — are accepted (`charter-format`), and an already-fenced body is unwrapped
  before emitting, never double-fenced (a double fence makes the inner fence literal, so the diagram does
  not render on GitHub).
- **An unknown `:::foo` keeps its BODY** — flagged as unknown, body preserved as blockquoted prose, never
  silently dropped. Same rule in the renderer (a `<pre>` under the unknown-directive marker).
- **A RESOLVED question RENDERS as resolved** — `class="question answered"` + `data-answered="true"`, the
  chosen value(s) pre-selected, an "Answered" chip in the legend — so a second review round does not re-ask
  a settled decision. An answer matching no declared option is surfaced as a checked write-in, never dropped.
- **Duplicate `:::question` ids warn early** (stderr, non-fatal, exit code unchanged) from `render`,
  `review`, and `handoff`, not only at answer-drain.

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
   markdown source lines; they survive re-render of unrelated blocks. The line handed to the agent is
   resolved **at drain time**, and an anchor that no longer resolves is reported as an explicit orphan —
   never as a stale-but-confident line number, and never as a different block.
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
| Git-mediated team review: log layout, record schema, the 8 fold rules, `prev`/contested, exact-hash-or-orphan | `docs/plans/03-git-mediated-team-review.md` |
| Build / test / package / distribution / gotchas | skill `charter-dev-knowledge` |
| Format rationale (vs alternatives) | plan D1 + the format-research verdict it cites |
| Guardrails handoff shape | to be pinned as a fixture in M0 |

## Status (update as milestones complete)

- **Released — v0.2.0 GA** on all channels (Homebrew, NuGet `dotnet` tool, native binaries): the Architecture B living-document release. Ships the renderer + source-map, loopback review server, in-place annotation loop, offline export, and the full **living-document loop** — `.charter.md` format + `charter-format` skill + `charter-format-version` marker; `charter skills install`; `charter poll --apply` / `charter resolve` fold reviewer answers back **into the `.charter.md`** (durable sidecar + peek→apply→commit, nothing lost); `charter handoff` (flatten); and **`charter convert`** (#17), the mechanical Markdown→`.charter.md` seed the agent-driven `authoring-from-source` on-ramp enriches (the rich "any source → plan" path is a **skill**, not a CLI — the LLM stays out of the binary). (v0.1.0 was the prior GA, pre-Architecture-B.)
- **Current version — 0.6.0** (`charter --version`). Landed since 0.2.0: the in-page **review panel** (#42) with pre-drain edit/retract; **answerable `:::question` forms** (#57/#58 — a real `Save answer` submit, `data-question-mode`, `free-text`/`number` no longer 400); **answers wake `poll --wait`** (#62); the in-page **Send to agent** round hand-off (#64); wide tables in a **scroll wrapper** (#68); and **git-mediated team review steps 2–4** — the per-author JSONL writer, `GET /api/review-log` + the panel's author/actor/contested/orphaned rendering, `POST /api/{key}/annotations/{id}/resolve`, and the server-less `charter poll` read path.
- **Team review — built vs NOT built** (`docs/plans/03-git-mediated-team-review.md` §9): steps 1–4 are in. **Step 5 is NOT** — there is **no `charter review verify` verb**, no stale-plan/upstream warning, no uncommitted-records reminder, and no orphan diff (an orphan shows its `quote`, never a diff). Steps 6 (agent `reply`) and 7 (the two-author browser test) are also outstanding. Do not describe any of these as shipped.
- **Pending** — Guardrails' interactive direct-ingestion of `.charter.md` (Guardrails #390–393, their team; targeted for Guardrails `1.0.0-preview.48` — Charter's producer side is complete, so this is unblocked on their schedule); macOS signing (#9); v2 features (#1–#6).
- **Decisions made** — D1 (markdown+directives hybrid), D2 (reimplement lean in C#).
