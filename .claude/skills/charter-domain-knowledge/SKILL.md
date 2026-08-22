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

**The bet:** the agent should be *visually expressive* **and** able to *elicit structured decisions*, and the
human's feedback should carry the context of exactly what it points at. Charter combines **Lavish**'s
comment-in-place review loop with **visual-plan**'s block authoring, C#-native. (Positioning vs both:
`docs/why-charter.md`.)

## Solo is the primary use case — and that is binding

`docs/plans/03-git-mediated-team-review.md` **§5.0** is a product constraint, not a courtesy: **one
person using Charter alone is the main use case, and team review is additive — it must never make solo
use heavier.** Concretely, every slice must honour all three:

- **No new required setup.** No git identity, no repo, no `.gitattributes`. A missing git identity
  falls back to a marked local identity and the review still works.
- **No per-session nagging.** Sharing-related warnings fire **only when the plan's `.review/`
  directory is actually tracked by git** — i.e. the reviewer opted in. Untracked, gitignored, or
  not-a-repo ⇒ **silent**.
- **No trace where nothing was said.** A `charter review` that writes no comment leaves **no
  `.review/` directory** beside the plan.

If a change would make the solo path louder, heavier, or messier, it is wrong regardless of how good
it is for teams. Check it against §5.0 before designing it.

## The model

- **Deliverable = block-structured markdown** (`.charter.md`), rendered to one portable HTML artifact.
  Blocks are CommonMark prose plus `:::` directive containers (Markdig `CustomContainer`), each validated
  against a C# record.
- **The renderer emits a COMPLETE, STYLED document, shared by render/review/export.** `CharterRenderer.Render`
  wraps the block body in the single shared shell (`CharterDocument.Wrap`: doctype/html/head/body + one inline
  `<style>` from the bundled `assets/charter.css`). Only `export` stamps a CSP meta (its strict offline
  policy); the review server supplies the served-page CSP as an HTTP header. Mermaid is inlined parse-safe and
  inits with `securityLevel: 'antiscript'` (inline SVG under CSP, no sandboxed iframe).
- **Block catalog and the `:::question` schema are normative in the `charter-format` skill** — including the
  fact that there is **no** `:::annotated-code` or `:::file-tree` (they have no renderer). Cite it; never fork
  it. A drift test binds the skill's catalog to `BlockKind` ∪ `QuestionSpec`, so a fork breaks the build.
- **`:::question` is the elicitation block**: a validated JSON body (`id`, `title`, `mode`, `options`,
  `target`, optional `answer` — whose presence marks it resolved) rendered as a native HTML `<form>`. The gap
  it fills is in *base markdown* (CommonMark has no input primitive), not in visual-plan, which already elicits
  via `question-form`.
- **Session:** keyed by canonicalized artifact path; holds queued prompts + annotations. Loopback-only,
  guarded by a per-session capability key.

### Anchors: orphan loudly rather than misattribute silently

Every block gets a content-derived **stable ID**, and the renderer carries a **source-map (anchor ID →
markdown line range)** so an annotation on the *rendered HTML* round-trips to the *markdown source* the agent
edits. This is the deepest correctness concern in the product — Charter splits source (markdown) from render
(HTML), which Lavish never did.

**The governing principle: a wrong attribution is unrecoverable and invisible; an orphan is visible and
carries its `quote`.** So every ambiguity resolves toward the orphan.

- **Resolution is exact-block-id-match or orphaned. There is no fuzzy ladder.** One was designed and
  **rejected**: quote-similarity / nearest-neighbour rebinding reintroduces exactly the #50 misattribution
  class it would be sold as fixing. Do not re-propose it.
