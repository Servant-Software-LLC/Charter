# The review loop — serve, annotate, drain feedback

This is the load-bearing part of driving Charter: the human reviews the plan in the browser and comments
**in place**, and you read that feedback back and revise. You drain that feedback with **`charter poll`** —
a CLI client over the loopback review server that discovers the running session (so the capability key
never crosses your command line), returns the queued annotations + `:::question` answers, and, with
`--apply`, writes the answers **inline** into the plan's `:::question` blocks. `charter resolve` is the
solo-human-reviewer companion that folds queued answers in when no agent is looping `poll`. The loopback
HTTP endpoints documented below are what `poll` wraps (and what its `--url` escape hatch targets directly).

`charter poll <plan>` also has a **server-less** read path: with no live session it folds the committed
review logs beside the plan, so you can read a teammate's comments while you are *executing*, not reviewing.
See [Reading a teammate's committed comments](#reading-a-teammates-committed-comments--source-and-review).

## Start the server

```
charter review plan.charter.md
```

This renders the plan, injects the annotation SDK **at serve time** (the saved artifact stays SDK-free —
invariant 1), and serves it over the **loopback** review server: bound to `127.0.0.1`, on an OS-chosen
ephemeral port, gated by a **per-session capability key**, and path-confined to the plan's directory
(invariant 4, *loopback + capability*). It opens the human's browser and prints one ready line to stdout:

```
Charter review server ready: http://127.0.0.1:<port>/?key=<key>
```

Two things to do with that line:

1. **Parse out `<port>` and `<key>`.** Every request you make to the server carries `?key=<key>`; a
   process that only guesses the port still can't read the plan or the feedback.
2. **Keep the process running.** `charter review` serves until it's stopped (Ctrl+C). Run it in the
   background so you can poll while it stays up. Pass `--no-open` when no browser should launch
   (headless or CI); the ready line still prints, so you can still drain.

The server **re-renders from the source file on every read request** — so when you edit `plan.charter.md`, the
human's next refresh shows your revision (live reload). You don't restart the server to publish a change.

## What the human does in the browser

The injected SDK lets the reviewer attach a note to three kinds of anchor:

- **element** — a whole rendered block (a callout, a table, a diagram, a code block).
- **text-range** — a selected span of text inside a block.
- **diagram-node** — a specific node inside a rendered `:::diagram`.

They also answer any **`:::question`** forms: choose an option (or type the answer), then click the
**Save answer** button in the form. That button stays greyed out until the answer differs from what the plan
already records — so on an open question it enables as soon as something is chosen, and on an already-answered
one it enables only once the reviewer *changes* the settled choice. In a free-text answer, Enter is a newline
and **Ctrl/⌘+Enter** saves. If you are telling a human what to do, tell them to click **Save answer** — the
form does not submit on its own. Each annotation is resolved server-side to the
**1-based markdown source line** of the block it points at (via the content-derived source-map), so the
feedback you drain tells you exactly which line to edit — that round-trip is the whole point (invariant 2).

Finally, the review panel carries a **Send to agent** button: the reviewer's way to say *"I'm done with this
round"* without leaving the page. It is disabled while there is nothing queued to send, and again once the
round has been handed off. It **signals only** — it queues no work of its own, applies nothing, and never
writes the plan. You remain the only writer of `plan.charter.md`.

## `charter poll` exit codes — branch on these, not on array lengths

On every outcome below except `1`, `poll` writes **exactly one** JSON envelope to stdout — including when it
found nothing and including when the `--apply` write failed — and reports the outcome as an exit code.
Script against the code:

| Code | Meaning | What you do |
|---|---|---|
| **0** | Something arrived — at least one annotation or answer, **or** a round hand-off with both queues empty. | Act on it. |
| **2** | Clean empty: a live session (or a readable review log) was found and nothing was queued. | Poll again / keep waiting. |
| **3** | No live session **and** no readable review log for this plan. Also returned when several sessions are live and you gave no selector (the candidates are listed on stderr). | Start `charter review`, or pass `<plan>` / `--url`. |
| **4** | A drain **could not complete** (transport, parse, or an unreadable review log). The queue state is **UNKNOWN**. | Retry. **Never** report "nothing queued". |
| **5** | `--apply` refused the inline write (duplicate `:::question` ids, a concurrent external edit, an I/O error). The answers are **preserved**, never committed. | Fix the plan, re-run `poll --apply` or `charter resolve`. |
| 1 | Generic verb error (an unexpected exception — e.g. a malformed `--url`). **No envelope is written.** | Fix the invocation. |

Source of truth: `src/Charter.Cli/ReviewExitCodes.cs` and `src/Charter.Cli/PollCommand.cs`. `charter resolve`
shares the same codes.

**Exit 4 is the one that will bite you.** A failed drain still emits an envelope with `"annotations": []`,
so code that only checks `annotations.length === 0` reads a transport failure as *"the human said nothing"*
and hands off a plan nobody approved. The envelope carries **`drainError`** — a human-readable string, `null`
on a clean drain — for exactly this. **`drainError !== null` means the queue state is unknown, not empty.**

```json
{ "annotations": [], "drained": { "annotations": 0, "answers": 0 },
  "drainError": "could not read 1 review log(s): alice-example-com.ff8d9819.jsonl: …" }
```

Exit **0** and exit **2** are the only two that mean "the drain told you the truth about an empty queue".

## Draining feedback — what `charter poll` returns (and the endpoints beneath it)

`charter poll` returns both streams below in one JSON envelope on stdout. If you need the raw surface —
scripting, debugging, or the `--url` path — the server exposes each as a plain HTTP `GET` carrying the
session key on the query string. Both are read-only reads; you don't POST anything (the browser SDK does
the POSTing, which is CSRF/same-origin gated — your GETs just need the key).

