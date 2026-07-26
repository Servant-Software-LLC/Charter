# Git-mediated team review — design of record

**Status:** proposed · **Rev 2** (spike-gated; adversarial pass applied 2026-07-26)
**Supersedes:** the "annotations are ephemeral" half of `02-architecture-b-living-document.md` §1.3
**Closes (recommended, owner sign-off):** #46 (durable consume), #3 (review rounds), #4 (hosted share)

> **Rev 2 changed four load-bearing things** after an adversarial review: the anchor ladder is replaced by
> **exact-hash-or-orphan** (§4.3), concurrent state settlement is **detected, not silently ordered** (§4.2),
> the server-less `poll` read path is promoted to a **first-class build item** (§5), and Charter is
> **permitted to read git state** (never write it) so the loop can tell a human what to do (§5.1).

---

## 1. The problem

Charter has **two kinds of human feedback with different lifecycles**, and only one travels:

| | `:::question` **answers** | **Annotations / comments** |
|---|---|---|
| Where they land | Inline **in the `.charter.md`** (`QuestionResolution.Apply`) | A **server-owned sidecar** in the per-user OS state dir |
| Travels via git? | **Yes** | **No** — never enters the repo |
| Lifetime | Permanent, part of the plan | Drained destructively to the local agent, then gone |
| Visible to a teammate? | Yes | **No — not ever** |

Three facts make the second column worse than merely "ephemeral":

1. `ReviewSidecar.PathForPlan` keys on `sha256(absolute plan path)` under `StateDirectory.Sidecars()` —
   **outside the repo**; a teammate cloning to a different path computes a different filename anyway.
2. `AnnotationStore.Drain()` is destructive — after `charter poll` the comment exists in **no store at
   all**, so there is nothing to commit even in principle.
3. `Annotation` has **no author field**.

The file already carries decisions and carries no discussion.

### The target use case (the owner's words)

> Team Member A has his AI agent write a `charter.md`, he checks it into GitHub, other team members pull
> the latest commit and in their AI agent they can pull up the rendered `charter.md` to see it and
> comment or make changes.

### This completes Architecture B

Arch B §1.3 kept annotations ephemeral "to keep the handed-off plan free of review chatter," conflating
two claims. *"Review chatter must not be in the plan file"* — still true, preserved below. *"Review
chatter may be ephemeral"* — wrong, and fixed here.

**Two committed files, one writer each**: the agent owns `plan.charter.md`; Charter owns the review log
beside it. That *strengthens* Arch B's single-writer property.

---

## 2. Decision: per-author-per-session append-only JSONL logs

```
docs/plans/tenant-rate-limit.charter.md            <- the plan (agent writes)
docs/plans/tenant-rate-limit.charter.review/       <- the review log (Charter writes)
    alice-example-com.20260726T104500Z-a1b2.jsonl
    bob-example-com.20260726T111203Z-7f3e.jsonl
```

- **One file per (author, review session)** — a session is one `charter review` invocation.
- **Append-only.** Records are immutable; edit/resolve/reopen/retract/reply are *new records*; state is a
  deterministic **fold**.
- **Committed by default** (owner decision) — with a documented opt-out (§7).
- **One JSON object per line. Never pretty-printed.**

### Why this shape

Two reviewers, or one reviewer in two sittings, never write the same file — so the **common** case needs
no merge driver at all. Verified: three teammates, parallel branches, squash-merged PRs, **no
`.gitattributes` present** → 0 conflicts, all records survive, and a resolve authored on one branch
correctly closed a comment authored on another.

**Honest limit — this is not "conflict-free by construction" in every case.** Two reproduced exceptions:

- **Squash-merge then continue the same session.** Alice's PR is squash-merged while her `charter review`
  is still open; she appends and syncs → **`CONFLICT (add/add)`**, clean *with* `merge=union`.
- **Cherry-pick / delete-one-side** → **`CONFLICT (modify/delete)`**, which **union cannot resolve at
  all**.

So `merge=union` is still worth committing, and the surgery cases still need a human. What per-session
buys is that the *ordinary* parallel-review path never conflicts.

### Why not a single shared log

