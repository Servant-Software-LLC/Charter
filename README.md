# Charter

**Visual, reviewable plans your agent drafts — and you annotate in place.**

Charter is the front door to an agentic delivery pipeline. An AI authors a rich, block-structured
plan (diagrams, tables, comparisons, annotated code — not a wall of prose); you review it in the
browser and **comment right on the deliverable**, so every note carries the context of exactly what
it points at. The approved plan is then handed to **[Guardrails]** to be broken down into an
executable, verified task DAG.

> **Status:** the MDX renderer, loopback review server, in-place annotation loop, offline export, and
> Guardrails handoff are all implemented and shipping in the binary. Charter builds, its tests are
> green, and it packs as a `dotnet` tool / native binary.

## Why

Plain-text plans are cheap for an agent to produce and painful for a human to review: feedback
lands in chat, detached from the thing it's about. Charter makes the plan *itself* the review
surface — the agent gets to be **expressive**, and your comments stay **anchored** to the block,
row, diagram node, or line they belong to.

## Where it fits

```
Charter  →  Guardrails  →  firstmate / gnhf
```

- **Charter** (this repo) — the AI drafts the plan as blocks; you annotate and approve it in place.
- **[Guardrails]** — consumes the approved deliverable and breaks it into tasks, each with
  deterministic acceptance checks ("guardrails"), then runs the DAG to green.
- **[firstmate]** / **[gnhf]** — agent orchestrators that do the actual work under those guardrails.

## Usage

Charter is a CLI over a single plan file. An AI authors the plan as block-structured markdown — a
small, fixed block catalog (diagram, table, comparison, code/diff, question) — and you drive it
through four verbs:

- `charter render <plan.mdx> -o <out.html>` — renders a plan to one portable HTML artifact.
- `charter review <plan.mdx> [--no-open]` — serves the plan over the loopback review server
  (`127.0.0.1`, an ephemeral port, gated on a per-session key) and opens your browser so you can
  annotate elements, text ranges, and diagram nodes **in place**. `--no-open` serves without
  launching a browser.
- `charter export <plan.mdx> -o <out.html>` — writes a self-contained, **offline** artifact with
  every local asset inlined as a `data:` URI — no server, no runtime, portable anywhere.
- `charter handoff <plan.mdx> -o <out.md> [--answers <answers.json>]` — emits plain CommonMark for
  Guardrails, resolving each `:::question` against the optional `--answers` JSON file (open
  questions that have no answer are handed off flagged).
- `charter --version` — prints the version.

A typical author → review → handoff pass:

```bash
# 1. Review the plan: serves it locally and opens the browser to annotate in place
charter review plan.mdx

# 2. Export a portable, offline copy of the reviewed deliverable
charter export plan.mdx -o plan.html

# 3. Hand the approved plan off to Guardrails as plain CommonMark
charter handoff plan.mdx -o plan.md --answers answers.json
```

If you're driving Charter from an agent, a bundled usage skill lives at `skills/charter/`.

## Still ahead

A few capabilities are deliberately **out of v1**, each tracked as its own issue so it outlives the
plan:

- **Recap mode** — building a plan from a diff (`charter recap`), a v2 addition.
- **Hosted share / publish** — v1 produces local artifacts only; nothing is hosted or published for
  you.
- **Telemetry** — v1 ships **none**: zero analytics dependency, zero data egress. Any future
  telemetry would be strictly opt-in and vendor-neutral, never Lavish's default-on model.

## Install

There's no published release, Homebrew tap, or NuGet package yet — those are gated on the first
real release. Build from source today.

## Build from source

```bash
dotnet build Charter.sln -c Release
dotnet test  Charter.sln -c Release
dotnet run   --project src/Charter.Cli -- --version
```

## Acknowledgements — the prior art this combines

Charter is a deliberate **synthesis of two existing ideas**, reimplemented in C#/.NET with
Guardrails-style engineering and distribution. Full credit to both:

- **[Lavish (lavish-axi)]** by Kun Chen — the model Charter follows *in principle and function*: a
  CLI + local server that opens an agent-generated artifact in the browser and lets a human
  annotate elements, text ranges, and diagram nodes, shipping those annotations back to the agent
  over a feedback loop. Charter reimplements that **comment-in-place review loop**.
- **[Agent-Native Plans (`visual-plan`)]** by Builder.io — the model for **authoring a plan as
  structured MDX blocks** rather than raw HTML or plain prose. Charter adopts that block-based
  authoring surface.

Charter's own contribution is combining the two — Lavish's in-place review loop **and**
visual-plan's MDX block authoring — as an independent, C#-native tool that feeds Guardrails.

## License

MIT © Servant Software LLC. See [LICENSE](LICENSE).

[Guardrails]: https://github.com/Servant-Software-LLC/Guardrails
[firstmate]: https://github.com/kunchenguid/firstmate
[gnhf]: https://github.com/kunchenguid/gnhf
[Lavish (lavish-axi)]: https://github.com/kunchenguid/lavish-axi
[Agent-Native Plans (`visual-plan`)]: https://github.com/BuilderIO/skills/tree/main/skills/visual-plan