### `GET /api/poll?key=<key>` — queued annotations (long-poll)

```
GET http://127.0.0.1:<port>/api/poll?key=<key>
```

Long-polls: it waits until **any** reviewer activity is queued — an annotation, a `:::question` answer, or a
**Send to agent** hand-off — or ~30 s elapses, then returns the queued *annotations* as a JSON array and
clears that queue. (The answers and the hand-off are read by their own routes below; they wake the poll so
you learn about them at once instead of on the timeout.) An idle poll returns `[]` after the timeout — just
poll again. Each element:

```json
[
  {
    "id": "8f3c1a…",
    "kind": "element",
    "anchorId": "db-choice",
    "note": "Prefer Postgres unless latency is the top constraint.",
    "sourceLine": 42,
    "anchorStatus": "resolved"
  }
]
```

- `kind` — `element`, `text-range`, or `diagram-node`. **Hyphenated on the wire**, not camelCase: the C#
  enum is serialized through a dedicated converter (`AnnotationApi.AnnotationKindConverter`) so it matches
  the browser SDK's tokens exactly. Branching on `"textRange"` never matches.
- `anchorId` — the stable block id the note is attached to.
- `note` — the reviewer's free text.
- `sourceLine` — the **1-based markdown line** to edit, **resolved at drain time** against the plan file
  as it is right now (not when the reviewer wrote the note). This is what closes the round-trip: go to
  that line in `plan.charter.md` and revise.
- `anchorStatus` — `resolved` or `orphaned`. **Check this before acting on `sourceLine`.**
  - `resolved` — the anchor still exists; `sourceLine` is a live, current line number.
  - `orphaned` — the annotated block has since **changed or been removed**, so its content-derived anchor
    no longer resolves and `sourceLine` is `null`. Do **not** guess a line. Use the annotation's `quote`
    (and `nodeId` for a diagram node) to find what the reviewer was looking at, and treat the note as
    feedback on content that has already moved on — often it was your own earlier edit that addressed it.

An orphan is normal and expected in a living document: the reviewer comments, you edit the block, and
that block's anchor changes by construction. It is information, not an error.

### `GET /api/answers?key=<key>` — `:::question` answers

```
GET http://127.0.0.1:<port>/api/answers?key=<key>
```

Reports the queued answers submitted through `:::question` forms and returns them as a JSON array (no
long-poll — it returns immediately with whatever is queued, `[]` if nothing). This is a **peek**: it does
not remove the answers — `charter poll --apply` / `charter resolve` remove them only after folding them
**inline** into the plan, so a plain poll can never strand a reviewer's decision. Each element:

```json
[
  {
    "questionId": "db-choice",
    "mode": "single",
    "values": ["Postgres"],
    "target": "agent"
  }
]
```

- `questionId` — matches the `id` you gave the `:::question` block; this is the key you'll use in the
  `--answers` handoff JSON.
- `mode` — the question's `charter-format` mode token (`single` / `multi` / `free-text` / `bool` / `number`).
- `values` — the selected option value(s); always an array (empty if none).
- `target` — `human` or `agent`, echoed verbatim for downstream routing.

### `reviewSubmitted` — did the human hand you this round?

