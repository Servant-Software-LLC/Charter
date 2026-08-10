# Charter

**Visual, reviewable plans your agent drafts — and you annotate in place.**

Charter is the front door to an agentic delivery pipeline. An AI authors a rich, block-structured
plan (diagrams, tables, comparisons, code — not a wall of prose); you review it in the browser and
**comment right on the deliverable**, so every note carries the context of exactly what it points at.
Your answers fold back **into the plan file itself**, and the approved plan is then handed to
**[Guardrails]** to be broken down into an executable, verified task DAG.

> **Status:** shipping on all channels. The renderer, loopback review server, in-place annotation loop
> with answers folded back into the plan, git-mediated team review, offline export, unattended runs, the
> `charter convert` seed, and Guardrails handoff are all implemented in the binary — released via Homebrew,
> NuGet (`dotnet` tool), and native binaries.

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

> **Feeding Guardrails.** `charter handoff` flattens the approved plan to plain CommonMark that **any**
> Guardrails version breaks down — that's the supported path today. Consuming the `.charter.md` **directly**
> (blocks intact, richer signal) arrives in **Guardrails ≥ `1.0.0-preview.48`**.

## Usage

Charter is a CLI over a single plan file. An AI authors the plan as block-structured markdown — a
small, fixed block catalog (diagram, table, comparison, code/diff, question) — and drives it through
these verbs. In practice you don't type most of them: you ask your agent to *"turn this doc / PDF /
link into a plan"* and it authors the `.charter.md` for you (guided by the bundled skill), then you
review it in the browser.

- `charter convert <doc.md> -o <plan.charter.md>` — seeds a plan from a plain Markdown doc: passes
  every block through, promotes an obvious "Open Questions" / "Risks" list into question blocks, and
  stamps the format marker. The mechanical floor an agent then enriches (diagrams, comparisons, more
  questions) — the rich "any source → plan" authoring is agent-driven, not a deterministic command.
- `charter recap <range> -o <plan.charter.md>` — the same seed from the other direction: a **git diff**
  instead of an intent, for reviewing a change that already happened. Emits an overview, a commit table
  and one per-line-annotatable diff block per file; git is read-only. Like `convert`, the mechanical
  floor an agent then enriches with the summary, grouping, diagram and questions a diff cannot state.
  It describes a **change**, not an execution run — run reporting stays Guardrails' job.
- `charter render <plan.charter.md> -o <out.html>` — renders a plan to one portable HTML artifact.
- `charter review <plan.charter.md> [--no-open]` — serves the plan over the loopback review server
  (`127.0.0.1`, an ephemeral port, gated on a per-session key) and opens your browser so you can
  annotate elements, text ranges, and diagram nodes **in place**. `--no-open` serves without
  launching a browser.
- `charter poll [<plan.charter.md>] [--apply]` / `charter resolve <plan.charter.md>` — drain the
  running review session's annotations and `:::question` answers, folding each answer **inline into the
  `.charter.md`** (agent-in-the-loop `poll --apply`, or `resolve` for a solo human review). The plan is
  a living document that accumulates your decisions before handoff.
- `charter headless <plan.charter.md> [--out-dir <dir>]` — the **unattended** path, for a run with no human
  present. Writes the same offline artifact `export` does plus a forensic JSON record — the plan's hash, every
  question and whether it was answered, and an anchor→source-line map so an element in the artifact can be
  traced back to the markdown **after the fact**. Serves nothing and waits for nothing; exits **2**, not in
  error, when a human still has to decide or fix something.
- `charter export <plan.charter.md> -o <out.html>` — writes a self-contained, **offline** artifact with
  every local asset inlined as a `data:` URI — no server, no runtime, portable anywhere.
- `charter handoff <plan.charter.md> -o <out.md> [--answers <answers.json>]` — emits plain CommonMark for
  Guardrails, resolving each `:::question` against the optional `--answers` JSON file (open
  questions that have no answer are handed off flagged).
- `charter skills install [--project]` — installs the bundled agent skills so your agent (and Guardrails)
  can discover them.
- `charter --version` — prints the version.

A typical author → review → handoff pass:

```bash
# 1. Review the plan: serves it locally and opens the browser to annotate in place
charter review plan.charter.md

# 2. Export a portable, offline copy of the reviewed deliverable
charter export plan.charter.md -o plan.html

# 3. Hand the approved plan off to Guardrails as plain CommonMark
charter handoff plan.charter.md -o plan.md --answers answers.json
```

If you're driving Charter from an agent, a bundled usage skill lives at `skills/charter/`.

## Still ahead

A few capabilities are deliberately **out of v1**, each tracked as its own issue so it outlives the
plan:

- **Recap mode** — building a plan from a diff (`charter recap`), a v2 addition.
- **Telemetry** — v1 ships **none**: zero analytics dependency, zero data egress. Any future
  telemetry would be strictly opt-in and vendor-neutral, never Lavish's default-on model.

And two things that were on this list and **won't be built**, because the answer turned out to be
something Charter already has:

- **Hosted share / publish** — *git is the share.* A plan and its per-author review logs are committed
  files: a teammate pulls the commit and sees the rendered plan with everyone's comments folded in.
  Hosting would also cut against loopback-only and zero-egress. For someone outside the repo,
  `charter export` produces a genuinely self-contained offline artifact.
- **Review rounds / diff between rounds** — *git supplies rounds.* A round is a commit, the diff is
  `git diff`, and every review record carries the plan hash it was written against.

## Install

Charter ships as a single self-contained native binary (no .NET runtime required) and as a `dotnet`
tool.

**Homebrew** (macOS / Linux):

```bash
brew install servant-software-llc/tap/charter
```

**`dotnet` tool** (any OS with .NET 10+):

```bash
dotnet tool install --global ServantSoftware.Charter
```

**Direct download** — grab the binary for your platform from the
[latest release](https://github.com/Servant-Software-LLC/Charter/releases/latest), extract, and run
`charter --version`.

> **macOS:** `brew install` and `dotnet tool install` need no extra steps — Gatekeeper's notarization
> check keys off the `com.apple.quarantine` attribute, which package managers don't set. Only a binary
> you download **in a browser** is quarantined. Clear it after extracting:
>
> ```sh
> xattr -dr com.apple.quarantine ./charter
> ```
>
> macOS 15 removed the Control-click → Open bypass; the GUI route is now System Settings → Privacy &
> Security → "Open Anyway". The binaries carry an ad-hoc signature (which is what Apple Silicon
> requires to run them at all) but are not Developer ID signed or notarized.

## Build from source

Requires the **.NET 10 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)) — and
nothing else. A .NET 10 SDK carries the .NET 10 runtime, so the same install covers building,
testing, and running the tool.

```bash
dotnet build Charter.sln -c Release
dotnet test  Charter.sln -c Release
dotnet run   --project src/Charter.Cli -- --version
```

The headless-browser tests drive Chromium via Playwright. Without it they **skip** rather than
fail, so a run can look green while covering nothing — a complete run is **759 passed, 0 skipped**:

```bash
pwsh tests/Charter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install chromium
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