**GitHub does not honour `merge=union` server-side.** Sourcing, stated honestly: this rests on
**documented GitHub behaviour** — [community discussion #9288](https://github.com/orgs/community/discussions/9288),
open since Dec 2021 and unresolved, including a GitHub staff statement that user-supplied
`.gitattributes` merge configuration is not used, with "merge locally" as the only workaround — **not on a
controlled experiment we can re-run.** Anyone revisiting this decision should re-verify it directly.

The failure is loud (merge button disabled, REST 405), not silent — no corruption risk. But with a single
shared log, *every parallel review PR would show as conflicting*, in the exact workflow this design
serves.

| | single JSONL | one file per comment | per-author | **per-author-per-session** |
|---|---|---|---|---|
| Ordinary parallel review | needs `merge=union` | conflict-free | needs union (same author) | **conflict-free** |
| Works on GitHub PRs | **no** | yes | mostly | **yes** |
| Files for a 40-comment review | 1 | **83** | ~3 | **2 per round** |
| PR diff readability | good | **poor** | good | **best** |

**Rejected — single JSON array.** Conflicts on every parallel comment.

**Rejected — one file per comment.** A resolve/edit either mutates the file (reintroducing
`CONFLICT (content)`, and `modify/delete` on retraction) or becomes another file — i.e. one file per
*record*: 83 single-line files for a 40-comment review.

**Rejected — inline `:::comment` blocks.** Commenting would **mutate the plan file**, so every reviewer
needs write access and races the author's edits; same-block comments conflict by construction; and it
would put review ephemera into the artifact Guardrails ingests.

---

## 3. What the spike proved

Transcripts: `scratchpad/merge-spike/`; adversarial re-runs: `scratchpad/da-spike/`.
Kill criterion: *silent corruption or silent record loss is fatal; a visible conflict is acceptable.*

**One record per line is mandatory.** Pretty-printed JSON under union merge fuses records: on a
single-object `.json` this is **silent** (records merge into one object with duplicate keys; parsers take
last-wins; exit 0, valid JSON, no error). On `.jsonl` it is loud (invalid lines). Charter is the sole
writer, so the reachable path is a third party (`jq .` in a script, a format-on-save hook) — the rule is
free, so take it.

**The trailing-newline hazard, correctly stated.** Union merge itself never concatenated records across
all eight newline combinations. The real chain: a merge can leave the file *without* a trailing newline
(inheriting the sloppy side's state); the **next** append then fuses two records. The rule is **"read the
last byte before appending; if it is not `\n`, write one first"** — not "always write a trailing
newline," which a reviewer did and still corrupted.

**File order is merge-order-dependent.** The same branches merged in two orders produce different byte
order. Two teammates with identical commits legitimately hold different bytes.

**Duplication is reachable** when an identical record's *position* differs relative to each side's other
records.

**Degradation without `.gitattributes` is safe** — loud `CONFLICT (content)`, all stages retained, markers
are invalid JSON so a strict fold fails loudly, and `git merge-file --union` recovers mechanically. Union
merge is an **optimization, never a correctness dependency**.

### Silent-loss paths that remain

1. **A human resolving a conflict with "take mine."** Valid JSON, no trace in the file.
2. **`git revert -m 1` of a review merge** deletes the log from HEAD; `git log --full-history` still shows
   the commits; and **re-merging the branch reports "Already up to date" — the records do not come back.**
   Recovery is `git checkout <sha> -- <path>`, not a re-merge.

Both are detectable by `charter review verify` (§5), which compares every record ever committed against
HEAD.

### Hard rules

**Writer:** one JSON object per line, never pretty-printed · read the last byte before appending and
supply `\n` if missing · records immutable · **globally-unique random ids** (a counter lets two offline
teammates mint the same id) · no raw control bytes (a NUL makes git treat the file as binary and bypass
the driver).

**Fold:**
1. **Dedupe by `id`, first wins.**
2. **Two-pass and order-independent.** Never depend on file order.
3. **Never sort by timestamp for causality.** Clocks are unsynchronized. Timestamps are for presentation.
4. **Retain and report orphans** — a reply/resolve whose target is absent means an unmerged branch, not
   corruption.
5. **Malformed line = loud, non-fatal** — report with line number, preserve, keep folding.
6. **Unknown `op` = forward-compatible** — retain, ignore for state, report a count.
7. **Unknown `v` on a *known* op = retain, report, DO NOT APPLY.** Without this, a v1 fold silently
   applies a v2 `resolve` under v1 semantics and two teammates hold different state with no warning.
8. **`v` is per-record**, not per-file.

### `.gitattributes`

```gitattributes
**/*.charter.review/*.jsonl   merge=union text eol=lf
```

Both are built into git — nothing to install. `text eol=lf` is not cosmetic: without it a CRLF-writing
teammate forks every line and union keeps both copies (reproduced). The `**/` prefix is **required** —
verified that `review/*.jsonl` matches only at the repo root.

---

## 4. Records

```json
{"v":1,"id":"cmt_9f3a1c22","op":"create","ts":"2026-07-26T10:45:12Z",
 "author":{"name":"Alice Ng","email":"alice@example.com"},"actor":"human",
 "anchor":{"blockId":"b92bb0c5fe0d7b8448379","kind":"element",
           "quote":"the read path will be built after","base":"sha256:1f4c…"},
 "body":"Is Postgres right here, given the latency budget?"}
```

**Ops:** `create`, `reply`, `edit`, `resolve`, `reopen`, `retract`.

**Identity** comes free from `git config user.name` / `user.email` — no account server, which is exactly
what neither prior art can do (Builder.io's local bridge hardcodes `local@agent-native.local`; Lavish has
no identity at all). Note the slug is *not* identity — two different emails can slug alike; the record
carries the true email, and the timestamp+random filename suffix keeps files distinct.

**`actor: "human" | "agent"`** — the owner's decision that the agent gets a voice. Today the agent can
only respond by silently editing the file, so every disagreement escapes into terminal chat.

### 4.2 State settlement: detect concurrency, never silently order it

Last-writer-wins by timestamp is **rejected**. It imposes a total order on genuinely concurrent events and
never reports that it did — and clock skew is not hypothetical when offline review is a supported mode.
*Failure it would cause:* Alice, offline with a 20-minute-slow clock, appends `reopen` at real 14:00Z
stamped 13:40Z; Bob resolved at 13:50Z; the fold discards **Alice's explicit reopen with no trace**, and
she later sees "resolved" and assumes a colleague did it deliberately.

Instead:

- Every state record (`edit`/`resolve`/`reopen`/`retract`) carries **`prev`** — the id of the latest state
  record its author had observed for that target (or `null`).
- If the latest `resolve` and the latest `reopen` are **concurrent** — neither observed the other — the
  fold yields **`contested`** and the panel shows both, with authors. A contested comment is **not**
  resolved, and `charter handoff`/execution treat it as open.
- Only a chain that *observed* its predecessor settles state.

This matches the design's ethos everywhere else: rule 5 makes a malformed line loud, and §4.3 refuses to
guess an anchor. State settlement is the highest-stakes case — it decides whether a blocking objection is
open when the plan feeds execution.

**Authorship rules (previously unspecified):**
- `retract` is valid **only from the comment's own author**. A retract by anyone else is retained,
  reported, and **not applied** — otherwise a teammate can silently delete a blocking objection.
- `resolve`/`reopen` are open to anyone (review is collaborative), and always attributed in the panel.
- **`retract` of a comment with replies** hides the body but **keeps the thread**, rendered as
  *"(comment withdrawn by author)"* with the replies intact. Replies are other people's words and are
  never removed by someone else's retract.

### 4.3 Anchors: exact-hash-or-orphan (the ladder is rejected)

Rev 1 specified a resolution ladder (`blockId → quote+context → neighbour fingerprint → Unresolved`). It
is **withdrawn**, because it re-introduces the exact misattribution class `BlockModel.cs` was just
rewritten to eliminate (#50).

*Demonstrated:* a plan with three identical paragraphs; the agent edits **only the heading above the
second one** — the most common edit in a review round. That block's id changes. Rank 1 fails; the quote is
**byte-identical across all three blocks**, so rank 2 is degenerate; the neighbour *is* what changed, so
rank 3 is degenerate too. A ladder with no ambiguity rule picks one and Bob's objection about the *write*
path silently re-attaches to the *read* path — and the agent "fixes" the wrong block.

**Therefore:** an anchor resolves by **exact `blockId` match, or it is `Orphaned`.** No fuzzy re-binding.

This costs less than it appears: ids are per-block and content-derived, so editing block X never orphans a
comment on block Y. Orphaning is confined to (a) the commented block itself changing — semantically
correct — and (b) a *duplicate* block whose neighbourhood changed, which is collateral from the #50 fix.

**An orphan is never blind.** Each record carries `base` (the plan's content hash when written) and the
`quote`, so the panel always shows *"you commented on «…»; the plan has changed since"* — with a diff
against `base` where available. That delivers the practical value of the ladder with none of the
rebinding risk, and it is a smaller build.

*(If real dogfooding shows orphan rates are painful, a ladder may be revisited — but only with a normative
ambiguity rule: **any rank matching more than one candidate yields `Orphaned`, never a guess.**)*

**"Orphaned" ≠ "addressed."** Rev 1 framed an orphan as the completion signal. That is false in reachable
cases: folding a `:::question` answer rewrites that block, changing its id — so every comment on that
question orphans **though nobody addressed anything**. The panel must render `Orphaned` as a neutral fact.
Claiming "Addressed" requires positive evidence that the *commented* block's content changed.

---

## 5. Surface changes

**Charter never mutates git.** It does not commit, push, stage, or rewrite history. It **may read** git
state (§5.1).

- **`charter review`** — opens/creates this session's log; loads **all** logs in the plan's `.review/` dir
  and folds them, so the panel shows teammates' comments. Watches the `.review/` directory as well as the
  plan file, so a `git pull` landing a teammate's log mid-session refreshes the panel instead of silently
  showing the startup fold.
- **`charter poll` — a NEW server-less read path (build item, not a footnote).** `poll` today is
  architecturally a *client of a running loopback server*: it resolves a session from the registry, probes
  it, and exits 3 when none is live. So without this, the payoff step — **A's agent reading B's committed
  comments** — requires A to be running `charter review`, which A is not: A is executing. `charter resolve`
  already has exactly the needed fallback (reads the sidecar directly when no server is live); `poll` needs
  the analogous path: read `.review/*.jsonl` → fold → envelope. The wire contract is unchanged; the read
  path is new.
  **`consumedAt` stays machine-local** in the existing sidecar and is deliberately **not** a log record —
  N agents on N machines, and A's agent consuming must not mark a comment handled for B.
- **`charter resolve`** — appends a `resolve` record instead of mutating a queue.
- **`charter review verify`** *(new)* — audits that no record ever committed is missing from HEAD,
  catching both silent-loss paths (§3), and warns on a stranded `.review/` directory (§8.4).

### 5.1 Read-only git awareness (new, and load-bearing)

"Charter never runs git" conflated *must not mutate* (a sound invariant) with *must not read* (not implied,
and the only thing standing between a reviewer and two silent failures):

- **Stale-plan review.** B opens `charter review` without pulling; A rewrote the plan yesterday; B writes
  twelve comments against blocks that no longer exist. Every one orphans. Forty minutes wasted, and **no
  warning was possible.**
- **Uncommitted comments.** B comments, closes the tab, and the records sit uncommitted in a directory B
  has never heard of. The server has exited, so it cannot even remind him. **The most likely outcome of a
  first team review is comments that never leave the reviewer's machine, with the tool reporting success.**

So Charter **reads** `HEAD`, the upstream ref, and the plan blob's sha, and:
- warns at `charter review` start when the plan differs from its upstream (*"your copy is N commits
  behind — pull before reviewing"*);
- at session end (and in the panel) reports uncommitted records and **prints the exact commands to run**.

It still never runs them. This is the difference between a loop that closes and one that depends on human
memory.

---

## 6. Deliberately unaffected

- **`plan.charter.md` is untouched by commenting.** No `:::comment` blocks; the block catalog does not
  change; `charter-format` does not bump (audited: the drift test binds `BlockKind` ∪ `QuestionSpec` ∪ the
  skill catalog table — a JSONL record schema touches none).
- **`charter handoff` and Guardrails ingestion are unaffected** (audited: `handoff`/`export`/`convert` all
  take a required `--out`, never glob, never derive a path from the plan name; no code enumerates a plan's
  parent directory).
- **`:::question` answers keep going inline into the plan.** *Decisions belong to the plan; discussion
  belongs to the log.* That split is the design in one line.

---

## 7. Permanence, privacy, and the opt-out

**Committed comments are permanent.** `retract` hides a comment from the fold; **the text remains in git
history, in every clone, and in every fork, forever.** Candid criticism of a colleague's plan becomes a
durable, potentially public repo artifact. Records also embed `author.email` in **file content** —
greppable and harvestable in a public repo, and `git config user.email` is often a personal address.

Therefore:
- **Committed by default** (owner decision) — it is the transport; without it the feature does not exist.
- **A documented opt-out is required**, not optional: a team that wants local-only review adds
  `*.charter.review/` to `.gitignore` and Charter keeps working exactly as it does today (single-reviewer,
  local). The README and skill must state this plainly alongside the permanence warning.
- `charter review` states, once, where records are written and that they are intended to be committed.

---

## 8. Risks and open questions

1. **"Why not just use GitHub PR comments?"** — the strongest objection; answer it honestly in the README.
   PR review wins on notifications, threads, resolve, approvals, required reviewers, mobile, email, and
   **suggested changes** (one-click apply — Charter has no equivalent), with zero new machinery. It does
   **not** win on offline/credentials — `gh pr view --json comments` needs a credential every dev already
   has, and this design needs the network too (`git pull`/`push` *is* its transport). *(An earlier draft
   claimed that advantage; it was untrue and is struck.)* What genuinely survives: commenting on the
   **rendered** artifact (a diagram node, a comparison row, a question form), anchors that survive
   re-render, and decisions that flow into execution. **Honest framing: complementary, not competitive —
   if most of your comments are "this section is wrong," PR review is cheaper and you should use it.**
2. **Retention.** "2 files per review" is per *round*, not per plan lifetime: 3 reviewers × 2 sittings/day
   × 2 weeks ≈ 60 files that are never deleted and outlive the plan. Deferring GC is cheap for a local
   cache and **not** cheap for a committed artifact. Per-author (not per-session) cuts this ~10× and —
   given §2's honest limit that per-session *also* needs union in the surgery cases — the gap between the
   two options is narrower than the table suggests. **Re-test that row before implementation.**
3. **Two reviewers, contradictory notes on the same anchor.** No merge problem; the agent needs skill
   guidance on surfacing disagreement rather than silently picking one.
4. **Plan rename strands the review history.** The `.review/` dir is name-derived and nothing follows it —
   `git mv 03-foo.charter.md 04-foo.charter.md` in a repo that numbers its plans leaves the whole
   directory orphaned, undetected. `charter review verify` must check for it.
5. **Migration.** Existing machine-local sidecar annotations: import once into the author's first session
   log, behind a flag.
6. **N=1 dogfooding.** Exactly one person has ever used Charter. The riskiest assumption in this document
   is that a second reviewer behaves like the first.
7. **Sequencing dependency (not a design flaw, but blocking).** This feature's minimum viable audience is
   **two humans**, and macOS code signing (#9) currently prevents a Mac teammate from installing Charter
   at all. Shipping team review before signing means shipping a team feature that cannot acquire its
   second user. Similarly, §8.1's strongest claim over PR comments — "decisions that flow into execution" —
   rests on `guardrails run` executing a Charter-derived DAG to green, which has not happened yet.

---

## 9. Build order

1. **Record + fold** in `Charter.Core` — schema, the eight fold rules, `prev`/concurrency detection,
   exact-hash-or-orphan. Pure and unit-testable; property tests for order-independence, dedup, and
   contested-state detection.
2. **Writer** in `Charter.Server` — session file naming, the pre-append last-byte check, atomic append.
3. **`charter review` reads all logs** + panel shows author, actor, resolved/contested/orphaned state.
   **Constraint:** logs are read server-side into the render payload — **do not add a static-file branch**
   at the path-confinement check. The served root is the plan's *directory*, so `.review/` already passes
   confinement; a static-file branch here would turn every sibling file under `docs/plans/` into a
   key-gated HTTP-readable resource in one line.
4. **Server-less `poll` read path** (§5) — the step that actually closes the loop.
5. **Read-only git awareness** (§5.1) + `charter review verify`.
6. **Agent voice** — `reply` from the agent; skill guidance on when to reply vs. edit.
7. **Browser test** — two logs from two authors fold into one panel; resolve round-trips; contested renders.
8. **Docs** — README (permanence warning, opt-out, and the honest PR-comments comparison), `charter` skill,
   domain knowledge.
