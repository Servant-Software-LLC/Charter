# Architecture B — the living `.charter.md` and direct Guardrails ingestion

**Status:** **DESIGN OF RECORD** — adopted 2026-07-23 · go/no-go spike **#25 PASSED — GO** (direct
`.charter.md` ingestion breaks down at least as well as the flatten) · David's Q1/Q2 decisions folded ·
authored by charter-architect
**Supersedes:** invariant 5 of `docs/plans/01-combine-lavish-and-visual-plan.md` (flipped in the same
change — see [SSOT change](#the-ssot-change-invariant-5)) · **Relates to:** Charter #13/#16/#17/#18/#19,
Guardrails `plan-breakdown` + autonomous mode (#361)

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

**Adoption delta (2026-07-23 — spike passed + David's two decisions):**

8. **Go/no-go spike #25 PASSED — GO.** The riskiest bet (§6) is proven: a human/charter-devils-advocate
   judged the DAG from direct `.charter.md` ingestion **equivalent-or-better** than the DAG from the same
   plan's flattened `handoff.md`. The gate is cleared → invariant 5 is flipped and the Guardrails epic is
   cleared to file (both in this change).
9. **Q1 — an open `:::question` at run time is DIAL-GOVERNED, not an unconditional halt (Guardrails #361).**
   *Breakdown time is unchanged:* interactive `/plan-breakdown` **asks (`AskUserQuestion`) or emits a task
   that writes `{"needsHuman": …}`, never silently defaults.* What changes is the **run-time** framing: when
   that emitted `needsHuman` fires in an **autonomous run**, it is the agent-emitted needs-human the approved
   autonomous classifier already governs as a **dial-eligible judgment call** (`12-autonomous-mode.md`
   §4.1). Below the dial → **proceed with a recorded best-guess** (forensic trail); at/above the dial →
   **escalate** (honest halt + async firstmate answer file, exit **`EscalationsPending = 4`**). It is **NOT
   exit 2 and NOT an unconditional halt**, and it needs **no new Guardrails gate type** — the classifier
   already covers agent-needs-human. Every "run-time `needsHuman`-halting task / exit 2" phrasing below is
   corrected to this.
10. **Q2 — the flatten STAYS; the autonomous pipeline is UNAFFECTED (real simplification).** Direct
    `.charter.md` ingestion is the **INTERACTIVE** breakdown path only. The **headless/autonomous**
    breakdown (harness-driven, no Skill tool) keeps consuming the **flattened `charter handoff` output** —
    which the autonomous pipeline already does. So **`HandoffMarkdown` / `charter handoff` STAY
    PERMANENTLY** as the headless/plain-markdown projection (§3 moves them REMOVE → KEEP). There is **no
    "slice-6 flatten deletion" and no deletion window** — the flatten just stays. The inline-`answer`
    fallback in `EmitQuestion` (shipped slice 1) is now a **permanent** feature (the flatten faithfully
    projects resolved questions), not a throwaway bridge. The "lost CommonMark projection" NIT **dissolves**
    (handoff stays).

## Outcome

Architecture B replaces Charter's **flatten-then-hand-off** pipeline with a **living-document** pipeline:

- The plan file — extension now **`.charter.md`** (#16) — is the **single container of review state**.
  Human input (answers to `:::question`, the agent's edits made in response to annotations) is written
  back **into the `.charter.md` itself**. There is no `answers.json`. The flattened `handoff.md` is **not**
  the container of review state — but it **is retained permanently** as the headless/plain-markdown
  projection (Q2, §3): the `.charter.md` carries the truth, and `charter handoff` projects it to plain
  markdown on demand for the autonomous pipeline.
- **Two breakdown paths (Q2 — decided).** The **INTERACTIVE** `/plan-breakdown` (a human-driven Claude Code
  session with the Skill tool) consumes **`.charter.md` directly** — `:::` blocks and all — guided by a
  **single Charter-published format skill** (`charter-format`) loaded top-level by the invoking session. The
  **HEADLESS/AUTONOMOUS** breakdown (harness-driven, no Skill tool) consumes the **flattened `charter
  handoff` output** (plain markdown), exactly as the autonomous pipeline does today — so it needs no
  `charter-format`, no parser, no Charter dependency, and is **unaffected** by this change.
- A resolved `:::question` carries its `answer` inline (the flatten faithfully projects it too). An open one
  is **asked at breakdown** (`AskUserQuestion`) or **emits a task that writes `{"needsHuman": …}`** — never a
  silent default. At **run time** that `needsHuman` is **dial-governed** in an autonomous run (Guardrails
  #361): best-guess-with-forensic-trail below the dial, else escalate (exit **`EscalationsPending = 4`**,
  async answer channel) — not an unconditional halt, not exit 2 (Q1).
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
| 5 | Feeds Guardrails via plain markdown (no MDX) | **Overturned — the invariant Architecture B flips (to a DUAL path).** Rewritten to "feeds Guardrails via the `.charter.md` itself in the **interactive** path (guided by `charter-format` within a declared format-version range), **and** via the retained flattened `charter handoff` output in the **headless/autonomous** path." No MDX in either. SSOT edit made in the same change ([below](#the-ssot-change-invariant-5)). |
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

**Decision: the invoking Claude Code session loads `charter-format` as a top-level skill.** This is the
**INTERACTIVE** path only (Q2). `/plan-breakdown` is a skill running in a Claude Code session; that session
also has `charter-format` available once `charter skills install` (#18) has placed it in `~/.claude/skills/`
(or `./.claude/skills/`). The interactive `plan-breakdown` SKILL.md Step 0 instructs the session: *if the
input is a `.charter.md`, load the `charter-format` skill; if it is not installed, stop and tell the user to
run `charter skills install`.* The catalog stays **single-sourced in Charter** (invariant 3) — nothing is
forked into Guardrails. **The headless/autonomous path never reaches this step** — it consumes the flattened
`charter handoff` output (plain markdown), so it needs no `charter-format` (G3).

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
| **KEEP (repurposed role — Q2)** | `src/Charter.Core/HandoffMarkdown.cs` + tests | **The flatten STAYS PERMANENTLY** as the headless/plain-markdown projection. Direct `.charter.md` ingestion is the *interactive* path; the *headless/autonomous* breakdown consumes this flattened `handoff.md` (which the autonomous pipeline already does). Not a bridge with a deletion window — a permanent projection. |
| **KEEP (repurposed role — Q2)** | `charter handoff` verb + `ReadAnswers` | `Program.cs:55-58, 213-309`. The CLI verb that produces the headless projection above. Stays. (The "lost CommonMark projection" NIT dissolves — this *is* that projection, retained.) |
| **REMOVE** | `src/Charter.Server/HandoffAnswersFile.cs` + `poll --answers-out` | `Program.cs:429-432`, `PollCommand.WriteAnswersFile`. The `answers.json` **intermediate** disappears — superseded by the inline `answer` field (§1.2); the flatten now reads inline answers directly (`EmitQuestion` fallback below), never a side-car answers file. **The one genuinely sunk piece of #13.** |
| **REMOVE** | `answers.json` concept + wave-1 "handoff fixture" as the *primary* fixture | The `.charter.md` fixture + the comparative spike (§6) are primary. The flattened `handoff.md` output still exists (the flatten stays) and `HandoffMarkdown`'s own tests still pin its shape; the removed piece is the `answers.json` side-car concept. |
| **REPURPOSE** | `QuestionSpec.cs` | Add optional `Answer`; `resolved` = non-empty `answer` (§1.2). |
| **REPURPOSE → permanent feature (Q2)** | `HandoffMarkdown.EmitQuestion` (`HandoffMarkdown.cs:249-273`) | Today it resolves a `:::question` only against the external `answers` dict (`:261`), so a resolved `.charter.md` flattens **all-open**. Teach it to **fall back to the inline `spec.Answer`** when the dict lacks the id. Shipped in slice 1. Because the flatten stays permanently, this is now a **permanent** correctness property (the headless projection faithfully carries resolved decisions), not a throwaway bridge fix. |
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
central** (the drain transport). Only the `answers.json` side-car tail (`--answers-out` →
`HandoffAnswersFile` → `answers.json`) is sunk — one file deleted, one flag repurposed to `--apply`. **The
flattener and `charter handoff` are NOT sunk (Q2)** — they are retained as the headless projection.

**Resolved NIT (Q2 — the capability is retained, not dropped).** An earlier draft named the loss of the
`charter handoff` **directive-free, plain-CommonMark projection** (useful for a non-Charter-aware consumer,
a CommonMark-only tool, or a clean text diff of plan content) as a consciously dropped capability. With Q2
that capability **stays**: `charter handoff` is exactly the headless/autonomous breakdown's input, so the
plain-CommonMark projection is a permanent, load-bearing output — no `charter export --flat` resurrection is
needed.

---

## 4. The issues to file (final, filing-ready)

Guardrails-side issues lead with a **context/epic** in Guardrails' own terms; Charter-side issues cover
the producer work. Each body is self-contained with cross-repo deps named. The
[split table](#41-cross-repo-split-at-a-glance) closes the section.

> **Filing gate — CLEARED (2026-07-23).** The comparative go/no-go spike (§6, Charter **#25**) **PASSED —
> GO**, so the Guardrails **EPIC** and the Charter **invariant-5 flip (Issue A)** are cleared to file/adopt.
> The other Charter issues (frontmatter, catalog reconciliation, durability, drift guard) were always
> file-immediately prerequisites the spike itself needed.

### Guardrails-side — EPIC (repo: `Servant-Software-LLC/Guardrails`)

**Title:** `Epic: consume Charter "living plans" (.charter.md) directly in the INTERACTIVE plan-breakdown (autonomous pipeline UNAFFECTED)`

**Body:** (the filing-ready text is [in the report / §4 of this doc](#the-issues-to-file-final-filing-ready);
reproduced verbatim below)

> ## TL;DR for the Guardrails team — your autonomous pipeline is UNAFFECTED
> Nothing changes for a headless/autonomous `guardrails run`. The between-wave / JIT breakdown your harness
> drives keeps consuming Charter's **flattened** plain-markdown projection (`charter handoff` output),
> exactly as today. Charter is **keeping that flatten permanently** as its headless/plain-markdown
> projection — there is no deletion, no migration window, no half-state.
>
> The single change this epic asks for is additive and lives entirely in the **INTERACTIVE**
> `/plan-breakdown` (a human-driven Claude Code session with the Skill tool): it gains the ability to
> consume a Charter **`.charter.md`** directly — richer signal than the flatten — when one is handed to it.
>
> ## Why
> Charter (the front door that feeds `/plan-breakdown`) is moving to a **living-document** model (Charter
> `docs/plans/02-architecture-b-living-document.md`, of record; go/no-go spike Charter **#25 PASSED**): the
> plan is a `.charter.md` that is the single container of its own review state — a settled `:::question`
> carries its chosen `answer` **inline**, and `:::` blocks (`:::note`/`:::warn`/`:::comparison`/`:::diagram`/
> `:::diff`/`:::custom-html`) stay intact. Flattening that to plain markdown is lossy (structured decisions
> and options become prose). For an INTERACTIVE breakdown, where a human is present, consuming the
> `.charter.md` directly preserves that signal.
>
> ## Two paths, decided (this is the de-risk)
> | Path | Driver | Input | How it reads Charter blocks |
> |---|---|---|---|
> | **Interactive** | human runs `/plan-breakdown` (Skill tool present) | the `.charter.md` **directly** | the Charter-published `charter-format` skill, loaded top-level in the session |
> | **Headless / autonomous** | the harness (no Skill tool) | the **flattened** `charter handoff` output (plain `.md`) | n/a — it is already plain markdown |
>
> So the autonomous pipeline (#361) never needs `charter-format`, never parses `:::`, never takes a Charter
> dependency. It reads plain markdown, as it does now.
>
> ## The decoupling (what you are NOT signing up for)
> - **No parser, no binary dependency.** You never parse Charter's directive syntax and never depend on
>   Charter's binary. The interactive path interprets blocks via a **skill** (`charter-format`) Charter
>   publishes and installs — a documentation contract, not a code one.
> - **One SSOT, no fork.** The block catalog + `:::question` open/resolved schema lives ONCE, in Charter's
>   `charter-format` skill, bound to Charter's renderer by a drift test. Nothing is vendored into Guardrails
>   (no `references/` copy to drift).
> - **No mid-run cross-skill load.** `/plan-breakdown` does not load another skill's `references/` mid-run.
>   The invoking session loads `charter-format` as a **top-level** skill (installed via Charter's
>   `charter skills install`, mirroring `guardrails skills install`).
>
> ## Compatibility — a format-version range check, not a binary pin
> Each `.charter.md` carries a plain-YAML frontmatter marker `charter-format-version: F` (readable WITHOUT
> `charter-format`). The installed `charter-format` skill declares `[format-min, format-version]`; the
> interactive session checks `format-min ≤ F ≤ format-version` and errors clearly on mismatch ("run
> `charter skills install` to update `charter-format`"). **Absent marker ⇒ reject with an actionable error**
> (never silently assume a version). **Min-version pin:** the interactive path requires an installed
> `charter-format` whose `[format-min, format-version]` range covers the file's `charter-format-version`;
> Charter publishes **v1** as the first format version and stamps every `.charter.md` at `≥ 1`.
>
> ## Open `:::question` at run time — flows through YOUR dial (#361), no new gate type
> A human may deliberately leave a `:::question` **open**. Two levels, both already in your model:
> - **At breakdown time** (interactive), an open `:::question` is surfaced, never defaulted — `AskUserQuestion`
>   if a human is present, else the breakdown emits a task whose action writes `{"needsHuman": "<question>"}`
>   and stops. This is the greenfield "surface it, never default it" idiom you already ship
>   (`references/stacks/dotnet.md:416-421`).
> - **At run time**, that emitted `{"needsHuman": …}` is exactly the agent-emitted needs-human your
>   autonomous classifier ALREADY governs as a **dial-eligible judgment call** (`docs/plans/12-autonomous-mode.md`
>   §4.1): criticality < the dial → **proceed with a recorded best-guess** (forensic trail in `decisions[]` +
>   `autonomy.jsonl`); criticality ≥ the dial → **escalate** (honest halt enriched with the question,
>   firstmate answers async via an answer file, run exits **`EscalationsPending = 4`**). It is NOT an
>   unconditional halt and NOT exit 2. **No new gate type is needed** — the classifier already covers
>   agent-needs-human.
>
> ## Sequencing
> Charter ships the producer side first: the `.charter.md` extension (Charter #16), the inline-`answer`
> schema, the `charter-format` skill (declaring `[format-min, format-version]`), and `charter skills install`
> (Charter #18) — what your team tests G1–G3 against. Because the flatten stays, there is never a window
> where a `.charter.md` meets an interactive `/plan-breakdown` that can't read it — worst case it emits the
> actionable "install/update `charter-format`" error. Spike Charter #25 (break down the `.charter.md` vs. its
> flattened `handoff.md`, judge equivalent-or-better) has **PASSED**, so this epic is cleared to proceed.
>
> ## Sub-issues
> - **G1** — interactive `/plan-breakdown` detects a `.charter.md` (or `:::`-bearing input), interprets `:::`
>   via the top-level `charter-format` skill, enforces the format-version range (absent marker ⇒ reject), a
>   resolved `:::question`'s `answer` folds in, a plain `.md` is unchanged.
> - **G2** — open-`:::question`: interactive → `AskUserQuestion`; run time → the dial-governed
>   agent-needs-human (#361), NOT a new gate primitive. Plus the trust-asymmetry note.
> - **G3** — `charter-format` discovery for the interactive session (installed via `charter skills install`);
>   the headless path does not need it.
>
> ## Acceptance (epic-level)
> A fixture `.charter.md` (one resolved + one open `:::question`) breaks down end to end **interactively**:
> the resolved decision is applied; the open one is asked via `AskUserQuestion` (or, deferred, becomes an
> agent-needs-human task the autonomous run then dial-governs per #361); the folder passes `guardrails
> validate`. A plain `.md` still breaks down unchanged. The flattened `handoff.md` path is untouched.

### Guardrails-side — G1 (repo: `Servant-Software-LLC/Guardrails`)

**Title:** `plan-breakdown (INTERACTIVE): accept a .charter.md, interpret ::: via top-level charter-format, enforce the format-version range`

**Body:**

> Under the Charter living-document epic. Applies to the **interactive** `/plan-breakdown` only (a
> human-driven session with the Skill tool). The headless/autonomous path is untouched — it consumes the
> flattened `charter handoff` output (plain markdown) and needs none of this.
>
> Widen interactive Step 0:
> 1. **Detect Charter input** — the input name ends `.charter.md`, or a `.md` contains `:::` directive blocks.
> 2. **Load `charter-format` as a top-level session skill** (discovery in G3). Do **not** try to load it as
>    one of plan-breakdown's own `references/` — that is not how the harness loads references. Interpret the
>    catalog from the loaded skill; do not hard-code directive semantics.
> 3. **Read the frontmatter marker `charter-format-version: F`** (plain YAML, readable without the skill).
>    **Absent marker ⇒ reject with an actionable error** and stop (`this plan has no charter-format-version
>    marker; re-author it with a current charter-format, or hand a plain .md`) — never silently assume a
>    version. Present ⇒ enforce `format-min ≤ F ≤ format-version` from the loaded `charter-format`; on
>    mismatch stop with the actionable message (too new ⇒ "run `charter skills install` to update
>    `charter-format`"; too old ⇒ "re-author against a current format"). (Charter `§2.4`.)
> 4. **Interpret, don't choke.** Parse-through `:::note`/`:::warn`/`:::comparison`/`:::diagram`/`:::diff`/
>    `:::custom-html` as context. A **resolved** `:::question` (non-empty inline `answer`) is a settled
>    decision — fold its `answer` into the DAG, keeping the options as rationale. Charter's catalog is
>    `:::note/warn/comparison/diagram/diff/question/custom-html` only — `:::file-tree` and `:::annotated-code`
>    do **not** exist (struck as vaporware); treat an unknown directive per `charter-format`, not as a known
>    block.
> 5. **No regression.** A `.md` with no `:::` blocks takes the existing path untouched.
>
> **Depends on (Charter):** #16, the `charter-format` skill (declares `[format-min, format-version]`), #18.
> **Depends on (Guardrails):** G3.
> **Acceptance:** a resolved-`:::question` fixture folds the decision in; a `.charter.md` with **no marker**
> is rejected with the actionable error; a too-new `charter-format-version` errors clearly; a plain `.md` is
> unchanged; `guardrails validate` passes.

### Guardrails-side — G2 (repo: `Servant-Software-LLC/Guardrails`)

**Title:** `plan-breakdown: open :::question → AskUserQuestion (interactive) / dial-governed agent-needs-human at run time (#361) — no new gate type`

**Body:**

> Under the Charter living-document epic. A human may deliberately leave a `:::question` **open**. Handle it
> with the idiom `plan-breakdown` **already** uses for an undecided choice — the greenfield "surface it,
> never default it" pattern (`references/stacks/dotnet.md:416-421`). There is **no new gate primitive** and
> no interactive/headless split inside the skill's authoring logic; the split is only which resolution fires.
>
> **At breakdown time:**
> - **Interactive (a human is present):** use **`AskUserQuestion`** with the question `title`/`mode`/
>   `options`; fold the answer in exactly like a resolved `:::question`.
> - **Deferred to run time:** emit a task whose action prompt writes `{"needsHuman": "<title + options>"}`
>   to the state-out path and stops — the shipped runtime escape hatch.
> - **Never** synthesize a default answer for an open `:::question`.
>
> **At run time — the #361 alignment (this is the correction).** When that emitted `{"needsHuman": …}`
> reaches an **autonomous run**, it is an **agent-emitted needs-human** — which the autonomous classifier
> ALREADY treats as a **dial-eligible judgment call** (`docs/plans/12-autonomous-mode.md` §4.1, class (a)).
> It is therefore **dial-governed**, NOT an unconditional halt:
> - criticality **<** the dial (`escalationThreshold`) → **proceed with a recorded best-guess**, full
>   forensic trail (`decisions[]` `proceeded-best-guess` + `autonomy.jsonl`);
> - criticality **≥** the dial → **escalate**: honest halt enriched with the question, firstmate answers
>   asynchronously via an answer file, run exits **`EscalationsPending = 4`** (NOT `2`).
>
> **Explicitly: no new gate type is needed.** The agent-needs-human classifier + the dial cover this end to
> end. The old framing ("run-time needsHuman-halting task, exit 2") is superseded by the approved autonomous
> model (#361).
>
> **Trust-asymmetry note (load-bearing).** A `:::question` resolved **at breakdown time** — whether via
> `AskUserQuestion` or already-inline in the `.charter.md` — is **trusted authoring input**: it shapes the
> DAG the human then reviews. A `:::question` answered **at run time** via the escalation answer channel is
> **untrusted, delimited data** in the autonomous model (`12-autonomous-mode.md` §7.4, Finding 4): the
> injected `needsHuman` answer text is wrapped as delimited human-answer data, can **never reach the verdict
> surface**, and only composes the next attempt's prompt (whose deterministic guardrails still gate the
> result). Keep the two kinds of "answer" distinct — never treat a run-time answer as authoring-trust.
>
> **Depends on:** G1. **Acceptance:** an open-`:::question` fixture is either asked via `AskUserQuestion`
> (interactive) or produces an agent-needs-human task; under an autonomous run that task is dial-governed
> (best-guess below threshold / escalate to `EscalationsPending = 4` at or above it), never silently
> defaulted, and no new gate type is introduced.

### Guardrails-side — G3 (repo: `Servant-Software-LLC/Guardrails`)

**Title:** `plan-breakdown (INTERACTIVE): discover the charter-format skill; instruct 'charter skills install' when absent (headless path exempt)`

**Body:**

> Under the Charter living-document epic. In the **interactive** Step 0, when Charter input is detected (G1)
> and the session does not have `charter-format` available, **stop** with `run 'charter skills install' so
> plan-breakdown can interpret Charter blocks` — do not guess directive semantics. `charter skills install`
> (Charter #18) mirrors `guardrails skills install` and places `charter-format` in `~/.claude/skills/` (or
> `./.claude/skills/`), where the invoking session loads it top-level.
>
> **The headless / autonomous path does NOT need `charter-format`.** It consumes the flattened `charter
> handoff` output (plain markdown), so it never detects Charter input, never loads the skill, and this stop
> never fires there. `charter-format` discovery is an interactive-session concern only.
>
> **Staleness is grounded on the file marker, not CLI drift.** Do **not** model this as
> `guardrails --version` drift (that compares an installed skill to the running Guardrails CLI — a category
> error here). The freshness signal is the **format-version range check** (G1): if the file's
> `charter-format-version` exceeds the installed skill's `format-version`, the installed `charter-format`
> is stale for this plan → "run `charter skills install` to update." That is the only staleness that
> matters for interpretation.
>
> **Depends on (Charter):** #18 + the `charter-format` skill. **Acceptance:** with `charter-format`
> installed, an interactive `.charter.md` interprets; without it, interactive `plan-breakdown` emits the
> install instruction and does not guess; the headless flatten path is unaffected.

### Charter-side issues (repo: `Servant-Software-LLC/Charter`)

**Issue A — umbrella / SSOT flip.**
`Architecture B: living .charter.md + direct Guardrails ingestion (flip invariant 5, DUAL path)` — Adopt
`docs/plans/02-architecture-b-living-document.md`; apply the invariant-5 rewrite to `01-…md` (the **dual
path**: interactive consumes `.charter.md`, headless/autonomous consumes the retained flattened `charter
handoff`). Tracks the sub-work below. Closes #19. **Spike (Issue G / #25) PASSED — adoption cleared.**
Depends on #16, #18. **Note (Q2):** the flatten is **retained permanently** — this issue does NOT delete
`HandoffMarkdown`/`charter handoff`.

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
`charter-format`. **STATUS: #25 PASSED — GO (2026-07-23)** — the gate is cleared; Issue A adoption + the
Guardrails EPIC are unblocked.

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
→ **Charter G (comparative spike #25) PASSED** → Charter A (adopt/flip) + file the Guardrails EPIC → Charter
B + E + #18 finalize the producer side → Guardrails G3 → G1 → G2 ship the **interactive** consumption.
**No flatten-removal step (Q2):** `HandoffMarkdown`/`charter handoff` stay permanently as the
headless/autonomous projection — there is no bridge to retire and no deletion window.

---

## 5. Risks, trade-offs, and failure modes

### Determinism vs. richer signal
Flattening was deterministic; direct ingestion routes block interpretation through the LLM breakdown.
Breakdown is *already* non-deterministic, so the marginal loss is small and the signal gain (explicit
decisions/options) real — and it applies only to the **interactive** path (the headless path keeps the
deterministic flatten). **Mitigations:** the resolved `answer` is a deterministic datum the LLM reads
(not re-derived); `charter-format` fixes the catalog; and the **comparative spike (Issue G / #25) gated
adoption and PASSED** — we did not flip invariant 5 on faith that direct breakdown is as good, we measured
it against the flattened baseline first (equivalent-or-better).

### Loose Charter↔Guardrails coupling via a skill
The contract is "the session's `charter-format` agrees with Charter's renderer." **Mitigations, layered:**
(1) the **drift test** (§2.2) binds the skill catalog **and its format version** to the code — enforceable
Charter-side; (2) the **format-version range check** (§2.4) turns a too-old skill into a legible "update
charter-format", not a misparse; (3) single-sourcing (no Guardrails-side vendor) means there is one
catalog to keep honest, not two.

### Flatten faithfulness (DA blocker 1 — fixed; now a permanent property under Q2)
The flatten must project a resolved `.charter.md` faithfully — **permanently**, since it is the
headless/autonomous path's input (Q2), not just a temporary bridge. `HandoffMarkdown.EmitQuestion` today
reads only the external answers dict (`HandoffMarkdown.cs:261`) → would flatten resolved questions as
**all-open**. The fix (inline-`answer` fallback, landed in slice 1) makes the projection faithful; without
it the headless path silently loses every human decision.

### Concurrency of live file mutation
The only unsound reading (server-writes-plan) is designed out (§1.4). The server writes only its **own
sidecar** (durability), never `.charter.md`. Residual: a large **non-atomic** agent write briefly racing
the server read → transient parse-degrade → self-heals. Bounded, visible, never data loss. `charter
resolve` / `poll --apply` are discrete single-writer invocations, atomic in the plan dir.

### Stranded answers (DA weak 4 — fixed)
A plain `poll` that drained answers destructively could strand them (removed from the store, never in the
file). Fixed: **`--apply` is the only answer-drain path**; plain `poll` reports-but-does-not-remove; the
sidecar is a second durable copy.

### Open-`:::question` in an unattended run (Q1 — dial-governed, not a hard halt)
`plan-breakdown` never silently defaults an open question. Interactive breakdown asks (`AskUserQuestion`);
otherwise it emits a task that writes `{"needsHuman": …}`. At **run time**, an autonomous run does **not**
unconditionally halt on that `needsHuman`: it is the agent-emitted needs-human the approved autonomous
classifier governs as a **dial-eligible judgment call** (`12-autonomous-mode.md` §4.1). Below the dial →
**proceed with a recorded best-guess** (forensic trail in `decisions[]` + `autonomy.jsonl`); at/above the
dial → **escalate** (honest halt + async firstmate answer file, exit **`EscalationsPending = 4`**, not
`2`). No new gate type — the classifier already covers agent-needs-human. This is Guardrails' existing
greenfield idiom plus its shipped autonomy dial, not new machinery.

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

Prove the risky bet first. The riskiest assumption was **"direct breakdown of a `:::`-bearing `.charter.md`
is at least as good as the flattened `handoff.md`."** It was proven by a **comparative spike that gated
the invariant-5 flip and the epic filing — spike #25 PASSED — GO (2026-07-23).**

**Prerequisite / gating slice (was before adoption — now cleared):**
1. **`QuestionSpec.answer`** + **`QuestionResolution.Apply`** (surgical `JsonObject`) + the inline-answer
   fallback in `EmitQuestion` (now a permanent flatten-faithfulness property — Q2). Deterministic,
   unit-testable.
2. **Frontmatter support** (Issue C) — must precede the marker.
3. **A prototype `charter-format` skill** + a fixture `.charter.md` (resolved + open `:::question`).
4. **Issue G — the comparative spike (#25): PASSED.** Broke down the `.charter.md` (direct) AND its
   flattened `handoff.md`; judged the DAGs equivalent-or-better. The gate is cleared → the invariant-5 flip
   and the epic filing are unblocked. (Had it FAILED it would have sent the design back cheaply, before any
   change.)

**On PASS — producer side:**
5. Flip invariant 5 (adopt Issue A); file the Guardrails epic. Land `charter-format` (final) + the drift
   test + unique-id lint (B), the format-version marker + range (E), custom-html promotion + strike (F),
   `charter skills install` (#18), the durability sidecar + `charter resolve` + `--apply`-only-drain (D),
   resolved-question rendering.

**Consumer side (interactive) — no clean break (Q2):**
6. Guardrails ships G3 → G1 → G2 for the **interactive** `/plan-breakdown`.
7. **No flatten removal.** `HandoffMarkdown` / `charter handoff` **stay permanently** as the
   headless/autonomous projection. The only handoff-adjacent deletion is the `answers.json` side-car
   (`HandoffAnswersFile` / `--answers-out`), which is superseded by the inline `answer` field and lands
   with slice 1 — it is **not** gated on Guardrails.

**Interaction with the pending queue and the held release:**

| Item | Interaction |
|---|---|
| **#16 `.charter.md`** | Prerequisite — land first. |
| **#18 `skills install`** | Prerequisite for ingestion — how `charter-format` reaches the breakdown session. Mirror `guardrails skills install`. |
| **#13 `poll`** | Keep the poll core; repurpose `--answers-out`→`--apply`; delete `HandoffAnswersFile`; make `--apply` the only answer-drain path. |
| **#17 `convert`** | Orthogonal but synergistic — plain `.md` → `.charter.md` is a living-doc producer that targets `charter-format`. After the core; not on the critical path. |
| **#19 discussion** | Resolved by adopting Issue A. |
| **Release** | **HELD for Architecture B.** No release until the living-document producer pipeline ships with durability. Do not ship a half-state (living-doc mode with no durability). **Q2 removes the old flatten-deletion hazard entirely** — the flatten stays permanently, so there is no "flatten deleted with no consuming Guardrails" failure mode. The interactive Guardrails consumption (G1–G3) can land after Charter's producer side without blocking a release, because the headless flatten path always works. The prior "cut now" option remains withdrawn. |

**Migration story (simplified — Q2):** there is **no deletion window**. `charter handoff` **stays
permanently** — faithful to inline answers (the `EmitQuestion` fallback shipped in slice 1) — as the
headless/autonomous projection. It is never removed; the "bridge retired once Guardrails consumes"
sequencing dissolves.

---

## The SSOT change (invariant 5)

Adopted (Issue G / spike **#25 PASSED**): replace invariant 5 in
`docs/plans/01-combine-lavish-and-visual-plan.md:92`:

**From:**
> 5. **Feeds Guardrails via plain markdown** — the handoff is canonical reviewed markdown, no MDX.

**To (the DUAL path — Q2 — with Q1's dial governance):**
> 5. **Feeds Guardrails via a dual path (no MDX in either).** In the **interactive** path, `/plan-breakdown`
>    consumes the `.charter.md` **directly** (`:::` blocks and all), guided by the single `charter-format`
>    skill the invoking session loads, within a declared format-version range. In the **headless/autonomous**
>    path, the breakdown consumes the **retained flattened `charter handoff` output** (plain markdown) — so
>    plain-markdown plans **and the autonomous pipeline break down unchanged**. The `.charter.md` is the
>    single container of review state (no `answers.json`); the flatten **stays permanently** as the
>    plain-markdown projection. A resolved `:::question` carries its `answer` inline; an open one is **asked
>    at breakdown** (`AskUserQuestion`) or **emits a `{"needsHuman": …}` task** — never a silent default —
>    and at **run time** that `needsHuman` is **dial-governed** in an autonomous run (Guardrails #361:
>    best-guess-with-forensic-trail below the dial, else escalate with exit **`EscalationsPending = 4`**),
>    not an unconditional halt. Compatibility is a **format-version range check** (`charter-format-version`
>    frontmatter marker vs. the skill's `[format-min, format-version]`), never Charter's parser/binary or a
>    Guardrails-binary pin; an absent marker is rejected and a too-old skill fails with a clear "update
>    charter-format" error.

The "Architecture", "Milestones (wave 5)", and "Open items" sections of `01-…md` that reference
`charter handoff` / the handoff fixture / "plain markdown" are updated **in the same change** to the
dual-path reality (the handoff references are **kept** — the flatten stays — not deleted). The
"Format & block catalog" reconciliation (`:::file-tree` / `:::annotated-code` strike, `:::custom-html`
promotion) is carried by **Issue F**, bound by the drift test, and is not folded into the invariant-5 flip.
This doc is the authority for those edits.

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
