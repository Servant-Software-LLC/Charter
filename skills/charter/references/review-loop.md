# The review loop — serve, annotate, drain feedback

This is the load-bearing part of driving Charter: the human reviews the plan in the browser and comments
**in place**, and you read that feedback back and revise. There is **no CLI verb** that hands you the
feedback — the review server exposes it over HTTP on the loopback interface, and you drain it there. This
playbook teaches that drain explicitly.

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

## Draining feedback — the two HTTP endpoints

The server queues two independent streams and you drain each with a plain HTTP `GET` carrying the session
key on the query string. Both are read-only drains; you don't POST anything (the browser SDK does the
POSTing, which is CSRF/same-origin gated — your GETs just need the key).

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
    "sourceLine": 42
  }
]
```

- `kind` — `element`, `textRange`, or `diagramNode` (camelCase in JSON).
- `anchorId` — the stable block id the note is attached to.
- `note` — the reviewer's free text.
- `sourceLine` — the **1-based markdown line** to edit. This is what closes the round-trip: go to that
  line in `plan.charter.md` and revise.

### `GET /api/answers?key=<key>` — `:::question` answers

```
GET http://127.0.0.1:<port>/api/answers?key=<key>
```

Drains the queued answers submitted through `:::question` forms and returns them as a JSON array (no
long-poll — it returns immediately with whatever is queued, `[]` if nothing). Each element:

```json
[
  {
    "questionId": "db-choice",
    "mode": "single-select",
    "values": ["Postgres"],
    "target": "agent"
  }
]
```

- `questionId` — matches the `id` you gave the `:::question` block; this is the key you'll use in the
  `--answers` handoff JSON.
- `mode` — the question's selection mode.
- `values` — the selected option value(s); always an array (empty if none).
- `target` — `human` or `agent`, echoed verbatim for downstream routing.

## The loop

Put the two drains together and iterate until the plan is approved:

1. Start `charter review plan.charter.md` in the background; parse `<port>` and `<key>` from the ready line.
2. `GET /api/poll?key=<key>` — for each returned annotation, jump to its `sourceLine` in `plan.charter.md` and
   revise per the `note`. An empty array just means "nothing yet"; poll again.
3. `GET /api/answers?key=<key>` — for each answer, record the decision against its `questionId`; you'll
   feed these into the handoff `--answers` JSON (see `handoff.md`). Fold consequential decisions back
   into the plan prose too.
4. Save `plan.charter.md`. The human's next refresh shows the revision (live reload).
5. Repeat until the human signals approval, then stop the server (Ctrl+C) and move to handoff.

Example drain with `curl` (any HTTP client works — this is a plain GET):

```
curl "http://127.0.0.1:53201/api/poll?key=Yb3…"     # queued annotations (waits up to ~30s)
curl "http://127.0.0.1:53201/api/answers?key=Yb3…"  # queued :::question answers (returns immediately)
```

Once the plan is approved, capture and hand it off — see `references/handoff.md`.