`charter poll`'s envelope carries two additive fields telling you *why* you woke:

```json
{
  "reviewSubmitted": true,
  "reviewSubmission": {
    "sequence": 3,
    "submittedAt": "2026-07-27T14:02:11.4180000+00:00",
    "annotations": 2,
    "answers": 1
  }
}
```

- `reviewSubmitted: false` (the normal case) — you woke because feedback arrived. This is **incremental**:
  the reviewer is still working. Act on what you drained, but expect more.
- `reviewSubmitted: true` — the reviewer clicked **Send to agent**: *"this round is complete, go revise."*
  Treat it as the point to do the substantial rewrite and re-render, not just to absorb one more comment.
  `reviewSubmission` records when they clicked and how much was queued at that moment.

**It is reported exactly once per hand-off.** The poll that reports it also acks it server-side, so the next
poll shows `false` again unless the reviewer hands off another round. Two details that matter:

- The ack fires **only on a clean drain**. If `drainError` is non-null the marker is deliberately left
  standing — you have not really been told — so the next successful poll reports it again.
- A failed ack is non-fatal by design: the marker survives and is re-reported. Delivery is **at-least-once**,
  so treat a repeated `sequence` as the same round, not a second one. Repeating a hand-off is safe; losing
  one is not.

A hand-off makes `poll` exit `0` (drained) even when both queues are empty, because "the human says this
round is done" is itself the thing you were waiting for.

A `review-log` envelope (below) always reports `reviewSubmitted: false` — a hand-off is a property of a live
session, and there is none.

The raw route, if you need it (`--url`, scripting, debugging):

```
GET  http://127.0.0.1:<port>/api/review?key=<key>
```

which returns `{ "submitted": bool, "submission": {…}|null, "pending": { "annotations": n, "answers": n } }`
without clearing anything. It is a read: only `charter poll` (or a `POST /api/{key}/review/ack?sequence=N`)
clears the marker.

**The hand-off never writes the plan.** The server records a signal and wakes you; every edit to
`plan.charter.md` is still yours to make.

## Reading a teammate's committed comments — `source` and `review`

Review comments are **also** written to a durable, per-author, append-only JSONL log beside the plan:
`<plan>.review/<slug>.<hash8>.jsonl` (so `plan.charter.md` → `plan.charter.review/`). They travel between
teammates **by git** — Charter reads git for the author's identity but **never** commits, pushes, or stages
anything. Design of record: `docs/plans/03-git-mediated-team-review.md`; don't restate it, cite it.

`charter review` opens that log **by default** and prints one stderr line naming the file and stating that
the records are meant to be committed and are permanent. If a human asks you to keep review local, the
answer is to gitignore `*.review/` — not a flag.

This is what makes `charter poll <plan>` useful when **no server is running at all**. With no live session
it folds every `*.jsonl` beside the plan and emits the *same* envelope. Two fields tell you so:

- **`source`** — `"session"` (a live loopback server, the normal path) or `"review-log"` (the server-less
  read). `session` is `null` on the `review-log` path because there genuinely isn't one.
- **`review`** — present only on a review-log annotation:
  `{ authorName, authorEmail, actor, status, ts }`.

```json
{ "session": null, "source": "review-log",
  "annotations": [ { "id": "cmt_…", "kind": "element", "anchorId": "b0821…",
    "note": "Name the limiter algorithm — token bucket or sliding window?",
    "sourceLine": 7, "quote": "We will add a per-tenant rate limiter",
    "review": { "authorName": "Alice Ng", "authorEmail": "alice@example.com",
                "actor": "human", "status": "open", "ts": "2026-07-27T10:45:12Z" },
    "anchorStatus": "resolved" } ] }
```

Rules for acting on it:

- **`status` is load-bearing.** `open` · `resolved` · `contested` · `retracted`.
  - **`contested`** means a resolve and a reopen happened concurrently, neither having seen the other. It is
    **not resolved** — treat it as open. Never pick a winner yourself.
  - **`retracted`** means the author withdrew it; the body reads `(comment withdrawn by author)`. Do not act
    on it.
- **`actor`** is `human` or `agent` — an agent has a voice in the log too.
- **A live session always wins.** If a server is running for that plan, `poll` drains it and never reads the
  log; `source` stays `"session"`.
- **Only `charter poll <plan>` reads the log.** Bare `charter poll` (auto-select) and `--url` / `--session`
  never do — they are session-discovery paths.