- **The handoff line is resolved at DRAIN time, never at submit time** (#49). One kernel,
  `AnchorResolution` (`src/Charter.Server/AnchorResolution.cs`), re-binds every anchor to the plan **as it is
  at handoff** — on the `/api/poll` drain, and again in `charter poll --apply` after its own write. The line
  stored at submit is a snapshot for the reviewer's in-page panel only. A stale line makes the agent edit the
  wrong block, confidently. An unreadable plan resolves the whole batch as orphaned rather than carrying
  unverifiable lines.
- **Duplicate-content anchors are discriminated CONTEXTUALLY, not by occurrence index** (#50). Identical
  content is disambiguated by a hash of the preceding slot's assigned id (plus the length of its run of
  adjacent identical siblings), so inserting an identical block elsewhere no longer renumbers existing ones
  onto each other's notes. Cost: a duplicate's id can change when a *neighbour* changes — which orphans
  (detectable) rather than misattributes (not).
- **Chrome around a block must stay anchor-invisible.** Every clipping block resolves to a **scroll region** —
  a real element carrying `tabindex="0"`, `role="region"` and a name, single-sourced by `ScrollRegion`
  (`src/Charter.Core/CharterRenderer.cs`, #68/#87). There are **five**, and one rule decides each one's shape:
  **where the clipping element is one the renderer already owns, the affordance goes ON it and the anchor does
  not move** (a code block's `pre`; `pre.unknown-directive-body`); where it is not — a `<table>`, a diff's
  per-line rows, an author's verbatim HTML — the renderer emits a **wrapper** (`.table-scroll`, `.diff-scroll`,
  `.custom-html-scroll`) carrying **no** `id`, `data-anchor` or `data-charter-anchor`, so `closestAnchored`
  walks straight past it to the block's own anchor and `SourceMap` never sees it. A wrapper holding an anchor
  would silently re-target every note on the block; any future one follows the same rule. The SDK's own chrome
  obeys the mirror rule: marked with the `data-charter-ui` **attribute**, **no ids at all**, so even a guard bug
  degrades to "no anchor", never "the wrong anchor".
- **SDK chrome may be a block's SIBLING; it must never be a block's ANCESTOR** (#164) — the mirror of the rule
  above, for chrome that sits *outside* a block rather than inside it. `closestAnchored` tests
  `el.closest(UNANCHORABLE)` — which includes `[data-charter-ui]` — **before** it walks for an id, so a
  chrome-marked ancestor makes every Alt+click anywhere inside that block resolve to `null`: the block silently
  stops being annotatable at all. That is why the count badge on a `<table>`/`<ul>`/`<ol>`/`<hr>` (where a
  `<button>` child is invalid content) rides a zero-height `.charter-badge-rail` inserted as the block's
  **previous sibling**, and why wrapping the block in a positioned frame — at render time or at serve time — is
  rejected. Reparenting is doubly wrong at serve time: it discards `.table-scroll`'s `scrollLeft` and any focus
  inside it, and `render()` runs on every SSE frame, so a teammate's pulled note would blow away a scrolled,
  focused table. A rail is placed only where the climb from the anchor reaches a direct child of `<body>`
  without passing an ancestor that carries an anchor of its own.
- **The reachable-anchor set is exactly: top-level document nodes, plain top-level list items,
  `:::comparison` rows, and `:::diff` lines — nothing else.** `CharterMarkdown.SubAnchors` is the single
  descent that defines the sub-block half, and `AnchorAssignment` walks the same union, so an id the renderer
  emits is always one `SourceMap` registers. A nested list inside an `<li>`, a list inside `:::note`/`:::warn`,
  a `<tr>`, and an author's own id inside `:::custom-html` are **not** anchors: the first three are never
  stamped, and the last is accepted by `closestAnchored` (which takes any id) but never registered by
  `SourceMap`, so a note on it reaches the agent orphaned — which is why the SDK deliberately shows no count
  badge there rather than advertising a note that cannot round-trip.
- **`:::diagram` is deliberately OUTSIDE all of it** (`pre:not(.mermaid)` in `assets/charter.css`): its oversize
  failure is shrink-below-legibility, not clipping, and its answer is #51's **SDK-only** pan/zoom. A
  shared-stylesheet rule reaching `pre.mermaid` would put part of that affordance into the exported artifact and
  break invariant 1.
- **`:::diff` renders `div.diff > div.diff-scroll > div.diff-line`, and the per-line sub-anchor stays on
  `.diff-line`** — the region is invisible to `closestAnchored`, so a line annotation still posts that line's
  own `Block.StableId` with a real `sourceLine`. **The scroll moved INWARD rather than flipping `.diff`'s
  `overflow`**, and re-proposing that flip is the trap: the `hidden` is what clips each line's **full-bleed**
  add/del background to the card's radius *while scrolled*, **and** the SDK's whole-block annotation badge is
  `position: absolute` inside `.diff` — making the card the scroll container would make the badge **ride away
  with the content** (the #51 lesson).

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
commented on has changed*; the stored `quote`/`nodeId`/note are the recovery hints). Within one payload
`anchorStatus` is *derived* from `sourceLine`, so those two can never disagree. An agent must never treat an
orphaned annotation as "no feedback"; it is feedback whose target moved. Field-by-field shape:
`skills/charter/references/review-loop.md`.

**Every route that REPORTS an annotation re-resolves, so no two can disagree** (#78). `GET /api/annotations`
(the panel's list) and `/api/poll` (the drain) both pass through `AnchorResolution`, so `sourceLine` /
`anchorStatus` mean exactly one thing wherever you read them: *the anchor's line in the plan as it is now*.
They did not: the list route emitted the **submit-time** snapshot, and for the same annotation at the same
moment against a replaced plan it read `sourceLine: 1, "resolved"` while the drain read `null, "orphaned"` —
#49 resurfacing through a key-gated route documented as safe, with the agent editing the wrong block
confidently. There is deliberately **no submit-time twin field**: the reviewer reads the rendered page, not
line numbers, so a second field meaning "the line when you wrote this" would only rebuild the ambiguity under
another name. Binding test: `PanelDrainParityTests`.

Two field-level traps that have each cost a debugging session:

- **`kind` is HYPHENATED on the wire** — `element` · `text-range` · `diagram-node` — not camelCase. The C#
  enum goes through `AnnotationApi.AnnotationKindConverter` to match the SDK's tokens exactly (#15). An agent
  branching on `"textRange"` never matches — and **posting** an unrecognised token is now a **400 naming the
  three accepted tokens** (#79), not a 200 that silently stores `element`. That coercion was the real damage:
  a `text-range` submission was downgraded to a whole-element one while its `quote`/`start`/`end` stayed on the
  record contradicting the kind. camelCase is **not** an alias — one spelling per kind, because a second legal
  spelling forks the vocabulary the SDK emits and the skills document. An **absent** `kind` still means the
  whole block. `AnnotationApi.TryParseKind` is the strict ingress parse; `ParseKind` stays lenient *only* for
  reading a durable review-log record, where the alternative is dropping a comment somebody wrote.
- **`start`/`end` index the BLOCK's own text**, from the selection's `Range`, via a shared walker that skips
  every `[data-charter-ui]` subtree — one reference frame shared with the panel's quote lookup, so
  `end > start` always holds for a real selection. They are **not** `anchorOffset`/`focusOffset`, which live in
  different text nodes and once drained as `start: 146, end: 0` over a ~150-character quote (#56). When no
  honest offset can be computed both are **`null`** — never a misleading pair. `quote` always carries the
  human-readable target.

**In-page annotation UI.** Notes are written in a styled, near-target composer (never `window.prompt`), and
the SDK renders a **review panel** — the **pre-drain queue**, so an annotation it lists is by definition *not
yet handed off*. Three loopback routes back it over the same pending buffer: `GET /api/annotations` (list),
`POST /api/{key}/annotations/{id}` (edit), `POST /api/{key}/annotations/{id}/delete` (retract) — both writes
key-in-path + CSRF-gated. Once `charter poll` drains a note it belongs to the agent: with **no review-log
writer**, edit/delete then 404 and the UI says "already handed off". With a writer (the shipped `charter
review` path) the log still knows the id, so the write appends an `edit`/`retract` record instead — but a
live-session agent polling `/api/poll` does **not** read the log, so a post-drain retraction never reaches it.
The panel/markers/composer are runtime-only DOM, so invariant 1 holds.

**A `:::diagram` has exactly TWO annotatable granularities, and BOTH anchor to the block.** It renders as
`<pre class="mermaid" id="<stable charter id>">` whose content Mermaid replaces with an `<svg>` carrying **its
own** generated ids (on the svg and every `g.node`). Those are not Charter anchors: `SourceMap.LineForAnchor`
cannot map one, and they change on every render.

- Alt+click a **node** ⇒ a `diagram-node` note whose `anchorId` is the **block**, with the Mermaid node in
  `nodeId` (#48, where `anchorId` was the Mermaid id and the agent got **no `sourceLine` at all**).
- Alt+click **anywhere else in the block** — background, padding, an edge ⇒ the ordinary `element` note every
  other block produces (#60, where a diagram was the one block type with no whole-block annotation).
- **`nodeId` is Mermaid-GENERATED and UNSTABLE across renders — a recovery hint, never an anchor.** Do not key
  state on it or resolve it back to source. The block id is a diagram note's only stable identity.
- A diagram is **never** text-range annotatable: it carries no prose, and Chromium's word-select fallback on
  its background used to fabricate a text-range note over unrelated text elsewhere on the page (#61).

`closestAnchored` resolves the enclosing `pre.mermaid` at the **anchoring layer**, not per handler, so by
construction no path can escape carrying a Mermaid id. Both granularities share one anchor id, so the
composer's context line is all that distinguishes them for the reviewer — it names which one explicitly.

**An oversized `:::diagram` PANS and ZOOMS at review time, and only at review time** (#51). Mermaid renders
with `useMaxWidth`, so a diagram wider than the review column never overflows — it *shrinks*, until the
labels cannot be read and no scrollbar ever says so. The SDK detects exactly that (`viewBox` width vs
rendered width) and gives that block, and only that block, a zoom bar (`−` · % · `+` · Reset), a tab stop,
and the gestures: **Ctrl/⌘+wheel** zooms about the pointer (a plain wheel is never intercepted), a **drag**
pans once there is somewhere to pan, and **arrow keys** pan the focused block. **Alt stays the annotate
modifier at every zoom level** — a *drag* swallows the click that ends it, so panning can never open a
composer, while a *click* annotates exactly as before. A diagram that fits gains none of it. Two rules
make it safe (the third, "never take pointer capture", is an SDK trap — `charter-dev-knowledge` §4):

- **It is an SDK affordance, so the exported artifact still renders the diagram statically** (invariant 1).
  `charter.css` and the renderer are untouched by the feature; `Reset` and `dispose()` both restore the block
  to the markup the renderer emitted. Guarded from both sides: `DiagramPanZoomArtifactTests` (the artifact
  carries none of it) and `ServedDocumentShellTests` (the served page does) — either alone would hold with
  the feature simply absent.
- **Zooming WIDENS the `<svg>`; it never transforms it.** The block becomes an ordinary scroll container —
  the shape above, but built at runtime. That keeps the label text vector-crisp, keeps `getBoundingClientRect`
  and hit-testing simply correct so node annotation is unaffected, and makes a pan a real element scroll,
  which the overlay's existing capture-phase `scroll` listener already follows.

**"Unanswered" is a state a reviewer can return a `:::question` to.** Clicking the already-selected radio
clears it (Space does too — Blink dispatches no click for that gesture, so the SDK handles `keyup` itself).
On an **open** question that just restores "nothing to save"; on an **answered** one it is a real, submittable
retraction — Save renames itself to **Clear answer** and posts `values: []`, which writes `"answer": []`,
which `QuestionSpec` treats as **open** again (#63). A reviewer who may freely *change* a settled decision
must be able to *withdraw* it, and a form showing nothing selected while the server still held an answer
would be a lying UI.

**The round HAND-OFF ("Send to agent").** The reviewer says *"I am done with this round"* without leaving the
page: `POST /api/{key}/review/submit` records a hand-off and wakes the long-poll, `GET /api/review` reports it
plus live pending counts, `POST /api/{key}/review/ack?sequence=N` clears it by compare-and-clear. It rides the
poll envelope as the additive `reviewSubmitted` / `reviewSubmission` pair and **signals only** — the drafting
agent stays the single writer of the plan.

**The wake-signal invariant is stated ONCE, in `PendingSignal`:** *the signal is completed iff the owning
store has pending work*, re-established under the owner's lock after **every** mutation. All three review
stores (annotations, answers, hand-off) use it and `ReviewServer.WaitForReviewWorkAsync` waits on all three; a
store that skips the re-sync either hot-loops `poll --wait` or strands it until timeout. That is what makes
**answers wake `poll --wait`** (#62) — waiting on the annotation store alone made the reviewer's *decisions*,
the highest-value signal Charter carries, its slowest.

### The replaced-plan quarantine

The durability sidecar is keyed by the plan's **path**, so deleting a `.charter.md` and authoring a different
plan at the same name used to resurrect the dead document's notes into the new review (#67). `ReviewSidecar`
now defends against that, and the rule is deliberately the **weakest one that still catches the bug** —
over-eager quarantine would discard real review work, which is the worse failure:

> Quarantine iff the sidecar holds **≥ 1 annotation**, **and** the plan is **not byte-identical** to the
> revision the queue was last written against (sidecar **schema 2** stores a `planHash`; a schema-1 sidecar
> has none and falls straight through), **and** **not one** of those anchors resolves in the plan as it is now.

Orphaning after an edit is normal — that is the living-document model — so a queue where *any* anchor still
resolves is an edited plan, never a replaced one, and is delivered untouched. **Nothing is destroyed:** the
queue is copied to `<sidecar>.stale-<utc>.json` and restored by **`charter review --keep-annotations`**.

Three follow-ups landed on top of it (#75):

- **The reviewer is told in the PANEL, not only on stderr** (item 2). `charter review` is frequently launched
  *by an agent*, so the stream carrying "your notes are safe, here is how to get them back" often reached
  nobody. `GET /api/review` now carries an additive `staleQueue { count, fileName, durabilityDisabled }` —
  omitted entirely when there is none — and the SDK opens the panel once and renders it. It names the **file
  name**, never a local absolute path, and it is runtime-only chrome (`data-charter-ui`, gone on `dispose()`).
- **Answers are id-keyed, never quarantined — but they are CHECKED** (item 3). The anchor evidence says
  nothing about an answer, which left a replaced plan reusing a question id able to fold a stale decision
  *into the plan file*. Each submitted answer now records `questionFingerprint` — `QuestionIdentity`'s hash of
  the question's **declared shape** (id, title, mode, target, options; **not** its answer, so applying one
  answer cannot make the next look stale) — computed **server-side** at submit and persisted in the sidecar.
  `charter resolve` / `poll --apply` refuse the whole batch (exit **5**, answers preserved) when a queued
  answer has a fingerprint, a question with that id still exists, and its shape differs. No fingerprint, or no
  such question, means *no evidence* ⇒ apply exactly as before. The human's override is
  **`charter resolve --apply-stale-answers`**; `poll --apply` deliberately has none.
- **`.stale-*.json` files are bounded** (item 4). One is retired only once it is **older than 30 days** *and*
  **superseded** by a newer set-aside queue for the same plan; the newest is kept at any age because it may be
  the only copy. Pruning runs at quarantine time, never on a read.

Reviewer-facing details: `skills/charter/references/review-loop.md`.

## What the AGENT must do with all this

The consumption contract, and the part most easily missed:

- **Branch on `charter poll`'s exit code, never on an empty array.** `0` drained · `2` clean-empty ·
  `3` no live session *and* no readable review log (also the ambiguous >1-session refusal) · `4` a drain
  **could not complete** — queue state UNKNOWN · `5` the inline apply did not happen — it either FAILED or was
  REFUSED as stale (#75 item 3); either way the answers are preserved, never committed, and stderr names which.
  `1` is the generic verb error. Normative in `src/Charter.Cli/ReviewExitCodes.cs`; `charter resolve` shares
  them. **A `4` still emits `"annotations": []`** — the envelope's `drainError` (non-null) is what
  distinguishes "nothing queued" from "we don't know", and treating them alike is how an agent hands off a plan
  nobody approved.
- **Check `reviewSubmitted` on every poll.** `true` = the human clicked **Send to agent**: *this round is
  complete, do the substantial revision*. `false` = you woke on incremental feedback and the reviewer is still
  working. The marker is **peek + ack** — reported once, acked after the envelope is written, and acked **only
  on a clean drain** (a non-null `drainError` leaves it standing). Delivery is at-least-once: a repeated
  `sequence` is the same round, not a second one.
- **Annotations DRAIN; answers only PEEK.** A bare `charter poll` removes the annotations it reports but
  leaves the answers queued (`AnswerStore.Peek`), so the *same* answer is re-reported on every poll until
  `poll --apply` / `charter resolve` has durably written it into the `:::question` and called `CommitFront`.
  That asymmetry is the "nothing lost" guarantee, not a bug — but an agent that treats each poll's `answers`
  array as fresh work will act on the same decision repeatedly. De-duplicate by `questionId`, or apply.
- **`anchorStatus: "orphaned"` is neutral** — not an error, and not proof the note was addressed (§4.3).
- **`status: "contested"` is NOT resolved** — treat it as open (§4.2).

## The unattended path (`charter headless`)

**Three of Charter's verbs already never block** — `render`, `export`, and `handoff` are all file-in/file-out
and exit. `export` in particular renders the full offline artifact (local assets inlined, local paths scrubbed,
SDK-free, strict CSP) and returns without a server or a long-poll, so "render headlessly" was never the gap
(#7). What was missing is the **forensic guarantee**: the anchor→line source map and the decisions a review
would have elicited lived **only in the live server's memory**, so an unattended artifact could not be traced
back to its markdown, and nothing recorded which decisions were never made.

`charter headless <plan> [--out-dir <dir>]` closes exactly that, and nothing else:

- **It calls the same exporter** — the artifact is byte-identical to `charter export`'s, pinned by a test.
  There is no second render path and no `--headless` flag anywhere; adding one would be pure redundancy.
- **Both output names are DERIVED from the plan's file name**, so a collecting harness computes them with
  nothing passed: `<stem>.html` + `<stem>.headless.json`, where `<stem>` is the plan name minus its final
  extension (`storage.charter.md` → `storage.charter.html` + `storage.charter.headless.json`). Beside the plan
  by default; `--out-dir` relocates the pair, never renames it. A derived name that would land on the plan
  itself is **refused** (exit 1) rather than rendered over the source.
- **The record is a pure function of the plan text + the tool version** — no clock, no local path (the plan and
  artifact appear as bare names + a `planSha256`), so two runs diff clean and the file is as safe to hand on as
  the artifact. Shape: `schema` · `charterVersion` · `plan` · `planSha256` · `artifact` · `needsHuman` ·
  `questions[]` (id/title/mode/target/options/answered/answer + **`anchorId` and `sourceLine`**) · `notes[]` ·
  `sourceMap` (anchor → 1-based line, ascending).
- **Exit codes are `0`/`2`, and `2` is NOT a failure.** `0` = nothing outstanding. `2` = everything is on disk
  **and** a human must decide or fix something. `1` stays the generic verb error. Normative in
  `src/Charter.Cli/HeadlessExitCodes.cs` — deliberately a **separate** class from `ReviewExitCodes`, whose `2`
  means "a queue was found and it was empty". Do not read one vocabulary as the other.
- **`needsHuman` is the single escalation fact**, serialized into the record *and* returned as the exit code,
  so the file and `$?` can never disagree. Exactly three things raise it: an **open `:::question` with
  `target: human`**; a **`:::question` whose body will not parse** (target unknown ⇒ assume the worst, never
  assume `agent`); **duplicate question ids** (an answer would resolve into both and `poll --apply`/`resolve`
  refuse the write). A missing/unsupported version marker and an unknown `:::foo` are recorded in `notes` but
  do **not** escalate — every other verb treats those as warnings that never change an exit code, and widening
  the rule would make the flag almost always true and therefore worthless.
- **Out of scope, deliberately:** auto-generating human-style review comments (#7 says so — that is an agent's
  job). `notes[]` is Charter's OWN diagnostics, the stderr warnings an agent-launched run may never show a
  human, made durable — not synthesized review prose.

## Git-mediated team review (the durable half)

Comments also become **per-author append-only JSONL** records in `<plan>.review/<slug>.<hash8>.jsonl` beside
the plan, so review travels by git instead of dying in a machine-local sidecar. Normative design:
`docs/plans/03-git-mediated-team-review.md` — cite it, don't restate it. The code surface:

- `GET /api/review-log` — every author's logs, folded and projected **server-side**. There is deliberately
  **no static-file branch** for `.review/`: the confinement root is the plan's own directory, so one would make
  every sibling file under `docs/plans/` a key-gated HTTP-readable resource.
- `POST /api/{key}/annotations/{id}/resolve` — appends a `resolve` record. Open to anyone (review is
  collaborative) and always attributed; `retract` (the existing `/delete`) is refused for anyone but the
  comment's own author. Orthogonal to the round hand-off: a resolve settles one comment forever, a hand-off
  marks one round of one live session.
- The `/events` stream names its frames: **`reload`** (the plan changed — navigate) vs **`review-log`** (a
  teammate's log landed — re-read the fold only). Keeping them distinct is what stops a pulled log discarding a
  half-typed note or an unsaved answer. **BOTH frames are guaranteed EVENTUALLY, not notification-dependently**
  (#88 for `review-log`, #92 for `reload`): the file watch is the fast path, and the stream's keep-alive beat
  re-checks the `.review/` directory *and* the plan file, so a notification the OS never delivered still reaches
  the client on the next beat. Frames stay coalesced and idempotent — a client must treat `review-log` as
  "re-read", never as a delta. One asymmetry, because a `reload` is a full navigation and a `review-log` is not:
  **a plan file that is momentarily MISSING pushes nothing at all** (the reviewer is mid-`git checkout`, and
  navigating them to an error page would lose their place); the beat holds the last known revision and reports
  the restore exactly once.
- `charter poll <plan>` has a **server-less read path**: with no live session it folds `<plan>.review/*.jsonl`
  into the same envelope with the additive `source: "review-log"` (else `"session"`) and a per-annotation
  `review { authorName, authorEmail, actor, status, ts }`. Consumption is tracked in a **machine-local** ledger
  (`StateDirectory.Consumed()`), never as a log record — A's agent consuming must not mark a comment handled for
  B. **A live session always takes precedence**; the log is read only when none is live and a `<plan>` is named
  (bare `poll`, `--url`, `--session` never read it). `--apply` is inert here. This path returns **0/2/4 where
  it used to return 3**.
- **`base` + `baseStatus` — the #74 resolution (`ReviewBaseStatus`, §4.3.1).** Every review-log comment carries
  the plan's content hash **when it was recorded** (`base`) and whether the plan is still that text
  (`baseStatus`: `current` / `different` / `unknown`). **Review-log only** — both are omitted from a
  live-session annotation, and both readers of the log carry them (`ReviewLogDrain` → the `poll` wire,
  `ReviewLogView` → the panel). **It LABELS; it never suppresses**: the #67 quarantine deliberately does not
  cross over, because there is no local remedy for someone else's committed record and "not one anchor
  resolves" has a high benign base rate over a shared log. `current` is a **sound positive**; `different`
  proves only "not exactly this text" and is the modal state of a living document — never "ignore this".
  `unknown` = no `base` on the record, or the plan was unreadable/empty. Read it as a **pair** with
  `anchorStatus`: `(orphaned, current)` is the one anomalous reading; `(resolved, different)` is *both* the
  ordinary post-edit state *and* #67's replaced-document anchor collision, and **the two are not separable**.
  Line endings are not content — the plan is hashed in every newline form so a mixed Win/Linux team does not
  read `different` on every comment at the same revision.
- **`status` (`ReviewStatusTokens`) is load-bearing on the wire:** `open` · `resolved` · `contested` ·
  `retracted`. `contested` = concurrent resolve+reopen, neither having observed the other. **`charter handoff`
  does not read the review log at all** — honouring "a contested comment blocks handoff" is the *agent's*
  responsibility, not a code gate.
- A later `edit`/`reply`/`resolve`/`reopen`/`retract` mints a new record id, so a comment already delivered
  becomes **deliverable again** with its new status — intended. Do not suppress the repeat as a duplicate.
- **Who writes, precisely.** The *library* option `ReviewServerOptions.ReviewLog` defaults `null`, and with no
  writer the server behaves bit-identically to the pre-log server (the panel still *reads* whatever logs sit
  beside the plan). The **`charter review` CLI always supplies one** (`Program.OpenReviewLog`), resolving the
  author from `git config user.name`/`user.email` — **read-only; Charter never mutates git state** — and
  falling back to a marked `@localhost` identity. A review never fails for want of a log; any failure only
  warns and reviews local-only. **But it creates nothing and says nothing until it has to** (§5.0, above):
  `ReviewLogWriter` creates `.review/` **on the first append**, not in its constructor, and both the §7
  permanence notice (fired once, via the one-shot `OnFirstRecordWritten` callback) and the unresolved-identity
  warning are gated on `GitTracking.IsTracked` — a read-only `git ls-files` on the directory. **A solo reviewer
  who never shares is therefore completely silent, and leaves no directory.** The user-facing opt-out for a
  reviewer who *is* in a tracked repo is gitignoring `*.review/` (§7).

## The handoff to Guardrails

**AUTHOR → REVIEW → HANDOFF**, and the handoff is **dual** — see invariant 5 for which path is which. The
flattened path emits plain CommonMark with each `:::question` resolved from its inline `answer` (or a
`--answers` file) and open questions clearly flagged.

**That flatten must be LOSSLESS in what the breakdown routes on.** Six defects — labelled **C1–C6** in commit
`fbbcdd9`, **not** GitHub issue numbers — were found by the first real end-to-end Charter → Guardrails
verification. Exact emitted shape: `skills/charter/references/handoff.md`. The rules that came out of it:

- **Every `:::question` emits a status line PLUS a metadata line**, identical in shape whether answered or
  open: `` _Question — id: `x`; mode: `single`; target: `human`; options: `A`, `B`_ ``. `options` are the
  rationale a resolved answer is folded in with (the *rejected* option is what a guardrail can be written
  against); `target` lets the headless path honour `target: agent` instead of halting for a human. Both used
  to be dropped, and dropping `target` gave the flattened DAG 2 needs-human roots against the direct DAG's 1.
- **`:::diagram` / `:::diff` flatten to EXACTLY ONE fence.** Both body forms are accepted (raw, or already
  wrapped in ` ```mermaid ` / ` ```diff `); an already-fenced body is unwrapped before emitting, never
  double-fenced (a double fence makes the inner fence literal, so the diagram does not render on GitHub).
- **Nothing is silently dropped.** An unknown `:::foo` keeps its body (blockquoted prose on flatten, an escaped
  `<pre>` on render); a **resolved** question renders as resolved (`class="question answered"` +
  `data-answered="true"`, values pre-selected, an "Answered" chip) so a second round does not re-ask a settled
  decision; an answer matching no declared option becomes a checked write-in; duplicate `:::question` ids warn
  early on stderr from `render`, `review`, and `handoff`.

## Format decision (settled)

**markdown + directives (Markdig), as a deliberate hybrid** — chosen over MDX, Adaptive Cards, JSON Forms, raw
HTML, notebooks, AsciiDoc/RST, and slides. The essence of "MDX blocks" is a validated block *schema* (Builder.io
validates with Zod), not JSX; real MDX cannot run in C#; so markdown+Markdig validated against C# records is
the correct C# reproduction. Narrative stays free-form (strict format degrades LLM reasoning); the rigid schema
is confined to `:::question`, where reliability matters; `:::custom-html` is the escape hatch. Full study:
`docs/plans/01-combine-lavish-and-visual-plan.md` (decision D1).

## Load-bearing invariants

1. **Portable artifact** — opens standalone; SDK injected only at serve time.
2. **Comment-in-place with round-trip** — annotations anchor to stable block IDs and map back to markdown
   source lines; they survive re-render of unrelated blocks. The line handed to the agent is resolved **at
   drain time**, and an anchor that no longer resolves is reported as an explicit orphan — never as a
   stale-but-confident line number, and never as a different block.
3. **Format single-sourced** — the block schema lives in one place; renderer, SDK, and skill cite it.
4. **Loopback + capability** — `127.0.0.1` default, per-session capability key, path-confined serving. The
   drafting agent is the **single writer of `.charter.md`**; server-owned files (sidecar, review log) are not
   the plan.
5. **Dual handoff to Guardrails** — the interactive `/plan-breakdown` reads the `.charter.md` directly; the
   headless/autonomous path consumes the flattened `charter handoff` output. (Architecture B — flipped from the
   earlier "plain-markdown-only handoff"; see `docs/plans/02-architecture-b-living-document.md`.) The direct
   path needs **Guardrails ≥ `1.0.0-preview.48`**; against anything earlier use `charter handoff` → flattened
   `plan.md` (no version floor, supported permanently). A **documentation** compat note, not a code pin —
   Charter never invokes Guardrails; the real gate is the `charter-format` version range checked their side.
   **Two unrelated senses of "headless" now live in this repo:** this invariant's is *which ingestion path
   Guardrails uses*; the `charter headless` verb is *how Charter runs unattended*. They meet nowhere in code —
   `headless` emits no handoff markdown, and `handoff` writes no forensic record.
6. **Narrow C#↔JS boundary** — browser logic isolated in `sdk/`.
7. **Telemetry: none in v1; vendor-neutral if ever** — no vendor-SDK lock-in. A default-*off* flag does not
   prevent lock-in (the dependency compiles in regardless); the safeguard is not adding a vendor SDK.
   Deliberate departure from Lavish's default-on. (#6.)
8. **Charter reads git; it never writes git.** `GitCommand` is the one place it shells out, read-only, with
   every failure mode (git absent, not a repo, timeout) degrading to the solo-safe answer.

## Where truth lives

| Question | Authoritative source |
|---|---|
| Block catalog + `:::question` schema (normative, drift-tested) | skill `charter-format` |
| Reviewer-facing loop: annotation JSON fields, quarantine recovery, poll usage | `skills/charter/references/review-loop.md` |
| Flattened-handoff emitted shape | `skills/charter/references/handoff.md` |
| Architecture, milestones, decisions D1/D2 | `docs/plans/01-combine-lavish-and-visual-plan.md` |
| Living-document / dual-handoff design (Architecture B) | `docs/plans/02-architecture-b-living-document.md` |
| Team review: log layout, record schema, the 8 fold rules, `prev`/contested, §5.0 solo primacy | `docs/plans/03-git-mediated-team-review.md` |
| Unattended run: exit codes; forensic-record shape and what raises `needsHuman` | `src/Charter.Cli/HeadlessExitCodes.cs`, `src/Charter.Core/HeadlessRecord.cs` |
| Build / test / package / distribution / testing lessons | skill `charter-dev-knowledge` |

## Status (update as milestones complete)

- **`v0.7.0` IS RELEASED** — GitHub release, remote tag, and NuGet all carry it. (An earlier attempt was cut
  and unwound mid-test-phase to take more fixes; nothing published then, so the same number was re-used. That
  is why a stale note may claim it is pending — it isn't.)
- **Master is AHEAD of v0.7.0 and `<Version>` has not been bumped yet.** `src/Charter.Cli/Charter.Cli.csproj`
  still says `0.7.0`, so a local build reports a version already published. Everything in the *"Also landed"*
  bullet below is **unreleased** and heading for the next tag — bump `<Version>` before cutting it.
- **Master baseline (`dc725b7`):** **755** tests green, 0 warnings — Core 405 · Server 244 · Cli 81 ·
  Browser 25.
- **Shipped in v0.7.0:** the #68 table scroll wrapper; team review steps 1–4 + the `.review/` tracked-gate; the
  #67 quarantine + `--keep-annotations`; #56, #66, #48, #60, #61, #63. (`git log v0.7.0` is the history.)
- **Also landed — merged to master but NOT YET RELEASED:** panel/drain anchor parity (#78); an unrecognised
  annotation `kind` refused with 400 rather than coerced to `element` (#79); the three #75 quarantine
  follow-ups (panel surfacing, answer-staleness refusal + `--apply-stale-answers`, `.stale-*.json` retention) —
  PR #82. The unattended `charter headless` verb (#7) — PR #83. Pan/zoom for an oversized `:::diagram` (#51),
  SDK-only — PR #84. The #74 review-log staleness resolution (`base` + `baseStatus`) — PR #80. The #5 layout
  regression gate — PR #86. The #87 reachable scroll affordances for `:::diff`, code blocks, an unknown
  directive's body and `:::custom-html` — PR #89.
- **Team review — built vs NOT built** (`docs/plans/03-git-mediated-team-review.md` §9):
  - **Built:** 1 (record + fold), 2 (writer), 3 (server-side fold + panel), 4 (server-less `poll` read path),
    7 (the two-author browser test — `Review_panel_shows_this_authors_committed_comment_and_a_teammates_log`).
  - **Step 5 is only PARTLY built.** The read-only git *plumbing* exists (`GitCommand`, `GitTracking`) and
    serves the §5.0 tracked-gate. The §5.1 **warnings do not exist**: no behind-upstream/stale-plan warning at
    `charter review` start, no uncommitted-records reminder at session end, **no `charter review verify` verb**,
    no orphan diff (an orphan shows its `quote`, never a diff).
  - **Step 6 (agent voice) is not built.** `ReviewOpKind.Reply` and `Reopen` are understood by the fold and by
    `ReviewLogWriter.NewId`, but **nothing appends either** — no `AppendReply`, no API route, no CLI verb. A
    `reopen` can only reach a log from outside Charter.
- **Known-open follow-ups:** #46 (annotation lifecycle v2). (#5 and #87 are CLOSED — both landed above.)
- **Pending externally** — Guardrails' interactive direct-ingestion of `.charter.md` (Guardrails #390–393,
  their team; Charter's producer side is complete); macOS signing (#9); v2 features (#1–#6).
- **Decisions made** — D1 (markdown+directives hybrid), D2 (reimplement lean in C#).
