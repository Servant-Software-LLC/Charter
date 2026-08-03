# Desk Pet — demo runbook

Every command below is a **real `charter` command**, typed as a user would type it. There is no
wrapper script, deliberately: the commands *are* the demo. If the audience can't see
`charter review desk-pet.charter.md`, they haven't seen Charter.

~10 minutes at a comfortable pace. Two terminals.

---

## Before you start

```bash
charter --version              # 0.9.0 or newer
```

Copy the chart somewhere scratch so the committed one stays pristine and you can re-run:

```bash
mkdir -p /tmp/demo && cp desk-pet.charter.md /tmp/demo/
cd /tmp/demo
```

**Why:** `resolve` writes answers *into* the chart. Rehearse against the committed file and your
next run starts from an already-answered plan.

---

## Beat 1 — the input is a sentence

Open **`PROMPT.md`** and read the prompt aloud. That's the whole input.

> "No file paths, no block names, no CLI. Someone asked their agent to **chart** a piece of work."

Point out that the prompt never says "diagram" or "comparison" — the agent chose those.

## Beat 2 — the chart

**Option A — author it live.** Paste `PROMPT.md` into Claude Code. Strongest version, 2–3 minutes
of watching text appear, and a live model can wander.

**Option B — skip ahead.** *"I ran that prompt, and this is what came back."* The committed
`desk-pet.charter.md` **is** the output of that prompt, so this is honest framing, not a cheat.

Either way, show the source briefly — it's just markdown with `:::` blocks — then render it:

```bash
charter render desk-pet.charter.md -o desk-pet.html
```

> "One portable HTML file. No server, no runtime — mail it to someone."

Open it. Don't dwell; the next beat is the point.

## Beat 3 — review (the centrepiece)

**Terminal 1:**

```bash
charter review desk-pet.charter.md
```

It prints the ready line and opens your browser:

```
Charter review server ready: http://127.0.0.1:<port>/?key=<key>
```

> "Loopback only, and gated on a per-session key. Nothing left this machine."

### In the browser — do these three, in this order

They escalate, and each one is something a chat message cannot do:

1. **A table row.** Comment on the **"Lines changed"** row of the *what counts as feeding it*
   comparison.
   > *"I'm not commenting on the table. I'm commenting on that row."*

2. **A diagram node.** Comment on the **`Feral`** state in the mood diagram.
   > *"That's a rendered SVG. The comment is anchored to a node inside it."*

3. **A diff line.** Comment on the **`deskpet feed --quiet || true`** line.
   > *"Per-line, on a diff. This is where review actually happens."*

### Then answer the questions

Answer all four in the panel. Note out loud that they're **real form controls, not prose** — two
single-selects, a yes/no, and a free-text — because the block declares its `mode`.

### Then click **Send to agent**

> "That's me saying *this round is done*."

## Beat 4 — the drain

**Terminal 2** (leave the server running):

```bash
charter poll desk-pet.charter.md --apply
```

This is the beat worth slowing down for. Two things land at once:

**Everything the reviewer did arrives in one envelope** — annotations *and* answers:

```json
"reviewSubmitted": true,
"drained": { "annotations": 3, "answers": 4 }
```

**And the two kinds of feedback are not the same thing:**

| | What it is | What happens to it |
|---|---|---|
| **Annotation** | Advice **with a location** — it carries the resolved markdown **source line** | An **agent** reads it and decides what to edit. Nothing is written automatically. |
| **Answer** | Data **with a home** — the `answer` key of its own `:::question` | Written back **mechanically**. No judgment needed. |

> *"An annotation needs an agent. An answer needs nobody."*

Then show the file:

```bash
grep -A2 ':::question' desk-pet.charter.md
```

The `answer` key is now present. **The plan is a living document, not a transcript** — this is what
makes it safe to hand to an automated next stage.

### If you're demoing solo, without an agent looping

```bash
charter resolve desk-pet.charter.md
```

Same fold-back, built for exactly this case: a human reviewer and no agent running `poll`.

---

## Beat 4 — variant B: agent in the loop (no terminal at the climax)

The stronger version, if your rehearsal goes cleanly. **Set this up before you start.**

In a second terminal, have your agent (Claude Code with the `charter` skill) run the drain in
**wait** mode:

```bash
charter poll desk-pet.charter.md --wait --apply
```

`--wait` blocks on the server's wake signal. Clicking **Send to agent** in the browser completes
that signal, so `poll` returns *immediately* with the round — the agent revises the plan, and the
page **live-reloads in front of the audience**.

What they see: you comment, you click one button, and the plan changes. **No terminal at the
climax.** That reframes Charter from a file format into a loop, which is the harder thing to
convey.

> **Why the button can't just call `poll` itself.** The plan file is single-writer — the drafting
> agent. The server never writes it and never invokes an agent. `Send to agent` hands the round
> over; the agent, already listening, picks it up. That constraint is deliberate, not an oversight.

### The honest caveats — know these before someone asks

- **It needs an agent actually running.** If nothing is listening, the click sets `submitted: true`
  and nothing happens.
- **The agent cannot push back.** If it thinks a comment is wrong or unclear, it has no channel to
  say so — it revises, or it doesn't.
- **Those two failures look identical to success.** A reviewer cannot currently distinguish
  *agreed and revised* / *declined* / *misread and revised the wrong thing* / *nobody listening*.

If you're asked *"what if the agent disagrees?"* — that gap is real and tracked as
[#106](https://github.com/Servant-Software-LLC/Charter/issues/106). Answer it plainly; it lands
better than improvising, and it's a roadmap item rather than a flaw in the idea.

### Which variant to run

| | Variant A (`poll --apply` by hand) | Variant B (agent in the loop) |
|---|---|---|
| Moving parts | one terminal, one command | an agent running live |
| Shows the drain explicitly | **yes** — the envelope is on screen | no, it happens off-camera |
| Shows Charter as a *loop* | no | **yes** |
| Risk | low | a live model on stage |

Rehearse A first. Only reach for B if B also rehearses clean — A is a good demo, and a stalled B
in front of an audience is not.

> **Only `charter poll` clears the queue.** `charter resolve` and the `/api/...` endpoints are
> peeks. So don't run a bare `charter poll` first "to look" — it drains, and a following `resolve`
> will find nothing. Run **one** of `poll --apply` *or* `resolve`.

## Beat 5 — handoff

```bash
charter handoff desk-pet.charter.md -o desk-pet.md
head -40 desk-pet.md
```

> "Every `:::` block is now plain CommonMark. Answered questions became decisions; anything still
> open is flagged as open. This is what Guardrails breaks into a task DAG."

Ctrl-C terminal 1 when you're done.

---

## The one sentence to land

> **Feedback stays attached to the thing it's about — a row, a node, a line — and the answers end
> up inside the plan itself.**

## If something goes wrong

| Symptom | Do this |
|---|---|
| `charter: command not found` | `dotnet run --project src/Charter.Cli -c Release -- <verb> …` from the repo |
| Browser doesn't open | Paste the capability URL from the ready line |
| Live authoring wanders | *"Here's one I prepared"* — use the committed chart |
| `resolve` exits **5** | A question changed shape mid-demo; `--apply-stale-answers` forces it |
| `poll` exits **2** | Clean-empty — nothing was queued. Did you submit in the browser? |
| `poll` exits **3** | No live session — terminal 1 isn't running |

`poll` exit codes: `0` drained · `2` clean-empty · `3` no session · `4` drain failed · `5` apply refused.

## Rehearse this once

The three annotation gestures are the demo. Do them once end to end before you present — that is
the part no amount of preparation on paper substitutes for.