- **Consumption is machine-local.** A ledger under the per-user state dir records what *this machine* was
  handed, so you see each comment once; a teammate's machine is unaffected — nothing is written back to the
  log. A later `edit`, `reply`, `resolve`, `retract` or `reopen` mints a new record, which makes the comment
  **deliverable again** with its new `status`. That is intended: you are being told something new about it,
  not the same thing twice.
- **`--apply` does nothing on this path** — there is no answer queue in a log, only comments.

With a live session, the same fold is available to the page over `GET /api/review-log?key=<key>`, which is
what the review panel renders (author, actor, contested, orphaned). There is deliberately no static-file
route for `.review/`.

## The loop

Put the drains together and iterate until the plan is approved:

1. Start `charter review plan.charter.md` in the background (the ready line confirms it's up).
2. `charter poll --apply` — for each returned annotation, jump to its `sourceLine` in `plan.charter.md`
   and revise per the `note`; each drained `:::question` answer is folded **inline** into its block's
   `answer` field by `--apply`. **Check the exit code first**: `2` is a genuine clean-empty (poll again),
   `4` means the drain failed and you know nothing (retry — do *not* proceed). (A solo human
   reviewer with no looping agent uses `charter resolve` to fold in their own answers instead.)
   Add `--wait` to block until something arrives instead of polling in a loop: it returns as soon as the
   reviewer annotates, answers, or clicks **Send to agent** — all three wake it, so a reviewer's *decision*
   reaches you immediately rather than on the ~30 s timeout.
3. Check `reviewSubmitted`. `true` means the human handed you the round — revise properly. `false` means
   you caught feedback mid-review; a small correction is fine, but a large rewrite under a reviewer who is
   still reading is the thing to be careful about.
4. Save `plan.charter.md`. The human's next refresh shows the revision (live reload) — and the page reloads
   itself, so a reviewer who clicked **Send to agent** watches your revision appear without touching
   anything. Their unsaved work (a half-typed note, an unsaved answer) defers the reload behind a banner
   rather than being discarded.
5. Repeat until the human signals approval, then stop the server (Ctrl+C) and move to handoff.

The low-level surface, if you need it (scripting, debugging, the `--url` path): each stream is a plain
`GET` — any HTTP client works.

```
curl "http://127.0.0.1:53201/api/poll?key=Yb3…"       # queued annotations (waits up to ~30s)
curl "http://127.0.0.1:53201/api/answers?key=Yb3…"    # queued :::question answers (peek; returns immediately)
curl "http://127.0.0.1:53201/api/review?key=Yb3…"     # the round hand-off + live pending counts (peek)
curl "http://127.0.0.1:53201/api/review-log?key=Yb3…" # every author's committed comments, folded
```

Add `&wait=0` to `/api/poll` to skip the long-poll and drain whatever is queued right now.

Once the plan is approved, capture and hand it off — see `references/handoff.md`.

## When a plan is replaced at the same path

An annotation queue survives across `charter review` sessions, keyed to the plan's path. That is right for
the normal case — you edit the plan, the reviewer's earlier notes are still waiting. But **deleting a plan
and authoring a different one at the same path** used to hand the new document the old document's notes.

Charter now detects that and **sets the queue aside rather than delivering it**. The test is deliberately
conservative: the queue is quarantined only when the plan is not byte-identical to the revision it was
written against **and _not one_ of its annotations' anchors still resolves. One surviving anchor means the
queue is treated as live.** Over-eager quarantine would risk discarding real review work, which is the
worse failure — so the rule is the weakest one that still catches a genuine replacement.

**Nothing is ever destroyed.** The queue is copied to a `.stale-<utc>.json` file beside the sidecar, and
Charter names the path on stderr:

```
charter review: this plan looks replaced — 8 queued annotation(s) no longer match any block.
charter review: they are kept at <path>. Re-run with --keep-annotations to restore them.
```

So when you drive a review and see that line, **tell the human** — they have not lost their notes, and
`charter review <plan> --keep-annotations` brings them back. Do not silently proceed as though the queue
was empty.

Two limits worth knowing:

- **Answers are never quarantined.** A `:::question` answer is keyed by the question's `id`, not by an
  anchor, so anchor evidence says nothing about it. If a replaced plan reuses a question id, an answer
  from the old document can still fold into the new one.
- **This applies to the machine-local queue, not the committed review log.** A log record whose anchor no
  longer resolves is still delivered, deliberately — an orphan there is a neutral fact carrying its
  `quote`, not an error. See the review-log section above.
