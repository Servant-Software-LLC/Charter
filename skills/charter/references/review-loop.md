# The review loop — serve, annotate, drain feedback

This is the load-bearing part of driving Charter: the human reviews the plan in the browser and comments
**in place**, and you read that feedback back and revise. You drain that feedback with **`charter poll`** —
a CLI client over the loopback review server that discovers the running session (so the capability key
never crosses your command line), returns the queued annotations + `:::question` answers, and, with
`--apply`, writes the answers **inline** into the plan's `:::question` blocks. `charter resolve` is the
solo-human-reviewer companion that folds queued answers in when no agent is looping `poll`. The loopback
HTTP endpoints documented below are what `poll` wraps (and what its `--url` escape hatch targets directly).

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

They also fill in and submit any **`:::question`** forms. Each annotation is resolved server-side to the
**1-based markdown source line** of the block it points at (via the content-derived source-map), so the
feedback you drain tells you exactly which line to edit — that round-trip is the whole point (invariant 2).

## Draining feedback — what `charter poll` returns (and the endpoints beneath it)

`charter poll` returns both streams below in one JSON envelope on stdout. If you need the raw surface —
scripting, debugging, or the `--url` path — the server exposes each as a plain HTTP `GET` carrying the
session key on the query string. Both are read-only reads; you don't POST anything (the browser SDK does
the POSTing, which is CSRF/same-origin gated — your GETs just need the key).

### `GET /api/poll?key=<key>` — queued annotations (long-poll)

```
GET http://127.0.0.1:<port>/api/poll?key=<key>
```

Long-polls: it waits until an annotation is queued (or ~30 s elapses), then returns the queued
annotations as a JSON array and clears the queue. An idle poll returns `[]` after the timeout — just poll
again. Each element:

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

- `kind` — `element`, `textRange`, or `diagramNode` (camelCase in JSON).
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

## The loop

Put the drains together and iterate until the plan is approved:

1. Start `charter review plan.charter.md` in the background (the ready line confirms it's up).
2. `charter poll --apply` — for each returned annotation, jump to its `sourceLine` in `plan.charter.md`
   and revise per the `note`; each drained `:::question` answer is folded **inline** into its block's
   `answer` field by `--apply`. Nothing queued yet is a clean-empty result — poll again. (A solo human
   reviewer with no looping agent uses `charter resolve` to fold in their own answers instead.)
3. Save `plan.charter.md`. The human's next refresh shows the revision (live reload).
4. Repeat until the human signals approval, then stop the server (Ctrl+C) and move to handoff.

The low-level surface, if you need it (scripting, debugging, the `--url` path): each stream is a plain
`GET` — any HTTP client works.

```
curl "http://127.0.0.1:53201/api/poll?key=Yb3…"     # queued annotations (waits up to ~30s)
curl "http://127.0.0.1:53201/api/answers?key=Yb3…"  # queued :::question answers (peek; returns immediately)
```

Once the plan is approved, capture and hand it off — see `references/handoff.md`.
