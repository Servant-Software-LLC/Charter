# AGENTS.md

Guidance for coding agents working in the Charter repository.

## Commands

```sh
dotnet build Charter.sln -c Release
dotnet test  Charter.sln -c Release
dotnet run   --project src/Charter.Cli -- --version

# one project, or one class, while iterating
dotnet test tests/Charter.Cli.Tests/Charter.Cli.Tests.csproj -c Release --filter "FullyQualifiedName~CommandCatalogTests"
```

## Project conventions

- **.NET 10** (`net10.0`; the SDK is pinned in `global.json`), C#, nullable + implicit usings enabled,
  warnings-as-errors — a warning fails the build. `Directory.Build.props` holds that policy and pins
  `AnalysisLevel`, so an SDK roll-forward cannot quietly introduce diagnostics that break a green build.
- Three source projects: `src/Charter.Core` (the block model, the renderer, and the file-in/file-out
  transforms — export, handoff, convert, recap), `src/Charter.Server` (the loopback review server,
  sessions, and the review log), and `src/Charter.Cli` (the `charter` tool itself — System.CommandLine
  + Spectre.Console). Each has a mirror under `tests/`, plus `tests/Charter.Browser.Tests`, which drives
  the real annotation UI through Playwright; its facts are `[SkippableFact]`, so they skip rather than
  fail on a host with no Chromium.
- The skills under `skills/` are **embedded in the binary** as resources (see `Charter.Cli.csproj`) and
  extracted by `charter skills install`. Editing a file there changes the shipped tool, not just the repo.
- Distribution mirrors Guardrails: a NuGet `dotnet tool` plus native self-contained binaries shipped
  via a Homebrew tap and SDK-free installers (see `.github/workflows/release.yml` and `install.sh`).
- The published binary is renamed from `Charter.Cli` to `charter` in the release workflow — do not
  set a global `-p:AssemblyName`, which would also rename `Charter.Core` and collide on publish.

## Adding a CLI verb

Add one entry to `CharterCommands.Commands` in `src/Charter.Cli/CharterCommands.cs`. That catalog is the
single source of truth: dispatch, the `--help` banner, and the unknown-verb error are all generated from
it, so there is no second list to keep in step.

Then document it. `CommandCatalogTests` and `DocumentedCommandsTests` fail until the verb is dispatchable
and named in **both** `README.md` and `skills/charter/SKILL.md`. That is deliberate rather than fussy:
`charter reply` shipped in 0.13.0 and was missing from `--help` and the README right through 0.18.0, so an
agent enumerating Charter's capabilities concluded the feature did not exist, wrote its replies to a
reviewer's notes into the plan body instead, and filed an issue asking for a feature that already shipped
(Charter #138).

## What Charter is

Charter is the authoring + review step whose approved deliverable **Guardrails** breaks down into a
task DAG. It combines Lavish's comment-in-place review loop with Builder.io visual-plan's MDX block
authoring, reimplemented in C#. Charter's own format is not MDX: a `.charter.md` is CommonMark plus
`:::` directive blocks, specified by the bundled `charter-format` skill. See `README.md`.

## Status

Shipping on all channels. The block model, the renderer, the loopback review server, the in-place
annotation loop with answers folded back into the plan, git-mediated team review with in-thread replies,
offline export, unattended `headless` runs, the `convert` / `recap` seeds, and the Guardrails handoff are
all implemented and released.

Keep this file current — it is what the next agent will believe about this repo. The facts here that a
test can check (the target framework, the source projects) are checked by `AgentsGuidanceTests`; the rest
is on you.
