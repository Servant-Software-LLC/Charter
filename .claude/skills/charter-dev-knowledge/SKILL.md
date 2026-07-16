---
name: charter-dev-knowledge
description: |
  Charter repo development knowledge: solution layout, build/test/run commands,
  dotnet-tool packaging, native-binary distribution, testing conventions and gotchas.
  Use when implementing, testing, running, or packaging Charter, or onboarding an
  agent to the codebase.

  SELF-UPDATING: When your work changes the solution layout, conventions, packaging,
  distribution, or any fact below, you MUST update the affected section(s) before
  completing your task.
---

# Charter Dev Knowledge

## Solution layout

```
Charter.sln                     # classic .sln (NOT .slnx — see gotchas)
global.json                     # pins the .NET 8 SDK band (8.0.100, rollForward latestFeature)
Directory.Build.props           # ImplicitUsings, Nullable, TreatWarningsAsErrors, AnalysisLevel 8.0
src/
  Charter.Core/                 # renderer, block catalog, session model (net8.0 library)
  Charter.Cli/                  # `charter` dotnet tool + native binary (Exe; System.CommandLine + Spectre.Console)
tests/
  Charter.Core.Tests/           # xunit (net8.0)
docs/plans/                     # the plan-of-record (SSOT for design)
install.sh / install.ps1        # SDK-free binary installers
.github/workflows/              # ci.yml, release.yml, bump-tap.yml
.github/templates/charter.rb.tmpl, .github/macos/entitlements.plist
```

TFM `net8.0`; `TreatWarningsAsErrors=true`. Deterministic locked restore (`packages.lock.json`) is
deferred until the dependency set is real — add it the Guardrails way when ready. The future
`Charter.Server` (loopback review server) and an embedded `sdk/` (JS, adapted from Lavish) land per
`docs/plans/`.

## Packaging & distribution

- **NuGet dotnet tool:** `PackageId ServantSoftware.Charter`, `ToolCommandName charter`. Publish is
  opt-in via repo variable `PUBLISH_NUGET=true` + NuGet Trusted Publishing (OIDC) + a `NUGET_USER` secret.
- **Native binaries (no .NET runtime for consumers):** `release.yml` builds self-contained single-file
  binaries for 5 RIDs on a `v*` tag, renames the apphost `Charter.Cli` → `charter` **post-publish**
  (a global `-p:AssemblyName` would rename `Charter.Core` too and collide on publish — NETSDK1152),
  smoke-runs `charter --version`, and uploads archives + `.sha256`.
- **Homebrew:** `bump-tap.yml` regenerates `charter.rb` from `.github/templates/charter.rb.tmpl` and
  opens a PR to `Servant-Software-LLC/homebrew-tap` (needs org secret `TAP_PAT`). Triggered by
  `workflow_run` of "Release" — a GITHUB_TOKEN-created release does not emit `release:published`.
- **macOS codesign/notarize:** a gated step in `release.yml`, auto-skips until the six `MACOS_*`
  secrets exist.
- **Dry-run:** a `v0.0.0-ci.N` tag exercises binaries + tap without touching NuGet (the `-ci.` guard
  skips the publish job).

## Commands

```powershell
dotnet build Charter.sln -c Release
dotnet test  Charter.sln -c Release
dotnet run   --project src/Charter.Cli -- --version
dotnet pack  src/Charter.Cli -c Release -o nupkg -p:Version=0.1.0-preview.1
# native binary (one RID):
dotnet publish src/Charter.Cli -c Release -r osx-arm64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish/osx-arm64
```

## Conventions & gotchas (hard-won)

- **Classic `.sln`, not `.slnx`.** CI uses `setup-dotnet 8.0.x`, which cannot read the newer `.slnx`
  format; keep the classic solution file.
- **Apphost rename, not AssemblyName.** Rename the published `Charter.Cli` binary to `charter` in the
  workflow; never set a global `-p:AssemblyName` (it renames every project and collides on publish).
- **LF-pinned installer.** `/install.sh` is `eol=lf` in `.gitattributes` so its shebang runs on
  macOS/Linux from a Windows (autocrlf) checkout.
- **Git with spaces / `git -C`.** Always `git -C <repo>`; assume paths contain spaces.
- **Portability seam.** The renderer emits a standalone artifact; the annotation SDK is injected only
  at serve time — never write it into the saved file.
- **Watch the file, not the tree,** for live reload (`FileSystemWatcher`), or a large parent directory
  saturates the event loop (Lavish's lesson).

## Status pointers

- Design of record / roadmap: `docs/plans/` (currently `01-combine-lavish-and-visual-plan.md`).
- Distribution + CI: `.github/workflows/`, mirrored from Guardrails' validated pipeline.
- Current state: **scaffold** — renderer, server, and annotation loop are not yet built.
