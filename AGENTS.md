# AGENTS.md

Guidance for coding agents working in the Charter repository.

## Commands

```sh
dotnet build Charter.sln -c Release
dotnet test  Charter.sln -c Release
dotnet run   --project src/Charter.Cli -- --version
```

## Project conventions

- .NET 8, C#, nullable + implicit usings enabled, warnings-as-errors (see `Directory.Build.props`).
- `src/Charter.Cli` is the CLI (System.CommandLine + Spectre.Console); `src/Charter.Core` holds the
  domain model; tests live in `tests/`.
- Distribution mirrors Guardrails: a NuGet `dotnet tool` plus native self-contained binaries shipped
  via a Homebrew tap and SDK-free installers (see `.github/workflows/release.yml` and `install.sh`).
- The published binary is renamed from `Charter.Cli` to `charter` in the release workflow — do not
  set a global `-p:AssemblyName`, which would also rename `Charter.Core` and collide on publish.

## What Charter is

Charter is the authoring + review step whose approved deliverable **Guardrails** breaks down into a
task DAG. It combines Lavish's comment-in-place review loop with Builder.io visual-plan's MDX block
authoring, reimplemented in C#. See `README.md`.

## Status

Scaffold. The MDX block model, renderer, local review server, and annotation loop are the next
milestones. Keep this file current as the architecture lands.
