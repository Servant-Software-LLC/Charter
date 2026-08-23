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
  inits with `securityLevel: 'antiscript'` (inline SVG under CSP, no sandboxed iframe) — and with
  `startOnLoad: false` plus an EXPLICIT node list, never the default document-wide `.mermaid` selector
  (#177: it rewrote an author's `<pre class="mermaid">` inside `:::custom-html`, in the artifact too).
  It is inlined **iff the render actually wrote a `<pre class="mermaid">`, at any nesting depth** — the flag
  comes from the container renderer, not from a walk (#184: it used to come from the anchor pass's
  top-level-only loop, so a `:::diagram` inside a `::::note` or a list item never inlined the runtime and
  rendered as its own source text, *intermittently*, since any unrelated top-level diagram made it draw by
  coincidence). The exported artifact gains the runtime for such a plan, which is invariant 1 being kept, not
  bent: a saved diagram must be the same picture the served page shows.
- **Block catalog and the `:::question` schema are normative in the `charter-format` skill** — including the
  fact that there is **no** `:::annotated-code` or `:::file-tree` (they have no renderer). Cite it; never fork
  it. A drift test binds the skill's catalog to `BlockKind` ∪ `QuestionSpec`, so a fork breaks the build.
- **`:::question` is the elicitation block**: a validated JSON body (`id`, `title`, `mode`, `options`,
  `target`, optional `answer` — which marks it resolved when it holds at least one value and none of them is
  blank; `[""]` is OPEN, #188) rendered as a native HTML `<form>`. The gap
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
  degrades to "no anchor", never "the wrong anchor". **But the attribute is a LABEL, not proof of ownership**
  (#176) — see the SDK-integrity rule below.
- **THE SDK OWNS WHAT IT BUILT, AND NOTHING THAT MERELY LOOKS LIKE IT** (#176/#177/#178/#179). Every name in a
  rendered plan is a name a `:::custom-html` author may write, verbatim — that is what an escape hatch is —
  so the SDK must never reach into the document by NAME and change what it finds. Concretely: `make()` stamps
  a private JS property (`charterOwned`) on every element the SDK constructs, and `isSdkUi` / the block-text
  walker test THAT rather than `[data-charter-ui]`; `renderMarkers` records the classes, attributes and
  elements it applied and `clearMarkers` undoes exactly that ledger, rather than sweeping the document for
  `.charter-annotation-badge` / `.charter-badge-rail` / `.charter-has-annotations` /
  `[data-charter-annotation-count]` and destroying every match. HTML cannot express a JS property, so
  ownership is not forgeable.
  - **Where a name really is the only handle, narrow it with the OPAQUE-REGION predicate, which is monotone.**
    Three places have to find plan markup by name — the renderer's Mermaid bootstrap and the SDK's
    `scanDiagrams` (`pre.mermaid`), and `questionRoot`/`questionForms` (`form[data-question-id]`, the gateway
    to intercepting a form's submit, re-labelling its button and disabling it) — so all three exclude anything
    inside `.custom-html-scroll`: forging the region class inside your own body can only ever REMOVE your
    markup from the set, never add someone else's. That is the #166 shape, reused.
  - **This category is DESTRUCTIVE, which is why forgery mattered here and does not for #166.** #166's
    predicate is a read-only containment test whose worst case is "unanchorable, degrading outward". These
    four wrote: an author's element deleted on every SSE frame, an author's markup replaced by a rendered SVG
    **in the exported artifact** (invariant 1), a reviewer's typed note dropped with no message, an author's
    inline CSS counted into the offset frame a note is recorded against.
- **SDK chrome may be a block's SIBLING; it must never be a block's ANCESTOR** (#164) — the mirror of the rule
  above, for chrome that sits *outside* a block rather than inside it. `closestAnchored` tests
  `isSdkUi(el) || el.closest(UNANCHORABLE)` — the first half by OWNERSHIP since #176, the second being native
  controls and `form.question` — **before** it walks for an id, so a
  chrome-marked ancestor makes every Alt+click anywhere inside that block resolve to `null`: the block silently
  stops being annotatable at all. That is why a count badge that cannot live inside its block rides a
  zero-height `.charter-badge-rail` inserted as the block's **previous sibling**, and why wrapping the block in
  a positioned frame — at render time or at serve time — is rejected. Reparenting is doubly wrong at serve
  time: it discards `.table-scroll`'s `scrollLeft` and any focus inside it, and `render()` runs on every SSE
  frame, so a teammate's pulled note would blow away a scrolled, focused table. A rail is placed only where the
  climb from the anchor reaches a direct child of `<body>` without passing an ancestor that carries an anchor
  of its own.
- **Two independent reasons put a badge on a rail, and a fix for one is not a fix for the other.**
  `<table>`/`<ul>`/`<ol>`/`<hr>` are railed because the CONTENT MODEL forbids a `<button>` child (#164). A
  non-diagram `<pre>` is railed because it is ITS OWN horizontal scroll box (#165): `charter.css` gives it
  `overflow: auto` and `.charter-has-annotations` gives it `position: relative`, so an appended badge is
  absolutely positioned *inside* the container it scrolls with — measured at `scrollLeft` 2543, a badge that
  started at x=863 finished at x=-1680. That is #51's lesson, and a `<pre>` has no wrapper to hoist past, so
  the rail goes before the `<pre>` itself. `pre.mermaid` is excluded, exactly as `charter.css` excludes it from
  the scroll regions: a diagram becomes a scroll box only under #51's pan/zoom, which already compensates its
  in-block badge on every scroll.
- **The accent bar and the count badge must live in the SAME coordinate space** (#167). The bar is an inset
  `box-shadow`, which decorates the element's own border box — so painting it on a top-level `<table>`, which
  lives INSIDE `div.table-scroll`, put half of one signal inside the scroll box and half outside it: measured
  at `scrollLeft` 4377, the table's left edge (and its bar) sat at x=-4323 while the railed badge stayed
  pinned. `markerBox` therefore reuses `railMount` — "the outermost box that is still this block" — so a
  table's bar is painted on the scroll REGION, and every other block keeps it on itself. Second rule from the
  same report: on `.table-scroll` the bar is an **outer** shadow rather than an inset one. An inset shadow
  paints on the element's own background layer, underneath every descendant, and a table's descendants paint
  there — `th`'s opaque background hid it across the header and the collapsed cell borders chopped the rest
  into one dash per body row. An outer shadow is clipped to outside the border box, so nothing in the block can
  reach it, and a shadow of any offset costs no layout.
- **An anchor a reviewer cannot POINT at is only half an anchor** (#169). A top-level `<hr>` has always carried
  a stable id and opened the composer, but `charter.css` had no `hr` rule, so the UA default (`height: 0;
  overflow: hidden` plus 1px borders) made it a 2px box — a measured hit band of ~2px, which a real click
  essentially never lands. It now has a **12px box with the rule painted as a hairline down its centre**, with
  margins trimmed so the block's vertical footprint is unchanged to within 2px. That lives in `charter.css`
  rather than the SDK, and the justification is invariant 1's own test: the artifact genuinely needs it —
  `border-style: inset` draws from `currentColor`, which made `<hr>` the one block element in a rendered plan
  that ignored the Charter palette (a near-white bar in dark mode). The pointer target is a consequence of
  giving it a real box, not review-only scaffolding added for review's sake.
- **The reachable-anchor set is exactly: top-level document nodes, plain top-level list items,
  `:::comparison` rows, and `:::diff` lines — nothing else.** `CharterMarkdown.SubAnchors` is the single
  descent that defines the sub-block half, and `AnchorAssignment` walks the same union, so an id the renderer
  emits is always one `SourceMap` registers. A nested list inside an `<li>`, a list inside `:::note`/`:::warn`,
  and a `<tr>` are **not** anchors: none of the three is ever stamped.
  **One carve-out, and it is the SDK's, not the renderer's: a `:::question` is not annotatable at all.**
  `form.question` is in the SDK's `UNANCHORABLE` list — a rendered question is native controls, and a note
  competing with the answer the block exists to collect would be worse than no note — so `closestAnchored`
  refuses everything inside one. The renderer still stamps the block's id and
  `SourceMap` still registers it, so nothing is inconsistent; just do not read "top-level document nodes" as
  "every top-level block can take a note", and do not "fix" the question block to match the sentence.
  **The refusal SPEAKS** (#178): Alt+click inside a question prevents the default (so the same gesture cannot
  half-work by ticking a radio) and opens the panel with the reason. A gesture that produces nothing and
  explains nothing leaves a reviewer unable to tell a rule from a bug — the #170 rule, applied past markers.
- **TWO REGIONS ARE OPAQUE TO THE ANCHOR WALK, and an id inside one is not an anchor** (#166).
  `div.custom-html-scroll` holds an author's verbatim markup and `pre.mermaid` holds Mermaid's generated
  markup. Neither kind of id was produced by `AnchorAssignment`, so `SourceMap` cannot map one to a line. The
  rule is a **containment predicate**, in `sdk/charter-annotate.js`: an element is an anchor **iff** it carries
  `id` / `data-anchor` / `data-charter-anchor` **and no ancestor of it is an opaque region**. All three
  attributes are gated uniformly, because `:::custom-html` passes all three through verbatim.
  - **Where the predicate fails the walk CONTINUES OUTWARD** — it does not return the region and it does not
    return null. That distinction is the design. An early return of the region would be a *new* bug:
    `RenderBody`'s anchor pass iterates top-level nodes only, so a `:::custom-html` or `:::diagram` nested in a
    `::::note` or a list item renders with **no id**, and returning it yields `anchorId: null`, which
    `textRangeAnchor` does not guard. Continuing outward lands on `div.note` / the `<li>`'s sub-anchor: a real
    anchor with a real `sourceLine`.
  - **It REPLACED the explicit `pre.mermaid` short-circuit and repaired what that got wrong.** The
    short-circuit returned the diagram block unconditionally, so a **nested** `:::diagram` — id-less, per
    above — hit `onClick`'s `!anchorIdOf(block)` guard and was **un-annotatable, silently**: no composer, no
    error. #48's guarantee is unchanged (every Mermaid id now fails the predicate on the same rule), and
    `textRangeAnchor` asks the SELECTION as well as the resolved block whether it is inside a diagram, since
    those stopped being the same question once the walk climbs out of one.
  - **The READ path is gated identically, and it is the more serious half.** `anchorElement` resolved by
    `document.getElementById`, which answers with the first element in document order — so two escape hatches
    that both contain `<table id="raw-t">` (a copy-paste, which is what an escape hatch is for) made a note
    taken in the *second* jump to, mark and quote the *first*. That is **misattribution, not orphaning**, and
    it is exactly what `AnchorAssignment`'s duplicate discriminator exists to prevent. A rejected match keeps
    looking rather than giving up, so a forged id cannot shadow a real anchor further down. Fixing only the
    write path would have left this standing for every already-committed note.
  - **Forgery cannot defeat it, because the predicate is MONOTONE.** An author may write
    `class="custom-html-scroll"` inside their own body, but the real region is an ancestor of everything in
    that body, so a forged inner region only ever *adds* an ancestor match: forgery makes more things
    unanchorable, never fewer, and "unanchorable" degrades outward to the enclosing block, never to null. No
    "outermost region" computation is needed.
  - **Accepted residue: markup that breaks out of both wrappers** (`</div></div><div id="pwn">`) is hoisted
    clear of the region by the HTML parser, and the predicate cannot see it. Balancing an author's body means
    parsing it. That case is covered from the other side — see the panel's orphan diagnostic under
    *Review-loop semantics*.
  - A `:::custom-html` block therefore badges like any other block, on its own `div.custom-html`. The old
    claim that the SDK "deliberately shows no count badge there" was already false before this landed (a
    hatch whose markup carries no ids has always anchored and badged normally) and is now false in every case.
- **"Top-level document node" means a node the AUTHOR wrote, not every child Markdig hangs off the document.**
  Markdig also appends SYNTHETIC children that render nothing and hold no position of their own — the
  `YamlFrontMatterBlock` and, since #171, the `LinkReferenceDefinitionGroup` that collects a plan's
  `[foo]: http://…` declarations. Both report `Line` 0 and a `Span` starting at offset 0 wherever the real
  text sits, so left in they claim the anchor slot at line 1 and — `AnchorAssignment` keying block slots by
  line, last writer wins — silently overwrite the id of the block that genuinely starts there, normally the
  plan title. `CharterMarkdown.ParseDocument` **strips both**, which is what makes the bullet above true for
  every seam at once (render, `AnchorAssignment`/`SourceMap`, `BlockDocument` and thus the handoff/export,
  `PlanWalk` and thus the headless record). Stripping costs nothing: Markdig resolves `[foo]` against the
  document's link-reference dictionary during `Parse`, so the links are already materialized, and removing a
  node never shifts another node's absolute span or line. **The invariant to keep is that no two top-level
  blocks share a start line** (`LinkReferenceDefinitionTests.ParseDocument_NeverYieldsTwoTopLevelBlocksOnTheSameLine`)
  — any future Markdig extension that appends a synthetic group (`FootnoteGroup` is the same shape) belongs on
  the strip list, NOT in a new id namespace. That is the opposite call from the `ListBlock`/first
  `ListItemBlock` collision, which needed `SubIdForLine`: there two REAL slots share one line, so both must be
  kept and told apart; here one real slot shares a line with a phantom, so the phantom goes.
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

**And the PANEL reads what those two routes agree on** (#166). `PanelDrainParityTests` binds the wire; it
cannot see whether the SDK looks at the field. It did not: `pendingRecord` threw `anchorStatus` away and
hardcoded it null, so the panel fell back to *"is the element on the page?"* — which says nothing about
whether Charter can map the anchor to a markdown line. A note on an id the assignment pass never produced sat
there drawn as healthy while the agent was handed `sourceLine: null`. Two rules now:

- **The orphan test is a DISJUNCTION** — `record.anchorStatus === 'orphaned' || !entry.el`. The two sources
  answer different questions and either alone is blind: the server knows whether the anchor maps to a line,
  the DOM knows whether the block is on this render. "Server wins outright" would draw a note whose element is
  absent as healthy.
- **Three orphan sentences, each earned by its own evidence.** `baseStatus: 'different'` ⇒ *"the plan has
  changed since this comment was written"* (§4.3.1, review-log only, so a pending orphan never reaches it);
  otherwise, element still present ⇒ *"this is still on the page, but Charter cannot trace it back to a line
  of the plan"*; otherwise ⇒ *"the block this comment was written on is not in the plan"*. The middle one
  exists because the last one is a plain falsehood when the reviewer is looking straight at the thing it
  claims is gone — which is the whole #166 residue.

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
  **SDK-owned** subtrees and `<style>`/`<script>` (#179 — their contents are source, not words a human reads,
  and an escape hatch carrying inline CSS shifted every offset below it by the length of that CSS). One
  reference frame, shared with the panel's quote lookup and with the derived label, so
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

**Opening the panel MOVES focus into it; an automatic open never does** (#168). The floating toggle hides
itself the instant the panel opens, so the control the reviewer just activated stops being focusable and the
browser resets focus to `<body>` — a keyboard reviewer was dropped at the top of the document and had to
re-traverse it to reach the notes they had just asked for. Focus lands on the panel itself (`tabindex="-1"`,
`role="complementary"`, labelled), so what is announced is the region that opened; a badge press is the one
exception and lands on the card it names. Closing returns focus to the toggle, for the mirror reason. The
`focus` option is **opt-in**, and the three automatic opens — a note saved, a round handed off, a quarantined
queue explained — deliberately leave it off: stealing the caret out of a document the reviewer is reading
would be a worse bug than the one this fixes.

**Absence is DISCLOSED, never left to be inferred** (#170, generalizing #164). Charter has one vocabulary for
*annotated* (the accent bar) and one for *how many* (the count badge), and **no third state meaning "annotated,
but not shown here"** — so a marker that correctly disappears reads as breakage unless something says
otherwise. Retracting a block's last live note is exactly that case: `renderMarkers` skips retracted
annotations, which is right, and the panel keeps the card. The card therefore states the neutral fact, the way
`renderItem`'s orphan handling already does — not by inventing a third on-page marker. Both halves are
load-bearing: the sentence appears only when the marker really has gone, so it does not become noise on every
retraction. `markingCounts` is the single computation the markers are painted from and the panel reads, so the
two cannot drift.

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

Every Mermaid id is refused at the **anchoring layer**, not per handler, so by construction no path can escape
carrying one. Since #166 that is the opaque-region predicate above — `pre.mermaid` is one of the two regions,
so every id inside it fails the same rule that refuses an author's id inside `:::custom-html`, and the walk
stops on the block. It used to be an explicit short-circuit returning `pre.mermaid` *unconditionally*, which
had the side effect of leaving a NESTED (id-less) diagram un-annotatable; the predicate covers #48 and repairs
that in one rule. Both granularities share one anchor id, so the composer's context line is all that
distinguishes them for the reviewer — it names which one explicitly.

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
  the artifact. Shape (**`schema` 2**): `schema` · `charterVersion` · `plan` · `planSha256` · `artifact` ·
  `planFormatVersion` · `needsHuman` · `questions[]` (id/title/mode/target/options/answered/answer/recommended
  + **`anchorId` and `sourceLine`**) · `notes[]` · `sourceMap` (anchor → 1-based line, ascending).
- **It is a DECLARED contract, and the declaration is a TEST, not prose** (#173). The stable core is
  `schema` · `charterVersion` · `plan` · `planSha256` · `needsHuman` · `questions[].{id,target,answered}`;
  `message` strings, `sourceMap` **values** and `notes[]` ordering are explicitly non-contract; an
  unrecognised `notes[].kind` must be **ignored, never rejected**. `HeadlessRecordContractTests` binds the
  emitted field set and every note-kind token to `skills/charter/references/unattended.md`, which is where the
  absence semantics and the `sourceMap`-value instability live. A prose promise was tried and failed —
  `recommended` shipped in #142 with `schema` left at 1.
- **Exit codes are `0`/`2`, and `2` is NOT a failure.** `0` = nothing outstanding. `2` = everything is on disk
  **and** a human must decide or fix something. `1` stays the generic verb error. Normative in
  `src/Charter.Cli/HeadlessExitCodes.cs`, now **shared with `charter handoff --fail-if-needs-human`** — both
  verbs mean the same thing by a 2, and **so does Guardrails** (`BreakdownCommand.NotCleanExitCode` "a 2 means
  READ THE FOLDER", `ExitCodes.TaskFailed` "at least one task needs a human"). **The outlier is Charter's own
  `ReviewExitCodes`**, whose `2` means "a queue was found and it was empty". #173 asked for the opposite
  warning; reading Guardrails' source settled it this way.
- **`needsHuman` is the single escalation fact**, serialized into the record *and* returned as the exit code,
  so the file and `$?` can never disagree. Exactly three things raise it: an **open `:::question` with
  `target: human`**; a **`:::question` whose body will not parse** (target unknown ⇒ assume the worst, never
  assume `agent`); **duplicate question ids** (an answer would resolve into both and `poll --apply`/`resolve`
  refuse the write). A missing/unsupported version marker and an unknown `:::foo` are recorded in `notes` but
  do **not** escalate — every other verb treats those as warnings that never change an exit code, and widening
  the rule would make the flag almost always true and therefore worthless.
- **Out of scope, deliberately:** auto-generating human-style review comments (#7 says so — that is an agent's
  job). `notes[]` is Charter's OWN diagnostics, the stderr warnings an agent-launched run may never show a
  human, made durable — not synthesized review prose. **All of them**, since `schema` 2: `notes: []` did not
  used to mean "Charter noticed nothing", because `handoff` printed the missing-`recommended` (#142) and
  untracked-deferral (#156) lints with no matching note kind. Both now exist and neither escalates.

## The unattended PIPELINE (`charter handoff --fail-if-needs-human`)

**`headless` is not the verb that feeds Guardrails — `handoff` is.** They share a word and nothing else, and
reaching for the wrong one is a *silent* failure: `charter headless` writes no `plan.md` and exits 0 on a
clean plan, so the run reports success while Guardrails gets nothing or a stale file. Design of record:
`docs/plans/04-machine-consumer-contract.md`.

- **It WRITES its output and exits 2.** Not fail-closed, for two reasons: every 2 in this pipeline means
  *the output exists, go read it*; and a refusal would leave the PREVIOUS run's `plan.md` on disk — a stale
  flatten carrying no open-question markers at all, internally consistent, passing any lint, and
  indistinguishable to a `BreakdownCommand` that only checks the file extension.
- **The predicate runs AFTER `--answers` is merged**, which is why it could not reuse `NeedsHuman` (a pure
  function of the plan text, by contract). The two share a `PlanInventory` — one walk — but deliberately
  **not** a verdict.
- **It is STRICTER than the record in two places.** An open `target: agent` question blocks unless it is
  *decidable* (carries `options` or a `recommended`) — because nothing downstream actually branches on
  `target`, so delegating is prose asking the next agent to decide, and a bare free-text one asks it to
  invent. And an **unknown `:::foo` blocks**, because a misspelled `:::questoin` classifies as one and would
  otherwise exit 0 from both verbs on a hidden `target: human` decision.
- **stderr names each blocker's id/title/target**, and separately reports `--answers` ids matching no
  question — reported, never a veto.
- **The flatten gained two things** (#172): an open `target: agent` question leads with *"Delegated decision
  — you must settle this before building:"* plus a mode-specific `_Decide: …_` instruction, because on that
  path prose IS the interface; and every flattened plan ends with `<!-- charter: plan-sha256=<hex> -->`,
  byte-identical to the record's `planSha256`. That stamp is the only provenance mechanism that **survives a
  consumer ignoring exit codes and side files** — out-of-band signalling cannot fix a failure of out-of-band
  signalling.
  - **There are TWO stamp lines since #187**, `<!-- charter: answers-sha256=<hex|none> -->` immediately above
    the plan one (which stays LAST, so a consumer matching the tail keeps working). **One stamp identifies the
    plan; the pair identifies the RESOLUTION INPUTS**, and only the pair closes the stale-manifest hazard: run
    once with `--answers … --manifest`, then re-run as a plain `handoff`, and `plan.md` becomes the
    all-questions-open flatten while the old manifest survives with `planSha256`, the plan stamp, the record's
    `planSha256` and `charterVersion` **all four matching**. `none` is a word rather than an omitted line
    because "this run merged no answers file" is a positive fact. Both stamps are CRLF-immune; the manifest's
    `handoffSha256` byte-hash is not.
- **ONE question-body parse, pinned.** `QuestionResolution.QuestionBody` is the single definition (any fence
  length, any line ending, an unterminated container at EOF — the three shapes the renderer already accepts);
  `TryLocateJsonBody` is only its span-based write twin. They forked once and it went **both ways**:
  `::::question` read fine in the record while the flatten deleted its id/title/target, and an unterminated
  container flattened perfectly while the record escalated it. `QuestionBodyParityTests` asserts the two
  verbs reach the same verdict, not that they call the same method.
  - **And ONE fence vocabulary under both** (`DirectiveFence`, #190). The `any fence length` tolerance was
    granted to the question path alone, so every OTHER container still matched `^:::\w+` / `^:::\s*$` and a
    `::::` container flattened with both its fence lines still in the body. That is not exotic authoring —
    `charter-format` tells an author to widen the fence whenever a body line would itself start with `:::`, so
    it fired on the NESTING case. **"Invariant 5 held anyway" was true of two of the six containers**: a
    note/warn's leak rode behind a blockquote `>`, but `:::comparison` and `:::custom-html` emit their inner
    lines verbatim, so the leak was a live directive line at column zero in the plain-CommonMark handoff, and
    a `:::diff`'s leaked opener defeated the fence unwrap and re-emitted the container's own fences as diff
    content inside an escalated fence (#48/C2, via #190).
- **An `--answers` entry may FILL, never REPLACE** (#186). It settles a question the plan left open and may
  re-state a recorded `answer` verbatim — *an answers file may only ADD information* — but it may never
  overwrite one, and `[]`/`null` is a rejection rather than an erasure ("no answer" is already spelled by
  **omitting the id**). Values are checked against the question's `mode` and `options`. A violation is a bad
  **invocation**: `charter handoff` exits **1**, writes **nothing**, and names every violation on stderr —
  the same class as the unreadable-`--answers` `1` it already returned, and deliberately not a `2` (a `2`
  would mean "the output exists, go read it" about a document that silently differs from what was asked for).
  One kernel: **`AnswerRules`** (`Merge` + `Check`), which replaced `HandoffGate.ResolvedAnswer` because the
  rules became part of the merge.
  - **Validation is a function of WHO SUPPLIED the value, not of WHERE IT LANDS.** A human at a review page
    holds authority to exceed the declared `options` (the "Something else" write-in, #109 — `charter-format`
    forbids validating it away); an **invocation does not**. That is why an inline `answer` is never
    membership-checked and an `--answers` value is: one rule, two suppliers, and the next channel someone
    adds takes its rule from who is behind it.
  - **Separately** (a different axis — the *question's* declared schema, not the answer's provenance): a
    **`free-text`** question can only be checked for SHAPE (one value, not blank), because it declares no
    options to test against. Do not hunt for one rule behind both.
  - **Chose refuse-the-override over #187's record-the-source.** Recording makes an override auditable, not
    safe: the flatten would still assert the overriding value in a side file nobody has to read. The residual
    hazard — a refusal leaves the PREVIOUS run's `plan.md`, and `plan-sha256` cannot expose that (same plan,
    different answers file) — is CLOSED by #187's `answersSha256` and the second stamp line (below).
  - **What `answerSource` can and cannot say** — the withdrawn reading, recorded so it is not re-invented:
    it does **NOT** distinguish *a human decided this* from *the automation supplied this* — `inline`
    conflates a reviewer's folded-in answer, the drafting agent's own edit, and any other writer, and
    `handoff` never reads the review log. It carries **which hash reproduces the decision**: `inline` ⇒
    `planSha256` covers it; `answers-file` ⇒ reproducing it also needs `answersSha256`.
- **"Answered" means a DECISION, and it is ONE predicate** (#188): `AnswerRules.IsDecision` — at least one
  value, **none of them blank**. It used to be `Count > 0` in **three independent implementations** (the
  record's property, the gate's inline test, the flatten's inline test) plus the renderer and the
  missing-lean lint, so `[""]` flattened as `Answered:` with nothing after it and any strict gate built on it
  certified a blank as a made decision. Fixing only the record's property would have produced three artifacts
  giving three answers about one plan — pinned by
  `BlankAnswerTests.TheRecord_TheGate_AndTheFlatten_GiveONEAnswerForOnePlan`, which asserts the three
  VERDICTS, not that they call the same method.
  - **It rode the UNRELEASED `schema` 2.** Narrowing a published field's meaning is a bump by
    `HeadlessRecord.Schema`'s own rule — except 2 was raised from 1 by #173 *after* 0.24.0 shipped (the
    release commit carries `Schema = 1`), so no consumer has ever seen a schema-2 record. Not licence to do
    this under a *released* version; that is the mistake #142 made.
- **`--manifest` writes the chain-of-custody manifest, from the SAME resolution pass** (#187). Boolean, not a
  path: derived from `--out` (`-o plan.md` ⇒ `plan.manifest.json`), because `HeadlessCommand`'s own rationale
  is *"a path convention a harness can compute"*. Its own **`schema` 1** and its own drift test —
  deliberately NOT a `HeadlessRecord`, whose `artifact`/`sourceMap`/`anchorId` are all wrong here.
  - **Stable core:** `schema` · `charterVersion` · `planSha256` · `answersSha256` · `handoffSha256` ·
    `malformedQuestions` · `gate.{flagPassed, needsHuman, exitCode}` · `gate.unmatchedAnswerIds` ·
    `questions[].{id, answered, answer, answerSource}`, document-ordered. **Not contract:** the three
    file-NAME fields (the hashes are the join key, the names are decoration), `questions[].title`,
    `blockers[]` ordering, key order. **`blockers[].detail` is never serialized** — the gate declares it
    non-contract, and a versioned schema would make it one.
  - **The governing absence rule, with a negative test:** *every line number in the manifest is a line in
    `plan`, and the manifest carries no map into the handoff output at all.* No `artifact`, no `sourceMap`,
    no `anchorId` — those three are exactly why emitting a record here was rejected.
  - **`answerSource` is two-valued** (`inline` | `answers-file`), null when unanswered. #186 shipped REFUSAL,
    so "the file overrode the plan" cannot occur and there is no token for it. It is classified **beside the
    merge, by asking the merge what it did**, so it cannot drift from the emitter — a REJECTED entry therefore
    reads `inline`.
  - **`answered` here is NARROWER than the record's**: the record's is the plan's inline answer (pure in the
    plan text), the manifest's is the MERGED answer. One name, two scopes — asserted, not assumed.
  - **Neither flag implies the other.** `--fail-if-needs-human` would write an unbidden file (§5.0);
    `--manifest` would change an exit code as a side effect of asking for a file. The gate is evaluated ONCE
    per run and feeds both; `gate.flagPassed` records the **argv**, not obedience (hence not `enforced`).
  - **Write order: handoff FIRST, then manifest**, both temp-file-then-rename. A handoff with no manifest is
    an honest degraded state; a manifest describing a file that does not exist is a lie. So exit `1` no
    longer promises "nothing was written" — the help text says so.
  - **`handoffSha256` is ADVISORY and has no consumer** (Guardrails #505: their `PlanHash` covers
    `guardrails.json` + `task.json`s, never the source markdown). A mismatch means tampering **or** a
    line-ending rewrite in transit, indistinguishable from the hash alone.
  - **One hash recipe for all three, and it is not `sha256sum`.** `File.ReadAllText` strips a UTF-8 BOM and
    decodes UTF-16/32 per the mark; `PlanHash.Sha256Hex` hashes the **UTF-8 re-encoding of that string**. So
    they match `sha256sum` only for a BOM-less UTF-8 file — Windows PowerShell 5.1 writes UTF-16LE, hence the
    stderr **warning** (never a rejection) when the answers file is not BOM-less UTF-8.
  - **Still deliberately NOT done:** `charter headless --answers`. Everything it produces is by contract a
    pure function of the plan text (its artifact is pinned byte-identical to `export`'s), and its needs-human
    is a strictly WEAKER predicate than the gate's — so the record still describes *the plan on disk* while
    the handoff describes *the plan plus a file*, and that is the correct split.

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
  against); `target` is there so a consumer **can** branch on `human` vs `agent`, and without it a delegated
  decision is indistinguishable from one needing a person. Both used to be dropped, and dropping `target`
  gave the flattened DAG 2 needs-human roots against the direct DAG's 1. **No consumer is known to branch on
  it**: neither literal Charter emits (`Open question (unresolved)`, `_Question — id`) appears anywhere in
  Guardrails' source, docs or skills (#172; filed reciprocally as Guardrails #500). The old claim that "the
  headless breakdown branches on `human` vs `agent`" was false and was the sole justification for exempting
  agent questions from strict handoff's gate — which is why that exemption is now narrowed.
- **An OPEN `target: agent` question flattens as a DELEGATED DECISION, not an open question** (#172):
  *"Delegated decision — you must settle this before building:"* plus the metadata line plus a mode-specific
  `_Decide: …_` instruction naming the author's lean. An answered one gains no instruction.
- **Every flattened plan ends with `<!-- charter: plan-sha256=<hex> -->`**, the same hash the headless record
  calls `planSha256`, preceded since #187 by `<!-- charter: answers-sha256=<hex|none> -->`. Before them, the
  flatten self-identified as nothing (front matter is stripped, with a test pinning it), so "did the plan
  Charter recorded match the plan Guardrails consumed" was unanswerable in principle — and the plan hash alone
  still could not tell two runs over one plan with different answers apart. Charter's own renderer ESCAPES
  them (`DisableHtml`); that is the security posture, not a defect.
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
| **The `.headless.json` CONTRACT** a machine may assert on (stable core, absence semantics, note kinds) | `skills/charter/references/unattended.md`, bound by `HeadlessRecordContractTests` |
| **Strict handoff, the delegated flatten, the provenance stamps, the one question-body parse, the manifest** | `docs/plans/04-machine-consumer-contract.md`; predicate in `src/Charter.Core/HandoffGate.cs` |
| **The `.manifest.json` CONTRACT** a machine may assert on (stable core, absence semantics, the hash recipe) | `skills/charter/references/handoff.md`, bound by `HandoffManifestContractTests` |
| Build / test / package / distribution / testing lessons | skill `charter-dev-knowledge` |
| **Release state, what shipped when, the test count** | `git describe --tags` · `git log` · GitHub releases · the run itself — **never a document.** See the rule under *Status* (#191) |

## Status

### Read the rule before the facts (#191)

**A Status block is where facts with a shelf life come to rot.** This one carried a version seventeen
releases old and a matching stale test count — in the file **every** agent working in this repo loads to get
its bearings, in the section whose numbers an agent will quote back with confidence, under a SELF-UPDATING
banner nobody had cause to distrust. Writing today's numbers in only resets the clock. So the section is now
written to a rule, and **the rule is what to preserve here, not the facts under it**:

> **State a fact here only if a test BINDS it, or if its shelf life is longer than the gap between edits to
> this file. Every other fact NAMES its authority instead of quoting it.**

The two facts that rotted get **opposite** rulings, because they fail differently:

- **The version is STATED, because it is BOUND.** It has exactly one authority — `<Version>` in
  `src/Charter.Cli/Charter.Cli.csproj`, reaching the runtime as `CharterVersion.Current` — so the binding is
  one assertion, and it fires *exactly* when the fact goes wrong (a version bump) and at no other time. Same
  shape as #155 binding `charter-format`'s version range to `CharterFormat.Version` and #173's
  `HeadlessRecordContractTests` binding the record's field set to its documentation.
- **The test COUNT is NOT stated, and must not come back.** There is no authority a document can cite: it is
  the *output of a run*, and it differs per CI leg (the whole solution vs the WebKit browser-only one). A test
  pinning it would fail on every PR that adds a test — a gate whose only signal is *"somebody wrote a test"*,
  which teaches people to bump the number without reading it. And it decides nothing: no work in this repo
  goes differently at a count in the low thousands than at one a hundred higher. It reads as calibration and
  is decoration. Its authority is **the run itself** plus `.github/workflows/ci.yml`; the commands are in
  `charter-dev-knowledge`.

**`StatusVersionDriftTests`** (`tests/Charter.Cli.Tests/`) enforces both halves plus the rule's own survival:
the stated version must equal the built tool's, **no second version may appear in this section**, and a test
count may not be re-added. Placed beside `DocumentedCommandsTests` / `AgentsGuidanceTests`, the other guards
holding Charter's prose to what the code does.

### The facts

- **Version: `0.24.0`** — what `<Version>` says and what a local build reports. Bound, per above.
- **Master is routinely AHEAD of the newest tag while reporting its number, and that is normal.**
  `<Version>` is bumped when a release is *cut*, not as work lands — so a local build can report a version
  already published, and the delta is unreleased work heading for the next tag. Which side of it you are on is
  a question for git, never for this file: **`git describe --tags`** (a `-N-g<sha>` suffix means N commits
  past the tag). Bump `<Version>` when cutting the next release.
- **What shipped in which release, and what is in flight** — `git log <tag>..HEAD`, the GitHub releases, and
  `gh issue list --repo Servant-Software-LLC/Charter --state open`. Deliberately **not** enumerated here: the
  old block's hand-copied "shipped in" / "also landed" / "known-open" / "pending externally" lists were the
  worst of the rot — by the time they were read, every issue they named as open or pending was closed.
- **Team review — built vs NOT built** (`docs/plans/03-git-mediated-team-review.md` §9). This one *is* stated,
  because it is coarse, it moves once per milestone rather than once per commit, and "the verb does not exist"
  is the fact an agent most needs before it plans against one:
  - **Built:** 1 (record + fold), 2 (writer), 3 (server-side fold + panel), 4 (server-less `poll` read path),
    6 (agent voice — the `charter reply` verb, `ReviewLogWriter.AppendReply`, and reply-vs-edit guidance in the
    `charter` skill), 7 (the two-author browser test —
    `Review_panel_shows_this_authors_committed_comment_and_a_teammates_log`).
  - **Step 5 is only PARTLY built.** The read-only git *plumbing* exists (`GitCommand`, `GitTracking`) and
    serves the §5.0 tracked-gate. The §5.1 **warnings do not exist**: no behind-upstream/stale-plan warning at
    `charter review` start, no uncommitted-records reminder at session end, **no `charter review verify` verb**,
    no orphan diff (an orphan shows its `quote`, never a diff).
  - **`reopen` still has no writer.** `ReviewOpKind.Reopen` is understood by the fold, but nothing appends one
    — no API route, no CLI verb — so a `reopen` can only reach a log from outside Charter.
- **Known-open, and read it before changing the block model:** **#203** — a `:::question` nested inside a
  `::::note` renders as an answerable form but is invisible to `BlockDocument`, so `needsHuman` reads false,
  `--fail-if-needs-human` exits 0, the flatten emits its raw JSON body, and the answer can never be folded
  back. It is a format/anchor-model decision, not a code fix.
- **Decisions made** — D1 (markdown+directives hybrid), D2 (reimplement lean in C#).
