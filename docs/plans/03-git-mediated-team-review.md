# Git-mediated team review — design of record

**Status:** proposed (spike-gated, spike PASSED 2026-07-26)
**Supersedes:** the "annotations are ephemeral" half of `02-architecture-b-living-document.md` §1.3
**Closes (recommended):** #46 (durable consume), #3 (review rounds), #4 (hosted share)

---

## 1. The problem

Charter today has **two kinds of human feedback with completely different lifecycles**, and only one of
them travels:

| | `:::question` **answers** | **Annotations / comments** |
|---|---|---|
| Where they land | Inline **in the `.charter.md`** (`QuestionResolution.Apply`) | A **server-owned sidecar** in the per-user OS state dir |
| Travels via git? | **Yes** — it is in the committed file | **No** — never enters the repo |
| Lifetime | Permanent, part of the plan | Drained destructively to the local agent, then gone |
| Visible to a teammate? | Yes | **No — not ever** |

Three facts make the second column strictly worse than "ephemeral":

1. `ReviewSidecar.PathForPlan` keys the sidecar on `sha256(absolute plan path)` under
   `StateDirectory.Sidecars()` — **outside the repo**, and a teammate who clones to a different path
   computes a different filename anyway.
2. `AnnotationStore.Drain()` is destructive, so after `charter poll` the comment exists in **no store at
   all** — there is no review record to commit even in principle.
3. `Annotation` has **no author field**. Nothing records who wrote a note.

So the file already carries decisions and carries no discussion.

### The target use case (the owner's words)

> Team Member A has his AI agent write a `charter.md`, he checks it into GitHub, other team members pull
> the latest commit and in their AI agent they can pull up the rendered `charter.md` to see it and
> comment or make changes.

Today: A commits, B pulls, **B can review and comment locally** — and A never sees a word of it.

### This completes Architecture B; it does not contradict it

Arch B §1.3 kept annotations ephemeral "to keep the handed-off plan free of review chatter." That
conflated two different claims:

- *"Review chatter must not be in the plan file"* — **still true, and preserved below.**
- *"Review chatter may be ephemeral"* — **wrong, and this document fixes it.**

The resolution is **two committed files, one writer each**: the agent owns `plan.charter.md`; Charter
owns the review log beside it. That *strengthens* Arch B's single-writer property rather than weakening
it.

---

## 2. Decision: per-author-per-session append-only JSONL logs

```
docs/plans/tenant-rate-limit.charter.md            <- the plan (agent writes)
docs/plans/tenant-rate-limit.charter.review/       <- the review log (Charter writes)
    alice-example-com.20260726T104500Z-a1b2.jsonl
    bob-example-com.20260726T111203Z-7f3e.jsonl
```

- **One file per (author, review session).** A session is one `charter review` invocation.
- **Append-only.** Records are immutable. Edit, resolve, reopen, retract and reply are *new records*;
  current state is a deterministic **fold**.
- **Committed by default** (owner decision). The log is a normal repo file. That is the entire mechanism
  by which comments reach a teammate.
- **One JSON object per line. Never pretty-printed.** This is a correctness requirement, not a style
  preference — see §3.

### Why per-author-per-session, and not the obvious alternatives

The spike (§3) ran real git across eight scenarios. One finding decided it:

> **GitHub does not honour `merge=union` server-side.** Verified by controlled experiment against two
> live public repos with union-marked files: GitHub reports `mergeable=false, mergeable_state=dirty`,
> while the identical merge locally *with* the repo's `.gitattributes` succeeds with zero conflicts.
> Corroborated by the absent `refs/pull/N/merge` ref and a decade of consistent GitHub Support replies.
> (Bitbucket Cloud: also unsupported. GitLab.com: unsupported; self-managed: supported.)

The failure is **loud, not silent** — GitHub deactivates the merge button, REST returns 405 — so there is
no corruption risk. But with a **single shared log**, *every parallel review PR would show as conflicting*
and require a local merge. That is fatal to the ergonomics of the exact workflow this design exists to
serve, and it pressures teams to relax branch protection.

Per-author-per-session removes the conflict class **by construction** — two reviewers, or one reviewer in
two sittings, never write the same file.

