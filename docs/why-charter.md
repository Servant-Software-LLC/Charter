# Why Charter?

**Positioning brief — why build Charter when Lavish and Builder.io's `visual-plan` already exist.**

Charter isn't competing with Lavish or Builder.io's `visual-plan`. It takes the best idea from
each, drops what doesn't fit our pipeline, and adds the one thing neither has — it's the reviewed
**front door to autonomous execution**.

## The suite

```
Charter  ─▶  Guardrails  ─▶  firstmate
(agree the    (turn it into    (run it to
 plan)         a verified        green)
               task DAG)
```

> **Charter the trip** · Guardrails keep you between the lines · firstmate does the driving.

## It's the combination — each fixes the other's flaw

- **From Lavish — the comment-in-place review loop.** Render the plan in the browser, annotate
  elements / text / diagram-nodes in place, feedback goes straight to the agent. But Lavish makes the
  agent **hand-write HTML** — so it needs a whole layout-audit subsystem just to catch overflow and
  overlap bugs.
- **From `visual-plan` — typed blocks from a validated schema.** The agent fills in vetted, typed
  blocks — "type-safety for natural language" — instead of hand-writing HTML, so that entire class of
  layout bugs disappears **by construction**.

**Charter = the review loop + the typed blocks**, done lean in a single C#/.NET binary — and pointed
at an execution engine. Neither existing tool has both.

## The real differentiator (the meeting answer)

Lavish and `visual-plan` produce a reviewed plan — and **stop**. They're review endpoints.

Charter's output is **canonical reviewed markdown that feeds Guardrails**, which turns it into a
guardrailed, executable task DAG that agents run to green.

The review loop exists *because* the plan is about to drive autonomous execution. Charter is the
human-in-the-loop checkpoint that earns the right to let the agents run — a capability neither of the
others is even trying to be.

## Where Charter lands vs. the two it draws from

| Capability | Charter | Lavish | visual-plan |
|---|:---:|:---:|:---:|
| In-browser comment-in-place review | ● | ● | ○ |
| Typed block schema — no hand-written HTML | ● | — | ● |
| Feeds an execution engine (verified task DAG) | ● | — | — |
| Local-first, loopback-only, zero telemetry | ● | — | — |
| Single self-contained binary, suite-native (C#/.NET) | ● | — | — |
| Portable plain-markdown handoff — no lock-in | ● | ○ | — |

**●** full · **○** partial · **—** none / not the goal

## If pushed on "why not just adopt one?"

- **Stack — suite consistency.** Guardrails and firstmate are C#/.NET. Charter is one native binary
  that matches them — adopting a JS/TS tool would drag a compiler and framework into the pipeline for
  no benefit.
- **Trust — properties they don't offer.** Lavish ships telemetry **default-on**; `visual-plan`'s
  default deliverable is **hosted** — data leaves the machine. Charter is loopback-only, local-first,
  zero telemetry. Not a switch you flip on someone else's tool.
- **No lock-in — portable at the handoff.** Charter hands off plain canonical markdown — not MDX or a
  proprietary hosted format — so the whole pipeline stays open and portable.

## Two soundbites to have ready

> Lavish and visual-plan are where you review a plan. Charter is where you review a plan **that's about
> to be executed autonomously**.

> We took Lavish's review loop and visual-plan's typed blocks, dropped the telemetry and the hosted
> dependency, and pointed the output at **our own execution engine**.

---

**Not reinvention — recomposition.** Charter reuses both ideas openly and adapts Lavish's annotation
SDK with attribution (MIT). We re-composed two proven ideas into the missing piece, on our stack, with
our trust guarantees.

*See also: [`docs/plans/01-combine-lavish-and-visual-plan.md`](plans/01-combine-lavish-and-visual-plan.md) — the plan of record, whose References section links Lavish and visual-plan.*
