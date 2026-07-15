# Charter

**Visual, reviewable plans your agent drafts — and you annotate in place.**

Charter is the front door to an agentic delivery pipeline. An AI authors a rich, block-structured
plan (diagrams, tables, comparisons, annotated code — not a wall of prose); you review it in the
browser and **comment right on the deliverable**, so every note carries the context of exactly what
it points at. The approved plan is then handed to **[Guardrails]** to be broken down into an
executable, verified task DAG.

> **Status: early scaffold.** The solution builds, packs as a `dotnet` tool, and ships as a native
> binary through the release pipeline. The MDX renderer, local review server, and annotation loop
> are the next milestones.

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

## How it will work (roadmap)

- Author the plan as **MDX blocks** — markdown plus a small, fixed block catalog (diagram, table,
  comparison, code/diff, question) — rendered to a portable HTML deliverable.
- `charter <plan.mdx>` opens a **local review server** in the browser; you annotate elements, text
  ranges, and diagram nodes; feedback returns to the agent through a `poll` loop.
- Distributed like Guardrails: a self-contained **native binary** via a Homebrew tap and SDK-free
  installers, plus a `dotnet tool` on NuGet — **no .NET runtime required** for consumers.

## Install

_Coming with the first release._ It will mirror Guardrails:
`brew install servant-software-llc/tap/charter`, a `curl … | bash` installer, or
`dotnet tool install --global ServantSoftware.Charter`.

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