| | single JSONL | one file per comment | per-author | **per-author-per-session** |
|---|---|---|---|---|
| Conflict-free for distinct comments | needs `merge=union` | by construction | needs union (same author) | **by construction** |
| `.gitattributes` dependence | real | none | partial | **none** |
| Files for a 40-comment review | 1 | **83** | ~3 | **2** |
| PR diff readability | good | **poor** | good | **best** |
| Works on GitHub PRs | **no** (conflicts) | yes | mostly | **yes** |

**Rejected — a single JSON array.** Conflicts on every parallel comment, and worse: see the fatal
pretty-print case in §3.

**Rejected — one file per comment.** Conflict-free for *distinct* comments, but a resolve or edit either
mutates the file (reintroducing `CONFLICT (content)`, and `CONFLICT (modify/delete)` on retraction) or
becomes yet another file — i.e. one file per *record*: 40 comments + 25 replies + 18 resolves = **83
single-line files**, alphabetically scrambled, versus `1 file changed, 83 insertions(+)`.

**Rejected — inline `:::comment` blocks in the plan.** Two independent reasons: (a) commenting would
**mutate the plan file**, so every reviewer needs write access to it and races the author's concurrent
edits — breaking precisely where the team model matters most; (b) same-block comments conflict in git by
construction. It would also put review ephemera into the artifact Guardrails ingests, which §6 keeps
clean.

### Verified end-to-end

Three teammates, parallel branches, **squash-merged PRs, no `.gitattributes` present**: `0 conflicts`,
all records survive, and a resolve authored on one branch correctly closed a comment authored on another.

---

## 3. What the spike proved (and the hard rules it imposes)

Full transcripts: `scratchpad/merge-spike/`. Kill criterion was *silent corruption or silent record loss
is fatal; a visible conflict a human can resolve is acceptable.*

**FATAL, reproduced — pretty-printed JSON silently destroys records.** Two pretty-printed records, one per
branch, under `merge=union`:

```
$ git merge --no-edit bob    ->  Merge made by the 'ort' strategy.   exit: 0

{ "id": "cmt_al01", "body": "Alice: is Postgres right here?",
  "id": "cmt_bo01", "body": "Bob: what about the write path?",  "v": 1 }

VALID JSON. Parsed as: {"id":"cmt_bo01", ...}
>>> Alice's comment: GONE. Clean merge, exit 0, valid JSON, no error anywhere.
```

Union deduplicated the structural lines and fused two records into one object with duplicate keys; JSON
parsers take last-wins. **Hence: one record per line, always.**

