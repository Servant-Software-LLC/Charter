# Architecture B — the living `.charter.md` and direct Guardrails ingestion

**Status:** proposed design-of-record · **corrected after devil's-advocate review** · **under review**
(not yet of record) · authored by charter-architect
**Supersedes on acceptance:** invariant 5 of `docs/plans/01-combine-lavish-and-visual-plan.md` (see
[SSOT change](#the-ssot-change-invariant-5)) · **Relates to:** Charter #13/#16/#17/#18/#19, Guardrails
`plan-breakdown`

## What changed since v1 of this doc (auditable delta)

The first draft's core — **single-writer = the drafting agent; the server never writes `.charter.md`**
(§1.4) — was reviewed and **confirmed sound; it is unchanged.** Everything below is a correction folded
in after the devil's-advocate pass and David's decisions:

1. **Catalog reconciled to what actually renders (DA blocker 3).** `:::file-tree` and `:::annotated-code`
   are **struck** — they have no renderer (vaporware). `:::custom-html` is **promoted to a first-class
   `BlockKind`** (it is renderer-special-cased today but misclassifies as `Note`). The drift test binds
   to this **real** set.
2. **Solo review is supported ⇒ durability is critical-path**, not deferred. A human reviewing with no
   agent draining still gets answers into the file — via a **server-owned sidecar** (durable, not
   `.charter.md`) plus a **discrete single-writer apply** (`charter resolve`). `--apply` becomes the
   **only** path that drains answers, so a plain `poll` can't strand them (DA weak 4).
3. **Release is HELD for Architecture B** (no "cut now" default).
4. **Migration bridge made faithful (DA blocker 1).** The surviving flattener's `EmitQuestion` learns to
   fall back to the inline `answer`, so a resolved `.charter.md` doesn't flatten as all-questions-open.
5. **Guardrails epic + G1/G2/G3 rewritten to Guardrails' *actual* model (DA blocker 2):** no mid-run
   cross-skill load, no authoring-time needs-human primitive, no interactive/headless mode split.
6. **Compatibility scheme completed** as David's **frontmatter format-version marker** with a
   `skillMin ≤ file.format ≤ skillMax` range check — a **skill-version** check, not a Guardrails-binary
   pin. A **frontmatter-support slice** is now a hard prerequisite (Charter's Markdig pipeline has no
   frontmatter parser today).
7. **Engineering corrections:** `QuestionResolution.Apply` is a surgical `JsonObject` key-add (never a
   lossy `QuestionSpec` round-trip); a **document-unique-question-id** lint is added; the comparative
   go/no-go spike now **gates the invariant-5 flip and the epic filing**; the atomic-write temp lives in
   the plan's directory; and the capability lost by removing the flattener is named, not hand-waved.

## Outcome

Architecture B replaces Charter's **flatten-then-hand-off** pipeline with a **living-document** pipeline:

- The plan file — extension now **`.charter.md`** (#16) — is the **single container of review state**.
  Human input (answers to `:::question`, the agent's edits made in response to annotations) is written
  back **into the `.charter.md` itself**. There is no `answers.json` and no derived `handoff.md`.
- Guardrails `/plan-breakdown` consumes **`.charter.md` directly** — `:::` blocks and all — guided by a
  **single Charter-published format skill** (`charter-format`) loaded by the invoking session. A resolved
  `:::question` carries its `answer` inline; an open one is asked at breakdown (`AskUserQuestion`) or
  becomes a run-time `needsHuman`-halting task — never a silent default.
- **One format source of truth** (`charter-format`, bound to the renderer by a drift test) is cited by
  **both** the drafting agent (to WRITE blocks) and the breakdown session (to INTERPRET them). Structured
  `:::` blocks are richer input to a non-deterministic breakdown skill than flattened prose, not poorer.
- **Compatibility is a format-version range check, not a backward-compat dance** (§2.4): a plain-YAML
  frontmatter marker stamps the file's format version; the installed `charter-format` skill declares the
  `[skillMin, skillMax]` range it understands; the session enforces `skillMin ≤ file.format ≤ skillMax`
  with a clear "update `charter-format`" error on mismatch.

The one place the maintainer's stated vision is technically unsound — the review server writing answers
into the file **while the agent is also editing it** (a two-writer race) — is designed out in
[§1.4](#14-concurrency-single-writer-agent-the-confirmed-fix): **the drafting agent stays the single
writer of `.charter.md`; the server never writes it.** The living-document property is preserved by
serializing all writes through one writer, not by adding a second one.

## Invariants in play

| # | Invariant | Effect of Architecture B |
|---|---|---|
| 1 | Portable artifact; SDK injected only at serve time | **Preserved.** Resolved answers live in the markdown *source*, so they render into the artifact as content. The SDK is still serve-time only. |
| 2 | Comment-in-place round-trip; survives re-render of unrelated blocks | **Preserved.** Resolving question *A* changes *A*'s content-hash id (expected — you edited *A*); annotations on unrelated blocks are untouched. |
| 3 | Format single-sourced | **Strengthened — and load-bearing.** One `charter-format` skill, bound to `BlockKind`/`QuestionSpec` **and its format version** by a drift test, cited by authoring **and** breakdown. No fork/vendor into Guardrails. |
| 4 | Loopback + capability | **Preserved.** No change to server binding. The server writes a **sidecar** it owns (durability), never `.charter.md` and never outside the session root. |
| 5 | Feeds Guardrails via plain markdown (no MDX) | **Overturned — the invariant Architecture B flips.** Rewritten to "feeds Guardrails via the `.charter.md` itself, guided by `charter-format` within a declared format-version range." SSOT edit required in the same change ([below](#the-ssot-change-invariant-5)). |
| 6 | Narrow C#↔JS boundary | **Preserved.** The core change needs no SDK change — answers already POST to `/api/answers`. |
| 7 | Telemetry: none | **Unaffected.** |

---

## 1. The living-document model

### 1.1 Who writes the file: single-writer = the drafting agent

Grounding the current model:

- The server **never writes** `.charter.md`. It **re-reads and re-renders** it on every request
  (`ReviewServer.ServeStatic`, `src/Charter.Server/ReviewServer.cs:328` — `File.ReadAllText(_session.SourcePath)`
  then render+inject) and pushes a reload over `/events` when a `FileSystemWatcher` fires
  (`ReviewServer.HandleEventsAsync`, `ReviewServer.cs:638-676`).
- Human input lands in **in-memory queues**, not the file: annotations → `AnnotationStore`
  (`HandlePromptsAsync`, `ReviewServer.cs:421-490`); answers → `AnswerStore` (`HandleAnswersPostAsync`,
  `ReviewServer.cs:523-574`). Both serialize all access under one lock (`AnswerStore.cs:17`).
- The **agent** drains those queues (`charter poll`, `PollCommand.cs`) and is today the only entity that
  edits the markdown.

Architecture B keeps this: **the drafting agent is the single writer of `.charter.md`.** What changes is
*what gets written back* — resolved answers now land **inline** in `:::question` blocks, so the file
accumulates all review state. The server's role is unchanged except it now also persists the queue to a
**server-owned sidecar** for durability (§1.6) — a different file, so single-writer-of-`.charter.md`
holds. This reconciles with the existing single-writer store discipline: the in-memory stores stay
lock-serialized (server side); the **plan file has exactly one writer** (agent side, or a discrete
`charter resolve`/`poll --apply` invocation).

### 1.2 On-disk representation: resolved vs. open `:::question`

Today a `:::question` body is JSON validated to `QuestionSpec` (`src/Charter.Core/QuestionSpec.cs:65-70`
— `Id`, `Title`, `Mode`, `Options`, `Target`). There is **no field for the answer**.

**Decision: the answer is an optional field inside the `:::question` block's validated body.** A non-empty
`answer` = **resolved**; absent/empty = **open**. This keeps the block *structured* (honoring "a resolved
`:::question` is richer input than prose"), single-sources the schema in `QuestionSpec`, and gives the
breakdown a deterministic field to read.

**Open** (as authored today — unchanged):

````markdown
:::question
{ "id": "db-choice", "title": "Which datastore for the read path?",
  "mode": "single", "options": ["Postgres", "DynamoDB"], "target": "human" }
:::
````

**Resolved** (`answer` added on drain):

````markdown
:::question
{ "id": "db-choice", "title": "Which datastore for the read path?",
  "mode": "single", "options": ["Postgres", "DynamoDB"], "target": "human",
  "answer": ["Postgres"] }
:::
````

Contract for `answer` (extends `QuestionSpec`):

| Field | Type | Required | Meaning |
|---|---|---|---|
| `answer` | array of strings | optional | Absent/empty ⇒ **open**. Non-empty ⇒ **resolved**. Shape matches `Answer.Values` (`src/Charter.Server/Answer.cs:16`): single/bool/number ⇒ one element; multi ⇒ selected values; free-text ⇒ the text as one element. |

**Question ids must be unique within a document.** Today two `:::question` blocks can share an `id`;
`QuestionResolution.Apply` (§1.4) would then write the answer into **both**. Add a
**document-unique-question-id lint** (a `charter`-side validation + a test) so a duplicate id is a
review-time error, not a silent double-write.

Rejected alternative — **rewrite the block to prose** (`**Q: …** — Answered: Postgres`, what
`HandoffMarkdown` does today): it destroys the structured block, contradicting the "richer input" premise.

### 1.3 How a resolved annotation / edit lands in the file

An **annotation is a review comment**, not plan content. Resolving it means the agent reads the note (with
its `SourceLine`, resolved via `SourceMap` at drain time — `ReviewServer.cs:469-470`) and **edits the
pointed-at block**. The *edit* lands in the file; the annotation note itself stays **ephemeral** and is
not written into `.charter.md`. This keeps the handed-off plan free of review chatter.

**Scoped out of v1 (flagged, not silently dropped): in-browser WYSIWYG editing.** The SDK supports
*annotating* and *answering*, not editing arbitrary prose in place. Adding that needs a reverse source-map
(rendered edit → markdown span) Charter lacks, and would make the browser a file-writer (the §1.4 race).
v1's "living document" = **answers-inline + agent-mediated edits**, not human WYSIWYG. Deferred with an
issue.

### 1.4 Concurrency: single-writer-agent, the confirmed fix

"The server writes answers into the file the instant the human submits" is unsound:

> The agent edits `.charter.md` with its own tools (Claude Code `Edit`/`Write`) — it does **not** honor a
> server-held lock. If the **server** also writes on answer-submit, there are **two independent writers**:
> (1) agent reads at T0; (2) human submits at T1, server writes the answer → v1; (3) agent writes its
> revision at T2 from its v0 snapshot → **clobbers the human's answer** (silent last-writer-wins). No
> file lock rescues this, because one writer won't take the lock.

**Fix (reviewed and confirmed): the server never writes `.charter.md`.** Answers sit in the
lock-serialized `AnswerStore` (and its durable sidecar, §1.6) until a **single discrete writer** applies
them: the drafting agent's `charter poll --apply`, or the solo human's `charter resolve <plan>`. One
writer ⇒ no race.

Two kernels make application deterministic and safe:

- **`QuestionResolution.Apply(markdown, answersById) → markdown`** (Charter.Core) — the single tested
  kernel that writes each answer into its `:::question` block. It performs a **surgical `JsonObject`
  key-add**: parse the block's JSON body to a `System.Text.Json.Nodes.JsonObject`, set the `answer` key,
  re-serialize that object in place. It **must not** round-trip through `QuestionSpec` — that record
  captures only five keys (`QuestionSpec.cs:65-70`) and its parse normalizes/drops everything else
  (`QuestionSpec.cs:105-109`), so a round-trip would silently discard any other body key. It reuses
  `BlockDocument.Parse` to locate blocks (never re-implementing Markdig traversal, mirroring
  `HandoffMarkdown`'s discipline). **No "preserve formatting" promise:** the rewritten JSON body may be
  re-whitespaced, but every key survives and the rest of the document (prose, other blocks, frontmatter)
  is untouched.
- **Atomic write** — `charter resolve` / `poll --apply` write via a temp file **in the plan's own
  directory** (same-volume rename, exactly `SessionRegistry.Write`'s pattern, `SessionRegistry.cs:56-65`)
  so the server's per-request `File.ReadAllText` (`ReviewServer.cs:328`) always sees a complete
  old-or-new file. A plain agent `Write` (non-atomic) still degrades gracefully on a transient partial
  parse (`CharterRenderer.Render` catches it and emits a placeholder, `CharterRenderer.cs:31-43`); atomic
  is the recommended path.

### 1.5 The bidirectional sync, end to end

```
browser (SDK) ─POST /api/{key}/answers─▶ AnswerStore (in-mem) ─▶ server-owned sidecar (durable)  [no .charter.md write]
                                                    │
agent:  charter poll --apply  ─┐                    │
solo:   charter resolve <plan> ─┴── drain (answers only via --apply) ──┘
        │  QuestionResolution.Apply(markdown, answers) → atomic temp+rename in the plan dir
        ▼
FileSystemWatcher fires ─▶ /events SSE "reload" ─▶ browser reloads ─▶ server re-reads + re-renders
        (ReviewServer.cs:644-647)       (:664)            (sdk:337)          (:328-329)
```

Annotations flow the same way (`/api/poll` long-poll → agent edits the block → same reload path).

### 1.6 Durability: critical-path, because solo review is supported

**A human may review with no agent draining** (they answer questions, then hand the plan to Guardrails
later). Their answers must not be lost, and must reach the file without breaking single-writer.

- **Durability — the server persists the queue to a sidecar it owns.** On each submit, the server writes
  the queued annotations/answers to a **sidecar** (e.g. `<plan-dir>/.charter/<plan>.review.json`, or under
  the per-user state dir), atomically, so a crash before drain loses nothing. The sidecar is **not**
  `.charter.md` — the server writing its own sidecar does not violate single-writer-of-the-plan. On
  restart the server rehydrates the in-memory store from the sidecar.
- **Solo apply — `charter resolve <plan>`** (a first-class verb) reads the sidecar (or the live server)
  and applies answers inline via `QuestionResolution.Apply`, atomic-write in the plan dir. It is a
  **discrete invocation** — one writer at a time — so it is single-writer-safe by construction. The solo
  human runs it after answering; the agent case uses `charter poll --apply` (same kernel). *(Optional
  convenience: `charter review --apply-on-exit` flushes on clean shutdown when no external agent-writer is
  registered. `charter resolve` is the primary path; `--apply-on-exit` is a nicety, not the contract.)*
- **`--apply` is the only path that drains answers (DA weak 4).** A plain `charter poll` **reports**
  queued answers (for visibility) but does **not remove** them from the store/sidecar; only `--apply`
  (and `charter resolve`) drain-and-apply. So a plain `poll` can never strand an answer with no durable
  home. (Annotations are unchanged — plain `poll` still drains them; they are ephemeral notes, and the
  agent acts on them by editing.)

This makes durability part of the core delta ([§3](#3-what-existing-machinery-changes-remove--repurpose--add)),
not a deferred nicety.

---

## 2. The single format skill (`charter-format`)

### 2.1 The reconciled block catalog (ship only what renders)

The catalog is exactly the set the renderer handles — no vaporware:

| Block | `:::` directive | `BlockKind` | Status vs. today |
|---|---|---|---|
| prose / heading / list / table / code | plain markdown | `Prose`/`Heading`/`List`/`Table`/`Code` | unchanged |
| callout | `:::note` / `:::warn` | `Note` / `Warn` | unchanged |
| comparison | `:::comparison` | `Comparison` | unchanged |
| diagram (Mermaid) | `:::diagram` | `Diagram` | unchanged |
| diff | `:::diff` | `Diff` | unchanged |
| question (elicitation) | `:::question` | `Question` | gains inline `answer` (§1.2) |
| custom HTML (escape hatch) | `:::custom-html` | **`CustomHtml` (new)** | **promoted** — renderer already special-cases it (`CharterRenderer.cs:178-208`) but `ClassifyContainer` returns `Note` for it (`BlockModel.cs:275`); make it a first-class kind with its own classify route and handoff arm |
| ~~file tree~~ | ~~`:::file-tree`~~ | — | **STRUCK** — no renderer; vaporware |
| ~~annotated code~~ | ~~`:::annotated-code`~~ | — | **STRUCK** — no renderer; vaporware |

`:::custom-html` promotion is a small consistency fix: add `CustomHtml` to the `BlockKind` enum, add an
`IsCustomHtml` branch in `ClassifyContainer` (`BlockModel.cs:253-291`) routing to it, and give it a
handoff/emit arm (`HandoffMarkdown.EmitBlock`) that passes the inner HTML through verbatim — relevant only
during the bridge window, but it stops custom-html from flattening as a blockquote callout.

### 2.2 Where the format lives, and the drift test (invariant 3)

- **New skill `skills/charter-format/`** — single responsibility: the reconciled catalog + per-block
  semantics + the `:::question` open/resolved schema + the **format-version marker rule** (§2.4). It is
  the ONE artifact cited by (a) the existing `skills/charter/` authoring skill (its inlined catalog table,
  `skills/charter/SKILL.md:120-141`, becomes a citation) and (b) the breakdown session.
- **A drift test** (Charter.Core.Tests) binds `charter-format` to the **code**: it asserts the catalog
  enumerates exactly the reconciled `BlockKind` set (custom-html in; file-tree/annotated-code out), the
  `QuestionSpec` fields (including `answer`), **and the declared `charter-format` format version**. Making
  the **format version part of the bound surface** is what enforces "any catalog change bumps the version"
  (§2.4): a semantic change with no version bump fails the test. It also closes the silent-fallback
  footgun — an unknown `:::foo` must surface as a visible "unknown directive" block, not a `Note`
  (Charter Issue B).

### 2.3 How the breakdown session gets the catalog (no mid-run cross-skill load)

**Decision: the invoking Claude Code session loads `charter-format` as a top-level skill.** `/plan-breakdown`
is a skill running in a Claude Code session; that session also has `charter-format` available once
`charter skills install` (#18) has placed it in `~/.claude/skills/` (or `./.claude/skills/`). The
`plan-breakdown` SKILL.md Step 0 instructs the session: *if the input is a `.charter.md`, load the
`charter-format` skill; if it is not installed, stop and tell the user to run `charter skills install`.*
The catalog stays **single-sourced in Charter** (invariant 3) — nothing is forked into Guardrails.

**Rejected alternative — vendor the catalog into `plan-breakdown/references/`.** It removes the load-time
dependency but **forks the SSOT** into Guardrails, reintroducing the two-copies drift Architecture B
exists to remove. If a hard constraint later forbids the top-level load, the fallback is vendoring **with
the §2.4 version marker as the pinned drift check** — but the top-level-load option is cleaner and is the
recommendation.

### 2.4 Compatibility: a frontmatter format-version marker + a skill-version range

Compatibility is a **format-version** check the session performs, **not** a Charter-binary or
Guardrails-binary version pin.

- **The marker.** Each `.charter.md` carries a plain-YAML frontmatter header stamping the **format
  version** it was authored against — readable **without** `charter-format` (solving the chicken/egg:
  you must read it *before* deciding whether the installed skill can interpret the blocks):

  ````markdown
  ---
  charter-format-version: 3
  ---
  ````

- **The rule: `skillMin ≤ file.format ≤ skillMax`.** The installed `charter-format` skill declares in its
  frontmatter **both** `format-version` (= `skillMax`, the newest catalog it defines) **and** `format-min`
  (= `skillMin`, the oldest file-format it still understands — cumulative back-compat with a floor). A
  `.charter.md` at `charter-format-version: F` is consumable iff `skillMin ≤ F ≤ skillMax`.
- **Any catalog change bumps `format-version`,** enforced by the drift test's bound surface (§2.2).
- **The pin is a skill-version range, derived file-format-floor → installed `charter-format` version** —
  a check the session/breakdown does, not "file requires Guardrails binary ≥ N".

**Mismatch handling — clear, actionable, never silent:**

| Situation | Where | Error |
|---|---|---|
| **File newer than the skill** (`F > skillMax`) | breakdown session, at Step 0 | `this plan needs charter-format v{F}; installed understands up to v{skillMax}. Run 'charter skills install' to update.` |
| **File older than the skill still supports** (`F < skillMin`) | breakdown session, at Step 0 | `this plan uses retired charter-format v{F} (skill supports v{skillMin}+). Re-author against a current format.` |
| **`charter-format` not installed** | breakdown session (G3) | `run 'charter skills install' so plan-breakdown can interpret Charter blocks.` |

**Prerequisite — frontmatter support in Charter (must precede any marker).** Charter's Markdig pipeline
has **no** `UseYamlFrontMatter()` (`BlockModel.cs:215-219`), so a raw `---` today parses as a thematic
break / setext underline and **corrupts the first block**. A dedicated slice adds `UseYamlFrontMatter()`
and makes every seam frontmatter-aware: the render, the `AnchorAssignment`/`SourceMap` pass, and the
handoff/export must **skip** the `YamlFrontMatterBlock` (no anchor, not rendered as prose), and
`QuestionResolution.Apply` must **preserve it untouched**. This lands **before** the marker is written
(§6).

---

## 3. What existing machinery changes (REMOVE / REPURPOSE / ADD)

| Action | Artifact | Detail |
|---|---|---|
| **REMOVE** | `src/Charter.Core/HandoffMarkdown.cs` + tests | The `:::`→plain-CommonMark flattener is obsoleted by direct ingestion. **Biggest deletion.** Kept as the migration bridge until Guardrails ships consumption (§6). |
| **REMOVE** | `charter handoff` verb + `ReadAnswers` | `Program.cs:55-58, 213-309`. See NIT below on the capability this drops. |
| **REMOVE** | `src/Charter.Server/HandoffAnswersFile.cs` + `poll --answers-out` | `Program.cs:429-432`, `PollCommand.WriteAnswersFile`. The `answers.json` intermediate disappears. **The one genuinely sunk piece of #13.** |
| **REMOVE** | `answers.json` concept + wave-1 "handoff fixture" | Replaced by a `.charter.md` fixture + the comparative spike (§6). |
| **REPURPOSE** | `QuestionSpec.cs` | Add optional `Answer`; `resolved` = non-empty `answer` (§1.2). |
| **REPURPOSE (bridge, DA blocker 1)** | `HandoffMarkdown.EmitQuestion` (`HandoffMarkdown.cs:249-273`) | Today it resolves a `:::question` only against the external `answers` dict (`:261`), so a resolved `.charter.md` flattens **all-open**. Teach it to **fall back to the inline `spec.Answer`** when the dict lacks the id. Lands in slice 1 (the field exists then), making the bridge faithful. |
| **REPURPOSE** | `charter poll` (#13) | `--answers-out <file>` → **`--apply`** (drain + inline-write, atomic). Answers drain **only** via `--apply`; plain `poll` reports-but-doesn't-remove them (§1.6). Poll's core survives and becomes more central. |
| **REPURPOSE** | `CharterContainerRenderer.WriteQuestion` (`CharterRenderer.cs:285-328`) | Render a resolved `:::question` with its `answer` reflected (pre-selected control + a "resolved" marker), still annotatable. |
| **REPURPOSE** | `ClassifyContainer` (`BlockModel.cs:253-291`) | Add the `CustomHtml` route (§2.1); surface an unknown directive as a visible block, not a silent `Note` (Issue B). |
| **REPURPOSE** | `skills/charter/` + `references/handoff.md` | Cite `charter-format`; rewrite `handoff.md` for direct ingestion. |
| **ADD** | Frontmatter support (`Charter.Core`) | `UseYamlFrontMatter()` + strip-from-render/anchor/handoff/export; `Apply` preserves it (§2.4). **Prerequisite slice.** |
| **ADD** | `src/Charter.Core/QuestionResolution.cs` | `Apply(markdown, answersById)` — surgical `JsonObject` key-add, atomic-friendly (§1.4). |
| **ADD** | `charter resolve <plan>` (`Charter.Cli`) | Solo single-writer apply from the sidecar/live server (§1.6). |
| **ADD** | Review sidecar (`Charter.Server`) | Server-owned durable queue persistence + rehydrate-on-restart (§1.6). |
| **ADD** | `CustomHtml` `BlockKind` | Promote custom-html to first-class (§2.1). |
| **ADD** | `charter-format` skill + drift test + unique-id lint | The single format SSOT, bound to code + format version (§2.2); the document-unique-question-id check (§1.2). |
| **ADD** | `charter skills install` (#18) | Places `charter-format` where the breakdown session finds it (§2.3). |
| **KEEP** | `AnswerStore`/`/api/answers`; `AnnotationStore`/`/api/poll`; `ReviewServer`; `ReviewClient`; `SessionRegistry`/`SessionDescriptor`/`PollEnvelope`; `render`/`review`/`export`; `BlockModel`/`SourceMap` | Transport + serve + drain unchanged (the sidecar is additive). |

**Sunk-cost note on #13:** the poll/`ReviewClient`/`SessionRegistry` core **survives and is more
central** (the drain transport). Only its handoff-bridge tail (`--answers-out` → `HandoffAnswersFile` →
`answers.json` → `handoff`) is sunk — one file deleted, one flag repurposed to `--apply`.

**NIT (capability dropped by removing the flattener).** `charter handoff` is today the **only** way to get
a **directive-free, plain-CommonMark projection** of a plan — useful for a non-Charter-aware consumer,
a tool that only reads CommonMark, or a clean text diff of plan content. Architecture B removes it by
design (Guardrails no longer needs it). Name it as a **consciously dropped capability**, not dead weight:
if a non-Charter consumer surfaces, resurrect it as `charter export --flat` behind an issue rather than
pretending it never had value.

---

## 4. The issues to file (final, filing-ready)

Guardrails-side issues lead with a **context/epic** in Guardrails' own terms; Charter-side issues cover
the producer work. Each body is self-contained with cross-repo deps named. The
[split table](#41-cross-repo-split-at-a-glance) closes the section.

> **Filing gate:** the Guardrails **EPIC** and the Charter **invariant-5 flip (Issue A)** are filed/adopted
> **only after the comparative go/no-go spike passes** (§6). The other Charter issues (frontmatter,
> catalog reconciliation, durability, drift guard) can be filed immediately — they are prerequisites the
> spike itself needs.

### Guardrails-side — EPIC (repo: `Servant-Software-LLC/Guardrails`)

**Title:** `Epic: consume Charter "living plans" (.charter.md) directly in plan-breakdown`

**Body:**

> ## The arc
> Charter — the front door that feeds `/plan-breakdown` — is changing what it hands you. Today Charter
> flattens its rich plan to plain `.md` (`charter handoff`) and you break down that derived file. Charter
> is moving to a **living-document** model (Charter `docs/plans/02-architecture-b-living-document.md`,
> discussion Charter #19): the plan is a **`.charter.md`** that is the single container of its own review
> state — a settled `:::question` carries its chosen `answer` **inline**, and `:::` blocks
> (`:::comparison`/`:::diagram`/`:::diff`/`:::note`/`:::warn`/`:::custom-html`) stay intact. Charter
> **stops emitting a flattened `handoff.md`**, so `/plan-breakdown` should consume the `.charter.md`
> directly.
>
> ## What this changes for `plan-breakdown` (input contract)
> Your input widens from "plain reviewed `.md`" to "**plain `.md` OR a Charter `.charter.md`**":
> - A plain `.md` with no `:::` blocks breaks down **exactly as today** — zero regression.
> - A `.charter.md` is interpreted with the Charter-published **`charter-format`** skill (block catalog +
>   `:::question` open/resolved schema). A **resolved** `:::question` is a settled decision you fold in; an
>   **open** one you resolve at breakdown or defer to run time (G2).
>
> ## How you get the catalog (matches your real model — no mid-run cross-skill load)
> `/plan-breakdown` does not load another skill's `references/` mid-run. Instead, **the invoking Claude
> Code session loads `charter-format` as a top-level skill** (installed via Charter's `charter skills
> install`, mirroring `guardrails skills install`). Your Step 0 gains: "if the input is a `.charter.md`,
> load `charter-format`; if absent, stop and tell the user to run `charter skills install`." The catalog
> stays single-sourced in Charter — nothing is vendored into Guardrails.
>
> ## Compatibility (a format-version range check, not a binary pin)
> Each `.charter.md` carries a plain-YAML frontmatter marker `charter-format-version: F` (readable without
> `charter-format`). The installed `charter-format` skill declares `[format-min, format-version]`; the
> session checks `format-min ≤ F ≤ format-version` and errors clearly on mismatch ("update charter-format").
> You take **no** dependency on Charter's binary or parser — only on the named skill + the marker.
>
> ## Sequencing
> Charter ships the producer side first (the `.charter.md` extension Charter #16, the inline-`answer`
> schema, the `charter-format` skill, `charter skills install` #18) — the skill your team tests G1–G3
> against. Charter keeps its old `charter handoff` flatten as a migration bridge until this epic ships, so
> there is never a window where a `.charter.md` meets an old `/plan-breakdown` with no path. **Gating
> evidence:** before Charter flips its invariant and files this epic, it runs a comparative spike (break
> down the `.charter.md` AND the same plan's flattened `handoff.md`, judge the DAGs equivalent-or-better).
> This epic proceeds on that spike passing.
>
> ## Sub-issues
> - **G1** — accept a `.charter.md`; interpret `:::` blocks via the top-level `charter-format` skill;
>   enforce the format-version range.
> - **G2** — open-`:::question` handling via your existing idiom (`AskUserQuestion` at breakdown / a
>   run-time `needsHuman`-halting task), not a new gate primitive.
> - **G3** — Step 0 discovery + the "not installed → run `charter skills install`" stop.
>
> ## Acceptance (epic-level)
> A fixture `.charter.md` (one resolved + one open `:::question`) breaks down end to end: the resolved
> decision is applied; the open one is asked (interactive breakdown) or becomes a `needsHuman`-halting
> task; the folder passes `guardrails validate`. A plain `.md` still breaks down unchanged.

### Guardrails-side — G1 (repo: `Servant-Software-LLC/Guardrails`)

**Title:** `plan-breakdown: accept a .charter.md, interpret ::: blocks via the top-level charter-format skill`

**Body:**

> Under the Charter living-document epic. Widen `/plan-breakdown` Step 0:
> 1. **Detect Charter input** — input name ends `.charter.md`, or a `.md` contains `:::` directive blocks.
> 2. **Load `charter-format` as a top-level session skill** (see G3 for discovery). Do **not** attempt to
>    load it as one of plan-breakdown's own `references/` — that is not how the harness loads references.
>    Interpret the catalog from the loaded skill; do not hard-code directive semantics.
> 3. **Read the frontmatter marker `charter-format-version: F`** (plain YAML, readable without the skill)
>    and enforce `format-min ≤ F ≤ format-version` from the loaded `charter-format`. On mismatch, stop with
>    the actionable message (Charter `§2.4`).
> 4. **Interpret, don't choke.** Parse-through `:::note`/`:::warn`/`:::comparison`/`:::diagram`/`:::diff`/
>    `:::custom-html` as context. A **resolved** `:::question` (non-empty `answer`) is a settled decision —
>    fold its `answer` into the DAG, keeping the options as rationale. **Note:** Charter's catalog is
>    `:::note/warn/comparison/diagram/diff/question/custom-html` only — `:::file-tree` and
>    `:::annotated-code` do **not** exist (struck as vaporware); treat an unknown directive per
>    `charter-format`, not as a known block.
> 5. **No regression.** A `.md` with no `:::` blocks takes the existing path untouched.
>
> **Depends on (Charter):** #16, the `charter-format` skill (declares `[format-min, format-version]`), #18.
> **Depends on (Guardrails):** G3.
> **Acceptance:** a resolved-`:::question` fixture folds the decision in; a too-new `charter-format-version`
> errors clearly; a plain `.md` is unchanged; `guardrails validate` passes.

### Guardrails-side — G2 (repo: `Servant-Software-LLC/Guardrails`)

**Title:** `plan-breakdown: resolve open :::question blocks via AskUserQuestion / a run-time needsHuman task (existing idiom)`

**Body:**

> Under the Charter living-document epic. A human may deliberately leave a `:::question` **open**. Handle
> it with the idiom `plan-breakdown` **already uses** for an undecided choice — the greenfield-framework
> pattern (`references/stacks/dotnet.md:416-421`): *surface it, never default it.* There is **no** new
> authoring-time gate primitive and **no** interactive/headless mode split in the skill.
> - **When the breakdown session can ask** (a human is present), use **`AskUserQuestion`** with the
>   question `title`/`mode`/`options`; fold the answer in exactly like a resolved `:::question`.
> - **When the decision must defer to run time,** emit a **task whose action prompt writes
>   `{"needsHuman": "<title + options>"}` to the state-out path and stops** (the runtime escape hatch,
>   `SKILL.md:1138-1139`). The run harness then halts on that task — and in a non-interactive run the
>   harness's autonomy path stops (exit 2) rather than guessing.
> - **Never** synthesize a default answer for an open `:::question`.
>
> **Depends on:** G1. **Acceptance:** an open-`:::question` fixture is either asked via `AskUserQuestion`
> (interactive breakdown) or produces a `needsHuman`-writing task (deferred), never a silent default.

### Guardrails-side — G3 (repo: `Servant-Software-LLC/Guardrails`)

**Title:** `plan-breakdown: discover the charter-format skill; instruct 'charter skills install' when absent`

**Body:**

> Under the Charter living-document epic. In Step 0, when Charter input is detected (G1) and the session
> does not have `charter-format` available, **stop** with `run 'charter skills install' so plan-breakdown
> can interpret Charter blocks` — do not guess directive semantics. `charter skills install` (Charter #18)
> mirrors `guardrails skills install` and places `charter-format` in `~/.claude/skills/` (or
> `./.claude/skills/`), where the invoking session loads it top-level.
>
> **Staleness (grounded on the file marker, not CLI drift).** Do **not** model this as
> `guardrails --version` drift (that compares an installed skill to the running Guardrails CLI — a category
> error here). The freshness signal is the **format-version range check** (G1): if the file's
> `charter-format-version` exceeds the installed skill's `format-version`, the installed `charter-format`
> is stale for this plan → "run `charter skills install` to update." That is the only staleness that
> matters for interpretation.
>
> **Depends on (Charter):** #18 + the `charter-format` skill. **Acceptance:** with `charter-format`
> installed, a `.charter.md` interprets; without it, `plan-breakdown` emits the install instruction and
> does not guess.

### Charter-side issues (repo: `Servant-Software-LLC/Charter`)

**Issue A — umbrella / SSOT flip.**
`Architecture B: living .charter.md + direct Guardrails ingestion (flip invariant 5)` — Adopt
`docs/plans/02-architecture-b-living-document.md`; apply the invariant-5 rewrite to `01-…md`. Tracks the
sub-work below. Closes #19. **Filing/adoption gated on the comparative spike (Issue G) passing.** Depends
on #16, #18.

**Issue B — drift guard on the reconciled catalog + unique-id lint.**
`Renderer↔charter-format drift guard (reconciled catalog + format version); document-unique-question-id lint`
— A test binds `charter-format`'s catalog to the reconciled `BlockKind` set (**custom-html in;
file-tree/annotated-code out**), the `QuestionSpec` fields (incl. `answer`), **and the declared
`charter-format` format version** (so any catalog change forces a version bump). Make `ClassifyContainer`
surface an unknown `:::foo` as a visible block, not a silent `Note` (`BlockModel.cs:275`). Add a
document-unique-question-id validation + test (§1.2). This test is the enforceable half of the loose
Charter↔Guardrails coupling — **not optional.** Depends on Issue A + the custom-html promotion (Issue F).

**Issue C — frontmatter support (prerequisite slice).**
`Add YAML frontmatter parse+strip to the Markdig pipeline; Apply preserves it` — Charter's pipeline has no
`UseYamlFrontMatter()` (`BlockModel.cs:215-219`), so a raw `---` corrupts the first block. Add it and make
render, the `AnchorAssignment`/`SourceMap` pass, and handoff/export **skip** the `YamlFrontMatterBlock`;
`QuestionResolution.Apply` must **preserve** it untouched. **Must land before any format-version marker is
written** (Issue E). No cross-repo dep.

**Issue D — durability now (solo review is supported).**
`Server-owned review sidecar + charter resolve; --apply is the only answer-drain path` — Persist the
in-memory annotation/answer queues to a **server-owned sidecar** (not `.charter.md`), atomically, with
rehydrate-on-restart, so a crash before drain loses nothing. Add **`charter resolve <plan>`** — a discrete,
single-writer-safe apply of queued answers inline (`QuestionResolution.Apply`, atomic temp+rename in the
plan dir) for the solo (no-agent) case. Make **`--apply` the only path that drains answers**; plain `poll`
reports-but-does-not-remove them (§1.6). **This is critical-path, not deferred.** Depends on the `answer`
field + `QuestionResolution.Apply` (Issue A sub-work).

**Issue E — the format-version marker + compatibility range.**
`Frontmatter charter-format-version marker; skillMin ≤ file.format ≤ skillMax range check` — Define the
plain-YAML `charter-format-version` marker (§2.4); have `charter-format` declare `[format-min,
format-version]`; document that the breakdown session enforces the range and errors clearly on mismatch.
The marker is a skill-version range check, **not** a Charter/Guardrails binary pin. Depends on Issue C
(frontmatter support) and Issue B (format version in the drift-bound surface).

**Issue F — promote `:::custom-html`; strike `:::file-tree` and `:::annotated-code`.**
`Catalog reconciliation: custom-html → first-class BlockKind; remove vaporware directives from docs/catalog`
— Add `CustomHtml` to `BlockKind`, an `IsCustomHtml` classify route, and a handoff/emit arm
(`HandoffMarkdown`, bridge window). Remove `:::file-tree`/`:::annotated-code` from the design doc,
`skills/charter` catalog, and any epic/reference text — they have no renderer. Depends on Issue A.

**Issue G — comparative go/no-go spike (the gate).**
`Spike: break down .charter.md vs its flattened handoff.md; judge equivalence before flipping invariant 5`
— Produce a fixture `.charter.md` (resolved + open `:::question`), break down **both** it (direct, with a
prototype `charter-format` loaded) **and** its flattened `handoff.md`, and have a human /
charter-devils-advocate judge the resulting DAGs **equivalent-or-better** — not merely that `guardrails
validate` passes (which only checks DAG structure, not quality). **Passing this spike gates the
invariant-5 flip (Issue A) and the Guardrails epic filing.** Depends on the `answer` field + a prototype
`charter-format`.

**Issue H — v2: in-browser WYSIWYG block edit.** (deferred) Reverse source-map + single-writer-safe write
(§1.3/§1.4).

### 4.1 Cross-repo split at a glance

| Repo | Issue | Title (short) | Depends on (cross-repo) | Gated by spike? |
|---|---|---|---|---|
| **Charter** | #16 (exists) | `.charter.md` rename | — | — |
| **Charter** | #18 (exists) | `skills install` | — | — |
| **Charter** | G (new) | Comparative spike (the gate) | — | **is the gate** |
| **Charter** | C (new) | Frontmatter support (prereq) | — | no |
| **Charter** | F (new) | custom-html promote / strike vaporware | — | no |
| **Charter** | D (new) | Durability now + `charter resolve` | — | no |
| **Charter** | B (new) | Drift guard (reconciled catalog + version) + unique-id | Charter F | no |
| **Charter** | E (new) | Format-version marker + range | Charter C, B | no |
| **Charter** | A (new) | Umbrella / invariant-5 flip | Charter #16, #18 | **yes (Issue G)** |
| **Charter** | H (new) | v2 in-browser edit | — (deferred) | — |
| **Charter** | #19 (exists) | *Discussion — resolved by A* | closed by A | — |
| **Guardrails** | EPIC (new) | Consume Charter living plans directly | Charter A shipped first | **yes (Issue G)** |
| **Guardrails** | G1 (new) | Accept `.charter.md`; top-level `charter-format`; range check | Charter #16 + skill; Guardrails G3 | — |
| **Guardrails** | G2 (new) | Open-`:::question`: `AskUserQuestion` / run-time `needsHuman` | Guardrails G1 | — |
| **Guardrails** | G3 (new) | Discover the skill; install instruction | Charter #18 + skill | — |

**Ordering across repos:** Charter #16 → Charter C/F/D + the `answer` field + a prototype `charter-format`
→ **Charter G (comparative spike) PASSES** → Charter A (adopt/flip) + file the Guardrails EPIC → Charter B
+ E + #18 finalize the producer side → Guardrails G3 → G1 → G2 ship consumption → **Charter removes the
flatten** (bridge retired once a consuming Guardrails exists). Neither team deletes a bridge the other
still needs.

---

## 5. Risks, trade-offs, and failure modes

### Determinism vs. richer signal
Flattening was deterministic; direct ingestion routes block interpretation through the LLM breakdown.
Breakdown is *already* non-deterministic, so the marginal loss is small and the signal gain (explicit
decisions/options) real. **Mitigations:** the resolved `answer` is a deterministic datum the LLM reads
(not re-derived); `charter-format` fixes the catalog; and the **comparative spike (Issue G) gates
adoption** — we do not flip invariant 5 on faith that direct breakdown is as good, we measure it against
the flattened baseline first.

### Loose Charter↔Guardrails coupling via a skill
The contract is "the session's `charter-format` agrees with Charter's renderer." **Mitigations, layered:**
(1) the **drift test** (§2.2) binds the skill catalog **and its format version** to the code — enforceable
Charter-side; (2) the **format-version range check** (§2.4) turns a too-old skill into a legible "update
charter-format", not a misparse; (3) single-sourcing (no Guardrails-side vendor) means there is one
catalog to keep honest, not two.

### Migration-bridge faithfulness (DA blocker 1 — fixed)
During the bridge window a resolved `.charter.md` must flatten faithfully. `HandoffMarkdown.EmitQuestion`
today reads only the external answers dict (`HandoffMarkdown.cs:261`) → would flatten resolved questions
as **all-open**. The fix (inline-`answer` fallback, landed in slice 1) makes the bridge faithful; without
it the bridge silently loses every human decision.

### Concurrency of live file mutation
The only unsound reading (server-writes-plan) is designed out (§1.4). The server writes only its **own
sidecar** (durability), never `.charter.md`. Residual: a large **non-atomic** agent write briefly racing
the server read → transient parse-degrade → self-heals. Bounded, visible, never data loss. `charter
resolve` / `poll --apply` are discrete single-writer invocations, atomic in the plan dir.

### Stranded answers (DA weak 4 — fixed)
A plain `poll` that drained answers destructively could strand them (removed from the store, never in the
file). Fixed: **`--apply` is the only answer-drain path**; plain `poll` reports-but-does-not-remove; the
sidecar is a second durable copy.

### Open-`:::question` in an unattended run
`plan-breakdown` never silently defaults an open question. Interactive breakdown asks (`AskUserQuestion`);
otherwise it emits a task that writes `needsHuman` and halts — and the run harness stops (exit 2) in a
non-interactive context. This is Guardrails' existing greenfield-framework idiom, not new machinery.

### Pressure on the invariants
- **Invariant 3** is now load-bearing for *correctness of ingestion*, held honest by the drift test +
  version marker — **not optional.**
- **Invariant 4** is specifically *not* weakened: the server gained sidecar-write authority only over a
  file it owns inside the session, never over `.charter.md` or outside the root.
- **Invariant 5** is deliberately overturned; SSOT updated in lockstep.

### Scope
The rejected over-build is server-side write-back of the plan for "instant" mutation (§1.4). Instant
*visual* confirmation already exists (native form state + the SDK `answer-submitted` event,
`sdk/charter-annotate.js:275-276`); the sidecar covers durability without a second plan-writer.

---

## 6. Sequencing and migration

Prove the risky bet first. The riskiest assumption is **"direct breakdown of a `:::`-bearing `.charter.md`
is at least as good as the flattened `handoff.md`."** It is proven by a **comparative spike that gates
everything downstream** — including the invariant-5 flip and the epic filing.

**Prerequisite / gating slice (before any adoption):**
1. **`QuestionSpec.answer`** + **`QuestionResolution.Apply`** (surgical `JsonObject`) + the inline-answer
   fallback in `EmitQuestion` (bridge faithfulness). Deterministic, unit-testable.
2. **Frontmatter support** (Issue C) — must precede the marker.
3. **A prototype `charter-format` skill** + a fixture `.charter.md` (resolved + open `:::question`).
4. **Issue G — the comparative spike:** break down the `.charter.md` (direct) AND its flattened
   `handoff.md`; judge the DAGs equivalent-or-better. **PASS gates the invariant-5 flip and the epic
   filing.** FAIL sends the design back, cheaply, before any deletion.

**On PASS — producer side:**
5. Flip invariant 5 (adopt Issue A); file the Guardrails epic. Land `charter-format` (final) + the drift
   test + unique-id lint (B), the format-version marker + range (E), custom-html promotion + strike (F),
   `charter skills install` (#18), the durability sidecar + `charter resolve` + `--apply`-only-drain (D),
   resolved-question rendering.

**Consumer side, then the clean break:**
6. Guardrails ships G3 → G1 → G2.
7. **Charter removes** `HandoffMarkdown` / `handoff` / `HandoffAnswersFile` — **after** a consuming
   Guardrails exists, so the bridge is retired only when a compatible consumer is available.

**Interaction with the pending queue and the held release:**

| Item | Interaction |
|---|---|
| **#16 `.charter.md`** | Prerequisite — land first. |
| **#18 `skills install`** | Prerequisite for ingestion — how `charter-format` reaches the breakdown session. Mirror `guardrails skills install`. |
| **#13 `poll`** | Keep the poll core; repurpose `--answers-out`→`--apply`; delete `HandoffAnswersFile`; make `--apply` the only answer-drain path. |
| **#17 `convert`** | Orthogonal but synergistic — plain `.md` → `.charter.md` is a living-doc producer that targets `charter-format`. After the core; not on the critical path. |
| **#19 discussion** | Resolved by adopting Issue A. |
| **Release** | **HELD for Architecture B.** No release until the living-document pipeline **and** the Guardrails consumption ship (i.e. through step 7). Do not ship a half-state (flatten deleted with no consuming Guardrails; or living-doc mode with no durability). The prior "cut now" option is withdrawn. |

**Migration bridge (explicit):** from slice 1 until step 7, `charter handoff` **stays** — now faithful to
inline answers (blocker-1 fix) — and is removed only once a Guardrails with G1–G3 is released.

---

## The SSOT change (invariant 5)

On acceptance (after Issue G passes), replace invariant 5 in
`docs/plans/01-combine-lavish-and-visual-plan.md:92`:

**From:**
> 5. **Feeds Guardrails via plain markdown** — the handoff is canonical reviewed markdown, no MDX.

**To:**
> 5. **Feeds Guardrails via the living `.charter.md`** — `/plan-breakdown` consumes the `.charter.md`
>    directly (`:::` blocks and all), guided by the single `charter-format` skill the invoking session
>    loads. A resolved `:::question` carries its `answer` inline; an open one is asked at breakdown or
>    becomes a run-time `needsHuman` task — never a silent default. No flattened handoff and no
>    `answers.json`: the `.charter.md` is the single container of review state. Compatibility is a
>    **format-version range check** (`charter-format-version` frontmatter marker vs. the skill's
>    `[format-min, format-version]`), never Charter's parser/binary or a Guardrails-binary pin; a too-old
>    skill fails with a clear "update charter-format" error, and plain-markdown plans that never touched
>    Charter still break down unchanged.

The "Architecture", "Format & block catalog", "Milestones (wave 5)", and "Open items" sections of `01-…md`
that reference `charter handoff` / the handoff fixture / "plain markdown" / `:::file-tree` /
`:::annotated-code` also update on acceptance; this doc is the authority for those edits.

## Open questions for David (decided-with-default; confirm or override)

1. **`charter resolve` (verb) vs. `review --apply-on-exit` as the primary solo-apply.** *Default:*
   `charter resolve` (explicit, discrete, single-writer-safe, same kernel as `poll --apply`);
   `--apply-on-exit` optional.
2. **Sidecar location** — beside the plan (`<plan-dir>/.charter/…`) vs. the per-user state dir. *Default:*
   per-user state dir (keeps the plan dir clean; consistent with `SessionRegistry`). Confirm.
3. **Format version as an integer vs. semver.** *Default:* a **monotonic integer** contract version
   (simplest for `skillMin ≤ file.format ≤ skillMax`); semver only if a need for minor/patch nuance appears.
4. **Does the authoring agent write the marker, or does `charter` tooling stamp it?** *Default:* the agent
   writes it per `charter-format` guidance; a `charter`-side lint verifies presence + range. Confirm.
5. **`charter-format` separate skill vs. folded into `charter`.** *Default:* separate (ISP — the breakdown
   session needs only the format).
