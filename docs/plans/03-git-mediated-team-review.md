# Git-mediated team review — design of record

**Status:** proposed · **Rev 2** (spike-gated; adversarial pass applied 2026-07-26)
· **Rev 2.1** adds §4.3.1 (review-log staleness — resolves #74) and amends §4.3, §5 and §9 accordingly, 2026-07-28
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

## 2. Decision: per-author append-only JSONL logs

```
docs/plans/tenant-rate-limit.charter.md            <- the plan (agent writes)
docs/plans/tenant-rate-limit.charter.review/       <- the review log (Charter writes)
    alice-example-com.4f2a91c8.jsonl
    bob-example-com.1d70b3e5.jsonl
```

- **One file per author**, for the life of the plan. Filename is
  `<slug(lowercased email)>.<first 8 hex of sha256(lowercased email)>.jsonl`.
  **Lowercase before hashing** — the fold compares author identity case-insensitively (so a capitalisation
  change in `git config` cannot cost someone the right to withdraw their own comment), and filename
  identity must agree with fold identity or one person ends up with two files.
- **Append-only.** Records are immutable; edit/resolve/reopen/retract/reply are *new records*; state is a
  deterministic **fold**.
- **Committed by default** (owner decision) — with a documented opt-out (§7).
- **One JSON object per line. Never pretty-printed.**

**The email hash in the filename is load-bearing, not decoration.** A slug alone is not identity:
`alice@ng.example.com` and `alice.ng@example.com` both slug to `alice-ng-example-com`. Under a per-session
scheme a random suffix hid that; with one file per author *forever*, two people would otherwise **share a
file** — interleaving their records and conflicting on every parallel review. The hash makes the filename
a function of the true identity while keeping the slug human-readable.

### Why this shape

**Different reviewers never write the same file**, so the ordinary parallel-review case — the one this
design exists for — needs no merge driver at all. Verified: three teammates, parallel branches,
squash-merged PRs, **no `.gitattributes` present** → 0 conflicts, all records survive, and a resolve
authored on one branch correctly closed a comment authored on another.

**Honest limit — "conflict-free by construction" applies to *distinct authors*, not to everything.**
Reproduced exceptions, all involving one author's own file:

- **Same author, two branches or two machines** (laptop and desktop, or two open PRs) → the same file
  diverges → resolved cleanly *by* `merge=union`, but **shown as conflicting on GitHub**, which does not
  honour the driver (below). This is the cost the per-author choice accepts, in exchange for ~10× fewer
  files and a PR diff that reads as "what Alice said."
- **Cherry-pick / delete-one-side** → **`CONFLICT (modify/delete)`**, which **union cannot resolve at
  all**, under any file-granularity scheme.

So `merge=union` is still worth committing, and surgery cases still need a human.

*(Chosen over per-author-per-session on 2026-07-26. Per-session removes the same-author case too, but at
~60 permanently-committed files for a two-week three-reviewer review — and since it still needs union for
squash-then-continue and cannot help modify/delete either, the gap between the two was narrower than it
first appeared.)*

### Why not a single shared log

**GitHub does not honour `merge=union` server-side.** Sourcing, stated honestly: this rests on
**documented GitHub behaviour** — [community discussion #9288](https://github.com/orgs/community/discussions/9288),
open since Dec 2021 and unresolved, including a GitHub staff statement that user-supplied
`.gitattributes` merge configuration is not used, with "merge locally" as the only workaround — **not on a
controlled experiment we can re-run.** Anyone revisiting this decision should re-verify it directly.

The failure is loud (merge button disabled, REST 405), not silent — no corruption risk. But with a single
shared log, *every parallel review PR would show as conflicting*, in the exact workflow this design
serves.

| | single JSONL | one file per comment | **per-author** | per-author-per-session |
|---|---|---|---|---|
| Ordinary parallel review (distinct authors) | needs `merge=union` | conflict-free | **conflict-free** | conflict-free |
| Same author, two branches | needs union | conflict-free | **needs union** | conflict-free |
| Works on GitHub PRs | **no** | yes | **yes**, except same-author-two-branches | yes |
| Files, 3 reviewers × 2 weeks | 1 | **~150** | **3** | ~60 |
| PR diff readability | good | **poor** | **best** — "what Alice said" | good |

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
1. **Dedupe by `id`.** For the reachable case the spike found — the *same* record appearing twice — any
   tie-break is equivalent. When two **different** records claim one id (tampering, or a broken writer),
   "first wins" would be order-dependent and would violate rule 2, so **rule 2 wins**: take the
   ordinally-smallest canonical JSON and emit a `ConflictingDuplicate` diagnostic. Order-independence is
   absolute; it is what makes two teammates with identical commits compute identical state.
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

### 4.2.1 Ratified fold semantics (settled during step 1)

Cases §4.2 left open, resolved consistently with its principle — *detect and report, never silently guess*:

- **A branch that never touched resolution abstains.** Only records that make a claim about open/closed
  vote. Without this, any teammate's unrelated `edit` would contest an existing resolve and **`Contested`
  would become the normal state**, destroying the signal.
- **Concurrent `edit`s** follow the same rule as resolve/reopen: the last body every branch agreed on
  stands (nearest common ancestor), plus a `ConcurrentEdit` diagnostic. Never an arbitrary winner.
- **`retract` is monotone and permanent** — there is no `unretract`, consistent with §7's permanence.
  It is a separate dimension from resolution: `Status` computes `Retracted > Contested > Resolved/Open`,
  so a retraction never erases a visible disagreement, and the body is withheld by default.
- **`resolve`/`reopen` aimed at a *reply*** is retained, reported (`UnsupportedTarget`), and **not
  applied** — inferring the thread root is exactly the guessing §4.3 forbids. Thread-level resolve is a
  future design call, not a fold behaviour.
- **A missing or non-integer `v`** is a malformed line (rule 5), not an unknown version (rule 7). Both
  are loud, retained, and not applied; only the diagnostic kind differs.

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
`quote`, so the panel can show *"you commented on «…»; the plan has changed since"* — with a diff
against `base` where available. That delivers the practical value of the ladder with none of the
rebinding risk, and it is a smaller build.

*Amended by §4.3.1 (2026-07-28) in two places.* The "the plan has changed since" half is a claim about the
whole document, and the panel now makes it **only when `baseStatus` backs it**; it used to be asserted on every
orphan, including ones where the plan is byte-identical to what the reviewer saw. And "never blind" **overstates
the guarantee for a whole-block (`element`) comment**, which carries no `quote` at all — for those an orphan is
its note, its author, its dead block id and its `base`, and nothing about the text it pointed at. That is an
honest limit of this design, not of §4.3.1; closing it means the submit path capturing a quote for element
anchors too.

*(If real dogfooding shows orphan rates are painful, a ladder may be revisited — but only with a normative
ambiguity rule: **any rank matching more than one candidate yields `Orphaned`, never a guess.**)*

**"Orphaned" ≠ "addressed."** Rev 1 framed an orphan as the completion signal. That is false in reachable
cases: folding a `:::question` answer rewrites that block, changing its id — so every comment on that
question orphans **though nobody addressed anything**. The panel must render `Orphaned` as a neutral fact.
Claiming "Addressed" requires positive evidence that the *commented* block's content changed.

### 4.3.1 Review-log staleness: the quarantine does not cross over (resolves #74)

**Ratified 2026-07-28, adversarial pass applied.** §4.3's *"an orphan is delivered as a neutral fact"* stands
unchanged and normative. This section says why the #67 defence stops at the sidecar, and what travels instead.

Charter #67 fixed the machine-local durability sidecar: a queue is quarantined when it holds ≥1 annotation, the
plan is **not** byte-identical to the revision the queue was written against, and **not one** anchor resolves.
`charter poll`'s server-less read (§5) folds the *committed* logs and is the sibling path. #74 asked whether the
same rule belongs there.

**It does not — and the reason is not deference to §4.3. The rule is unsound over this population.**

**1. The remedy does not exist here.** Quarantine means *copy the queue aside, rewrite the live file*. The
review log is git-tracked and Charter never mutates git (§5, invariant 8). There is nothing to move aside and
nothing to rewrite, so "quarantine" over the log could only mean **suppression at read time** — a filter over
the fold — which is what §4.3 forbids, and which would fork the contract between the two readers of one fold
(the panel and the `poll` envelope) that §5 built a single `ReviewLogStore` read path to keep in agreement.

**2. The evidence does not carry.** "Not one anchor resolves" is decisive for a machine-local, undelivered,
single-session queue written against a single revision. Over a **shared, permanent** log the same observation
has a high *benign* base rate:

- **A fresh clone, a second `git worktree`, a renamed checkout, a rebuilt devcontainer.** The consumption
  ledger is machine-local and path-keyed by design (§5), so the whole of a mature plan's review history is
  delivered in one poll — and nearly all of it is legitimately orphaned, because the plan has moved on.
  *All-orphaned is the expected state here.* This case alone is fatal to any corpus-level test — as a
  suppression rule **and equally as a warning** — because it fires loudest on the healthiest workflow.
- **A round the agent addressed.** Every commented block changed by construction, so every anchor orphans.
  That is the loop this design exists to serve.
- **A teammate's comment on a revision you never had** — the case §5.1's stale-plan warning exists for.

**3. Being wrong is unrecoverable in the direction that matters.** A quarantined sidecar is preserved on the
machine that owns it and restored by `charter review --keep-annotations`. A committed record a reader declines
to show has **no local remedy**: the record is fine, the *reader* is wrong, and the person who could vouch for it
is on another machine. §4.2 exists to keep a disagreement visible; a fold that quietly withholds a blocking
objection is that failure wearing a different hat.

**Therefore, normatively: every reader of the fold delivers every comment it holds.** Staleness never
suppresses, never reorders, never downgrades a record. That is the property that distinguishes the log from the
sidecar — the sidecar is *undelivered work in progress owned by one machine*; the log is *the durable, shared
record*.

> **Note on the one thing that does gate delivery.** The consumption ledger withholds records this machine has
> already been handed. That is delivery bookkeeping on a *fact*, not suppression on an *inference*, and it is
> what "1." above is really claiming: **evidence-based suppression is what this design refuses.** The ledger has
> its own honest cost — re-cloning into the *same* absolute path reuses it, so a genuinely fresh clone there is
> silently short — which is out of scope here and recorded in §8.8.

#### What travels instead: the evidence, and only claims that are earned

#74's question 3 asked whether the readers should distinguish *"orphaned because the plan moved on"* from
*"orphaned because this is a different document entirely."*

**They should not, because that distinction is not derivable from the fold, and `base` does not derive it.**
`base` is the plan's content hash when the comment was recorded. Equal to the plan's hash now, it proves *"this
comment was recorded against exactly this text"* — a sound positive. Different, it proves only *"not exactly
this text"*: an ordinary edit and a wholesale replacement produce the same observation. Inferring which from a
hash mismatch is §4.3's misattribution class in a different costume — a confident, wrong, invisible answer.

What *is* sound is the evidence itself, and it reached neither reader: `ReviewLogDrain` and `ReviewLogView` both
dropped `anchor.base` on the floor, so §4.3's promise was **asserted, not derived** — the panel printed *"the
plan has changed since this comment was written"* on every orphan, including ones where it had not.

**Contract — a projection change only. No record-schema change, no new `v`, no `charter-format` change; §6 is
untouched.** Every review-log-sourced comment carries, on the `charter poll` envelope and in the
`GET /api/review-log` projection:

| field | meaning |
|---|---|
| `base` | the record's `anchor.base` verbatim, or absent from the wire |
| `baseStatus` | `current` · `different` · `unknown` (always present on a review-log comment) |

- **`current`** — the plan is byte-identical (modulo line endings) to the text this comment was recorded
  against. **Sound**: byte-identical text at this path *is* this document, by every definition Charter has.
- **`different`** — it is not. **Evidence of nothing on its own**, and the modal state of nearly every comment
  in a living document. An agent that reads it as "ignore this comment" is misreading it.
- **`unknown`** — the record does not say, **or there is no plan text to compare**. A `null` *or empty* plan
  answers `unknown` for every comment: the reachable ways to hold an empty plan are a read that raced the
  drafting agent's truncate-then-write, an interrupted save, and a caller that answers an unreadable file with
  `""`. Without this rule an entire review reads `different` at exactly the moment the plan is being rewritten —
  the unearned claim this section exists to remove, reproduced at scale.

**Read them as a pair with `anchorStatus`.** This is where the value actually is:

| pair | what it means |
|---|---|
| `resolved` + `current` | ordinary live feedback on the current text |
| `orphaned` + `different` | the ordinary living-document orphan |
| `resolved` + `different` | the block is unchanged, the document is not. Ordinary after any edit round — **and** #67's collision. **The two are not separable**, which is the honest limit of this contract |
| `orphaned` + `current` | **the one anomalous pair**: the block was never in the very text this comment was recorded against. Reachable when the reviewer commented on a render they had not reloaded (the SDK defers re-render while a composer is open), or when the anchor was never valid |

**`base` says "when recorded", not "as the reviewer saw it".** The hash is taken from a fresh read at submit
time, and the SDK deliberately defers re-render while a draft is open, so the reviewer can be looking at an
older render than the file that was hashed. Earlier prose in §4 and in the code said "as the reviewer saw it";
that was wrong, and it is the reason the `orphaned + current` pair exists at all. Making `current` mean
*"the text the reviewer's eyes were on"* would require the render's hash to travel with the submission — a
change to the submit contract, deliberately **not** made here, and the honest reason to keep the weaker claim.

**Line endings are not content — and the minting side is frozen.** `anchor.base` is minted over the plan's
**raw bytes**, and that is now permanent: records are immutable and committed forever, so changing the minting
normalization would make every record written to date read `different` for all time. All widening therefore
happens on the **comparison** side: the plan is hashed in each newline form, because `Block.StableId` normalizes
CRLF before hashing for the same reason and a checkout's `core.autocrlf` must not make a teammate's comment read
as written-against-different-text. Without it a Windows/Linux team reads `different` on every comment at the
identical revision, and the signal is dead exactly where team review lives.

**Error behaviour, stated as §4.2.1 states its own:**

- **False `different`** — reachable, and harmless by construction because the field labels rather than
  withholds: a **mixed-newline** file (a merge, or an LF tool and a CRLF editor touching one file) matches
  neither pure form; a trailing-newline or BOM change; any hashable difference that is not a content
  difference. `Block.StableId` trims, so no anchor moves and the comment is delivered untouched — only the
  claim is over-strong.
- **False `current`** — not reachable short of a SHA-256 collision.
- **The false negative that matters:** `different` is the label in **both** the benign "the plan moved on" case
  and the #67 "a different document is at this path" case, and nothing in the fold separates them. What it does
  buy is #67's *worst* finding — a comment whose content-derived anchor **collided** with a block in the
  replacement arrives today looking exactly like fresh feedback (`anchorStatus: "resolved"`, a plausible line).
  It now arrives naming the revision it was actually recorded against.

#### Where the signal is shown, and where it is not

- **The envelope carries `base` and `baseStatus` on every review-log annotation.** An agent has no visual
  context; per-record evidence is data it branches on, not decoration. `base` is also what lets a later step
  fetch that revision from git and diff it (§9 step 5).
- **The panel uses it only to make a claim it was already making *earned*.** An orphan with `different` keeps
  *"The plan has changed since this comment was written."*; any other orphan says only that the block it was
  written on is not in the plan. **No new per-comment badge** — `different` is the normal state of almost every
  comment in a living document, and badging it would train the reviewer to ignore the one badge that matters.
  That is §5.0's no-nagging principle applied to signal quality rather than to setup.
- **Delivery is at-least-once.** The read no longer records consumption; the caller confirms it *after* the
  envelope is written. Committing first made the drain at-**most**-once — a broken pipe or a killed process in
  that window lost a committed objection on that machine permanently, and every later poll reported a clean
  empty. "Deliver everything and let the reader judge" is only safe if a delivery that never arrived is not
  recorded as one.

#### The governing rule this settles

**An unsound signal may inform a question; it may never make a decision — and a question must itself be gated
on a fact, not on an inference.** The "is this review log even about this plan?" heuristic is real, useful, and
cannot be made sound. So:

- **`charter review verify`** (§5; §9 step 5, not yet built) gains an advisory: *no comment in this plan's
  `.review/` anchors to a block in the current plan*, beside §8.4's stranded-directory check. `verify` is
  human-invoked, so it cannot nag (§5.0).
- **No orphan-rate heuristic ships in the skill.** An earlier draft of this decision would have told the agent
  to ask the human when *every* delivered comment is orphaned and `different` — but that is the fresh-clone
  state and the after-an-edit-round state, i.e. the modal reading, so it fails the same test that killed the
  corpus rule in "2." above. **If a first-read notice ever ships it must be gated on the ledger fact** — *"this
  machine has never read this plan's review history before; N of M comments predate the current text"* — which
  is a statement, not an inference. Recorded with that default; not built here.
- The `charter` skill must instead teach the *pair* table above, and in particular that `different` is not a
  reason to discard. **SSOT: `skills/charter/references/review-loop.md` owns the annotation wire shape and must
  gain `base`/`baseStatus`** (skill change owned by charter-skill-author; not made in this change).

#### Alternatives considered and rejected

- **Transplant `ReviewSidecar.IsStale` to the log.** Rejected above: the remedy is unavailable and the evidence
  is unsound over a shared, permanent log.
- **Suppress in the `poll` drain only, not the panel.** Rejected: two readers of one fold disagreeing about what
  the review says is the drift §5's single read path exists to prevent.
- **A quote-presence flag** ("does the quoted text still occur anywhere in the plan?"). Rejected twice over: it
  is one rung of the ladder §4.3 withdrew and invites exactly the re-attachment that ladder was withdrawn for;
  and it does not discriminate — a rewritten block and a replaced document both delete the quote.
- **A document identity (a uuid in the `.charter.md`).** The only design that would actually *decide* #74's
  question 3 — and rejected. It expands the `charter-format` SSOT for a failure mode that already has a
  human-visible remedy; it is unreliable exactly where it matters (a human replacing a plan by hand does not
  mint a new id, and a human copying a plan as a template *keeps* the id and gets a confident false "same
  document"); and it is worth nothing to any plan or log that already exists. *Recorded with a default: revisit
  only if replacement-at-a-path proves common in real use, and then as a format RFC, never as a quiet field.*
- **Searching git history for a blob whose sha256 equals `base`.** It would prove positively that a comment
  belongs to this path's lineage — at O(history) subprocess calls, failing in a shallow clone, and failing in
  the *common* case that the commented revision was never committed. Cost without an answer.
- **`git log --diff-filter=D -- <plan>` ("was this path ever emptied and refilled?").** This one is genuinely
  sound and §5.1 already permits the read, so the claim "not derivable" must be stated precisely: **the fold
  cannot derive it; one bounded git call can answer a narrower question.** It is rejected *for the drain* and
  recorded *for `verify`, §9 step 5*, because it answers the wrong question in the reported case — #67's own
  repro overwrote the file in place, which git records as an ordinary content change with no delete at all. A
  signal that misses the bug that motivated it does not belong on the hot path.

---

## 5. Surface changes

**Charter never mutates git.** It does not commit, push, stage, or rewrite history. It **may read** git
state (§5.1).

- **`charter review`** — opens/appends to **this author's** log; loads **all** logs in the plan's `.review/` dir
  and folds them, so the panel shows teammates' comments. Watches the `.review/` directory as well as the
  plan file, so a `git pull` landing a teammate's log mid-session refreshes the panel instead of silently
  showing the startup fold.
- **`charter poll` — a NEW server-less read path (build item, not a footnote).** `poll` today is
  architecturally a *client of a running loopback server*: it resolves a session from the registry, probes
  it, and exits 3 when none is live. So without this, the payoff step — **A's agent reading B's committed
  comments** — requires A to be running `charter review`, which A is not: A is executing. `charter resolve`
  already has exactly the needed fallback (reads the sidecar directly when no server is live); `poll` needs
  the analogous path: read `.review/*.jsonl` → fold → envelope. The read path is new.
  **`consumedAt` stays machine-local** in the existing sidecar and is deliberately **not** a log record —
  N agents on N machines, and A's agent consuming must not mark a comment handled for B.
  *(Rev 2.1 — this bullet used to say "the wire contract is unchanged", and that is no longer true. §4.3.1 adds
  the additive `base` / `baseStatus` pair to review-log-sourced annotations; both are omitted entirely from a
  pending-queue annotation, exactly as `review` is, so the shipped session drain stays byte-for-byte what it
  was. Consumption is now recorded **after** the envelope is written, making the read at-least-once.)*
- **`charter resolve`** — appends a `resolve` record instead of mutating a queue.
- **`charter review verify`** *(new)* — audits that no record ever committed is missing from HEAD,
  catching both silent-loss paths (§3), warns on a stranded `.review/` directory (§8.4), and advises when
  **no comment in a plan's `.review/` anchors to a block in the current plan** (§4.3.1 — the one place that
  heuristic is allowed, because `verify` is human-invoked and therefore cannot nag).

### 5.0 Solo is the primary use case and must not regress

**One person using Charter alone remains the main use case.** Team review is additive; it must never make
solo use heavier. Concretely, binding on every slice:

- A solo reviewer who never intends to share **must not be nagged**. The §5.1 warnings fire only when the
  `.review/` directory is actually **tracked** — i.e. the user has opted into sharing. Untracked,
  gitignored, or not-a-repo ⇒ **silent**, and Charter behaves exactly as it does today.
- No new required setup: no git identity, no `.gitattributes`, no repo. Missing git identity falls back to
  a local identity and the review still works (§2).
- The solo path *gains* from the log and should be framed that way: comments stop vanishing on drain, so
  the reviewer keeps a durable record of what they asked for and what was addressed — the deeper form of
  the #42 complaint — and it survives restarts and their own second machine.

*(A solo user can still exercise the team path deliberately — two git identities in a scratch repo, two
branches, merge — which is exactly what the spike did.)*

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
2. **Retention — resolved by the per-author choice (2026-07-26).** File count is now bounded by *team
   size*, not by review activity: three reviewers means three files, whether the review lasts a day or a
   quarter. Growth is within-file, and a log of one person's comments on one plan stays small. Compaction
   is therefore deferred indefinitely rather than provisionally — and if it ever happens it is a rewrite,
   safe only on a quiesced branch.
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
8. **The consumption ledger is path-keyed, and re-cloning to the same path silently short-changes the new
   clone** (surfaced by the §4.3.1 adversarial pass). `ReviewLogLedger` keys on `sha256(full plan path)` under
   the per-user state dir, so "blow it away and re-clone into the same directory" reuses the old ledger and the
   fresh clone is never handed the history it has not seen. It is the mirror image of the fresh-clone case
   §4.3.1 leans on, and it is a real, unlabelled withholding. **Recorded, not fixed here** — the cheap options
   are keying the ledger on the plan's repo-relative path plus a repo identity, or invalidating it when the
   plan's inode/creation time moves backwards. Needs its own issue.
9. **`base` cannot mean "what the reviewer saw" without a submit-contract change** (§4.3.1). It is minted from a
   fresh read at submit time, while the SDK deliberately defers re-render whenever a composer is open — so the
   `orphaned + current` pair is reachable by design. Closing it means the rendered document's hash travelling
   with the submission. Recorded with a default: leave it, and keep the weaker, true claim.

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
5. **Read-only git awareness** (§5.1) + `charter review verify`. **This step also owns the orphan diff.**
   `anchor.base` is the *plan's* content hash, so rendering "you commented on «…», here's what changed"
   requires fetching that plan revision from git — the fold cannot supply it. Until this step lands, an
   orphan shows its `quote` but not a diff.
   *(Rev 2.1: `base` itself now ships on both readers ahead of this step — §4.3.1 — so what remains here is the
   git fetch and the diff render, not the plumbing. This step also owns the two advisories §4.3.1 assigns to
   `verify`: the all-orphaned `.review/` notice, and the optional `git log --diff-filter=D -- <plan>` check for
   a path that was emptied and refilled.)*
6. **Agent voice** — `reply` from the agent; skill guidance on when to reply vs. edit.
7. **Browser test** — two logs from two authors fold into one panel; resolve round-trips; contested renders.
8. **Docs** — README (permanence warning, opt-out, and the honest PR-comments comparison), `charter` skill,
   domain knowledge.