**The trailing-newline hazard is real, but the folklore is wrong.** Union merge itself never concatenated
records across all eight newline combinations. The actual chain: a merge can leave the file *without* a
trailing newline (inheriting the sloppy side's state) even when your side wrote one; the **next** append
by a writer that assumes a trailing newline then fuses two records. So the rule is **not** "always write a
trailing newline" — it is **"read the last byte before appending; if it is not `\n`, write one first."**

**File order is merge-order-dependent.** The same three branches merged in two different orders produce
different byte order in the file. Two teammates with identical commits legitimately hold different bytes.

**Duplication is reachable.** Git usually dedupes an identical record added on both branches, but not when
its *position* differs relative to each side's other records — reproduced, one id appearing twice.

**Degradation without `.gitattributes` is safe.** A loud `CONFLICT (content)`; all three stages retained in
the index; the markers are invalid JSON so a strict fold fails loudly; and
`git merge-file --union -p ours base theirs` recovers everything mechanically. **Union merge is an
optimization, never a correctness dependency** — which is what makes it acceptable that GitHub ignores it.

**The one silent-loss path that remains** is a human (or GUI) resolving a conflict with "take mine".
Detectable: `git rev-list --full-history HEAD -- <path>` can prove no committed record vanished; `charter
review verify` will do this (§5).

### Hard rules this imposes

**Writer:**
1. One JSON object per line. **Never** pretty-print.
2. Before appending, read the last byte; if it is not `\n`, write one first.
3. Records are immutable — never rewrite a line.
4. Record ids are **globally unique random** (a sequential counter lets two offline teammates mint the
   same id — silent loss).
5. No raw control bytes (a NUL makes git treat the file as binary and bypass the merge driver).

**Fold:**
1. **Dedupe by `id`, first wins.**
2. **Two-pass and order-independent** — index by id, then apply ops. Never depend on file order.
3. **Never sort by timestamp for causality.** Clocks are unsynchronized; skew would sort a reply above its
   parent. Timestamps are for *presentation* and for last-writer-wins among competing `edit`s only.
4. **Retain and report orphans** — a reply/resolve whose target is absent means an unmerged branch, not
   corruption. Never drop it.
5. **Malformed line = loud, non-fatal** — report with line number, preserve, keep folding. This converts
   every remaining failure mode from silent to visible.
6. **Unknown `op` = forward-compatible** — retain the record, ignore it for state, report a count.
   Preserve unknown fields on round-trip.
7. **`v` is per-record, not per-file** — a log may legitimately contain mixed versions.

### `.gitattributes` (belt-and-braces, not load-bearing)

```gitattributes
**/*.charter.review/*.jsonl   merge=union text eol=lf
```

Both `merge=union` and `text eol=lf` are **built into git** — nothing for teammates or CI to install.
`text eol=lf` is not cosmetic: without it a CRLF-writing teammate forks every line and union keeps both
copies (reproduced). Note `review/*.jsonl` would only match at the repo root — the `**/` prefix is
required.

---

## 4. Records

```json
{"v":1,"id":"cmt_9f3a1c22","op":"create","ts":"2026-07-26T10:45:12Z",
 "author":{"name":"Alice Ng","email":"alice@example.com"},"actor":"human",
 "anchor":{"blockId":"b92bb0c5fe0d7b8448379","kind":"element",
           "quote":"the read path will be built after","base":"sha256:1f4c…"},
 "body":"Is Postgres right here, given the latency budget?"}
{"v":1,"id":"rec_4b7e","op":"reply","target":"cmt_9f3a1c22","ts":"…","actor":"agent",
 "author":{"name":"charter-agent","email":"agent@local"},
 "body":"Switched to Postgres and noted the tradeoff in the comparison block."}
{"v":1,"id":"rec_88d1","op":"resolve","target":"cmt_9f3a1c22","ts":"…","actor":"human","author":{…}}
```

**Ops:** `create`, `reply`, `edit` (new body for a target), `resolve`, `reopen`, `retract`.
`edit`/`resolve`/`reopen` are last-writer-wins among themselves by `(ts, id)` — a deliberate, documented
exception to rule 3, applying to *state settlement*, never to causality.

**Identity** comes free from `git config user.name` / `user.email` — no account server, which is exactly
what neither prior art can do (Builder.io's local bridge hardcodes `local@agent-native.local`; Lavish has
no identity at all).

**`actor: "human" | "agent"`** — the owner's decision that **the agent gets a voice**. Today the agent can
only respond to a comment by silently editing the file, so every disagreement or clarification escapes
into terminal chat and is lost to the team. An agent reply is a first-class record.

### Anchors: no assigned ids (decided)

Two independent analyses — one adversarial, one strategic — rejected carrying assigned anchor ids in the
`.charter.md`. The decisive reasons:

- **A comment must not mutate the plan file.** Pins would make every reviewer's comment a diff hunk racing
  the author's concurrent edits.
- **Repair asymmetry.** Back-filling a lost id requires quote/context matching anyway, so the resolution
  ladder below is required in *both* worlds. Assigned ids are an optimization on top of it, never a
  replacement.
- Builder.io's own anchor field is literally typed `anchor: unknown | null`, and **no code in that repo
  ever constructs, reads, or compares one**. Charter's content-derived hash + source-map is already more
  anchoring machinery than the system we would be copying.

**Resolution ladder**, applied at fold/render time:
`blockId` exact match → `quote` + surrounding context → neighbour fingerprint → **`Unresolved`**.

Each record carries `base` (the plan's content hash when the comment was written) and the `quote`, so **an
orphan is never blind**: the UI can always say *"this pointed at «…», and the plan has changed since."* An
orphan is normal in a living document — you commented, the agent fixed that block, its content-derived
anchor changed by construction. That is the completion signal, not debris.

---

## 5. Surface changes

**Charter writes the log; Charter never runs git.** Committing is the human's or the agent's act.

- `charter review` — opens/creates this session's log; loads **all** logs in the plan's `.review/` dir and
  folds them, so the panel shows teammates' comments, not just yours. Warns (does not auto-write) if the
  `.gitattributes` entry is missing.
- `charter poll` — unchanged wire contract. Reads *unconsumed* records for **this machine's** agent.
  **`consumedAt` stays machine-local** in the existing sidecar and is deliberately **not** a log record:
  N agents on N machines, and A's agent consuming a comment must not mark it handled for B.
- `charter resolve` — appends a `resolve` record instead of mutating a queue.
- `charter review verify` *(new)* — audits that no committed record is missing from `HEAD` (catches the
  "take mine" conflict resolution, the one silent-loss path left).

The **local sidecar survives**, with its role narrowed to exactly what is machine-local: which records
this machine's agent has already seen. The shared, durable truth is the committed log.

---

## 6. What is deliberately unaffected

- **`plan.charter.md` is untouched by commenting.** No `:::comment` blocks; the block catalog does not
  change; `charter-format` does not bump. Arch B's "handed-off plan free of review chatter" holds.
- **`charter handoff` and Guardrails ingestion are unaffected.** The review log is a sibling file neither
  reads. The direct-ingestion path verified against Guardrails `1.0.0-preview.48` keeps working unchanged.
- **`:::question` answers keep going inline into the plan.** Decisions belong to the plan; discussion
  belongs to the log. That split is the whole design in one line.

---

## 7. What this closes (recommended — owner sign-off)

- **#46 durable consume** — subsumed. Its blocker 1 (sidecar lifetime) dissolves: durable state is the
  committed log, and `consumedAt` stays machine-local by design. Its blocker 2 inverts: a dead anchor on a
  consumed comment is the **completion event**, surfaced as *"Addressed — here's before/after"*, not debris.
- **#3 review rounds + diff** — dissolved. **Git supplies rounds**: a round is a commit, the diff is
  `git diff`, and `base` on each record ties a comment to the version it was written against. No round
  subsystem.
- **#4 hosted share** — dissolved. **Git is the share**, and it contradicts loopback + zero-telemetry.
- **#5 layout audit** — unrelated; recommend closing as won-by-design (typed blocks make that bug class
  structurally impossible), but it is not part of this design.

---

## 8. Risks and open questions

1. **"Why not just use GitHub PR comments?"** — the strongest objection, and it must be answered
   honestly in the README rather than dodged. PR review wins on: notifications, threads, approvals,
   permissions, and zero new machinery. Charter's review wins on: commenting on the **rendered**
   deliverable (a diagram node, a comparison row, a question form) rather than on markdown source lines;
   comments anchored to *blocks* that survive re-render; and decisions that flow into execution. The
   honest position is **complementary, not competitive** — and if a team is happy reviewing the raw diff
   in a PR, they should.
2. **Log growth.** Compaction is a rewrite, so it is only safe on a quiesced branch. Defer until someone
   feels it.
3. **Two reviewers, same anchor, contradictory notes.** No merge problem (different files), but the
   agent must be told how to handle disagreement rather than silently picking one. Needs skill guidance.
4. **Migration.** Existing machine-local sidecar annotations: import into the author's first session log,
   or leave to age out. Recommend import, once, behind a flag.
5. **Untested attended path.** Guardrails' `AskUserQuestion` branch remains unexercised (their #411).
6. **N=1 dogfooding.** This design is for a team, and exactly one person has ever used Charter. The
   riskiest assumption in this document is that a second reviewer behaves like the first.

---

## 9. Build order

1. **Record + fold** in `Charter.Core` — schema, the seven fold rules, the resolution ladder. Pure and
   unit-testable; property tests for order-independence and dedup.
2. **Writer** in `Charter.Server` — session file naming, the pre-append newline check, atomic append.
3. **`charter review` reads all logs** + panel shows author, actor, and resolved state.
4. **Agent voice** — `reply` from `poll --apply` / a new verb; skill guidance for when to reply vs edit.
5. **`charter review verify`** + the `.gitattributes` warning.
6. **Browser test** — two logs from two authors fold into one panel; resolve round-trips.
7. **Docs** — README (including the honest PR-comments comparison), `charter` skill, domain knowledge.
