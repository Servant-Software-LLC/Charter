# Desk Pet — the Charter demo runbook

A complete author → review → handoff demo that starts from **one prompt** and ends with plain
CommonMark ready for Guardrails. Roughly 8 minutes at a comfortable pace.

## What this demonstrates

| Beat | What the audience sees |
|---|---|
| The prompt | A human asks to **"chart this out"** — no file paths, no block names, no CLI |
| Authoring | The agent picks the blocks itself: a diagram for the state machine, a comparison for the trade-off, questions for what it can't decide |
| Review | Comments anchored to a **table row**, a **diagram node**, and a **diff line** — not a chat message detached from the artifact |
| Fold-back | Answers are written **into the `.charter.md`**; the plan is a living document, not a transcript |
| Handoff | The same file flattens to plain CommonMark for the next stage |

The point to land: **feedback stays attached to the thing it's about.**

## Prerequisites

```bash
charter --version      # 0.9.0 or newer
```

Install with `brew install servant-software-llc/tap/charter` or
`dotnet tool install -g ServantSoftware.Charter`. The demo scripts also accept `--local` /
`-Local` to run a locally-built CLI instead, which is the safer choice if you're demoing from a
machine you haven't set up.

For the live-authoring opening you also need the `charter` skill installed in Claude Code:

```bash
charter skills install --force
```

Then **restart Claude Code** — skill activation is matched at session start, so a skill installed
mid-session won't trigger.

## Running it

```bash
./demo.sh          # macOS / Linux
./demo.ps1         # Windows
```

Both work on a **copy** in `.work/`, so the committed chart stays pristine and you can rehearse as
many times as you like. That's deliberate: `resolve` writes answers back into the chart, so a run
against the committed file would leave your next rehearsal starting from an already-answered plan.

## The two ways to open

**Live authoring (impressive, riskier).** Paste [`PROMPT.md`](PROMPT.md) into Claude Code and let
the agent write the chart in front of everyone. This is the strongest version — it shows the whole
value proposition starting from a sentence — but a live model can wander, and you're spending 2–3
minutes watching text appear.

**Pre-authored (safe).** Open with "I gave it this prompt, and 30 seconds later it produced this,"
then go straight to the review loop. The committed `desk-pet.charter.md` **is** the output of that
prompt, so this is an honest framing, not a cheat.

Pick based on the room. If you're at all unsure, pre-authored — the review loop is the part people
remember, and it's worth arriving there with time to spare.

## The three moments that matter

Everything else is setup. These are the beats worth slowing down for:

1. **Comment on the "Lines changed" row** of the comparison. Say: *"I'm not commenting on the
   table. I'm commenting on that row."* Per-row anchoring is the differentiator.
2. **Comment on the "Feral" node** in the mood diagram. A rendered SVG diagram whose individual
   nodes are annotatable is the thing nobody expects.
3. **Answer the four questions, then show the source file.** The answers are now inside the
   `.charter.md`. This is what "living document" means, and it's what makes the plan safe to hand
   to an automated next stage.

## If something goes wrong

| Symptom | Do this |
|---|---|
| `charter: command not found` | Re-run with `--local` / `-Local` |
| Browser doesn't open | The ready line prints the capability URL — paste it manually |
| Live authoring produces a weak chart | Stop, say "here's one I prepared", use the committed chart |
| `resolve` reports a stale answer | A question changed shape mid-demo; `--apply-stale-answers` forces it |

## Files

| File | What it is |
|---|---|
| [`PROMPT.md`](PROMPT.md) | The single prompt that starts everything, and why it elicits each block |
| `desk-pet.charter.md` | The chart that prompt produced — committed as the known-good fallback |
| `demo.sh` / `demo.ps1` | Stepped drivers for the loop |
| `.work/` | Scratch output, git-ignored, recreated per run |
