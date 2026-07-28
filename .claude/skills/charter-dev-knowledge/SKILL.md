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
  Charter.Core/                 # renderer, block catalog, session model, exporter, shared doc shell (net8.0 lib)
    assets/mermaid.min.js       # vendored Mermaid v11.16.0 (MIT), embedded → Charter.Core.mermaid.min.js
    assets/charter.css          # bundled stylesheet, embedded → Charter.Core.charter.css (CharterStyles/CharterDocument)
    ReviewLog*.cs               # the PURE review-record fold (schema + the 8 order-independent rules)
  Charter.Cli/                  # `charter` dotnet tool + native binary (Exe; System.CommandLine + Spectre.Console)
    ReviewExitCodes.cs          # the 0/2/3/4/5 contract shared by `poll` and `resolve` — SSOT
  Charter.Server/               # loopback review server + annotation API; embeds ../../sdk/charter-annotate.js
    ReviewLog*.cs               # all the review-log I/O: writer, store, ledger, server-less drain, panel view
sdk/charter-annotate.js         # the ONLY browser JS (annotation SDK, adapted from Lavish, MIT); serve-time only
tests/
  Charter.Core.Tests/           # xunit (net8.0) — renderer/exporter/format golden + security tests
  Charter.Server.Tests/         # xunit — loopback serve, annotation/answer API, sidecar, served-doc-shell guard
  Charter.Cli.Tests/            # xunit — CLI process + poll/resolve + skills
  Charter.Browser.Tests/        # xunit + Microsoft.Playwright (Chromium) — headless review-loop acceptance (#8)
docs/plans/                     # the plan-of-record (SSOT for design)
install.sh / install.ps1        # SDK-free binary installers
.github/workflows/              # ci.yml (Playwright chromium install step), release.yml, bump-tap.yml
.github/templates/charter.rb.tmpl, .github/macos/entitlements.plist
```

TFM `net8.0`; `TreatWarningsAsErrors=true`. Deterministic locked restore (`packages.lock.json`) is
deferred until the dependency set is real — add it the Guardrails way when ready.

**Render contract (SSOT `CharterDocument`):** the renderer emits a COMPLETE, STYLED HTML document —
`CharterRenderer.Render` = `CharterDocument.Wrap(RenderBody(md), cspMeta: null)`. `render`, `review`, and
`export` all wrap the same `RenderBody` output in the same shell (doctype/html/head/body + one inline
`<style>` from `assets/charter.css`); only `export` stamps a CSP meta (its strict offline policy), and the
review server supplies the served-page CSP as an HTTP header. Never re-add a bare-fragment render path.

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
# browser acceptance test (Charter.Browser.Tests) needs Chromium installed once, after build:
pwsh tests/Charter.Browser.Tests/bin/Release/net8.0/playwright.ps1 install --with-deps chromium
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
- **Inline-JS must be script-parse-safe.** A big minified lib inlined between `<script>…</script>` can carry
  `<!--` / `<script` / `</script` (even inside string/regex literals) that flip the browser's script-data
  tokenizer, tearing the script apart — the lib's tail dumps as visible text and its `<iframe>` template
  literal materializes as a real (CSP-blocked) element, so the lib never defines (was Charter #37). Escape
  those three sequences (`<\!--` etc.) when inlining. Charter does this to the Mermaid runtime in
  `CharterRenderer.MermaidRuntimeMarkup`, and inits Mermaid with `securityLevel: 'antiscript'` so it renders
  inline SVG (no sandboxed iframe/`frame-src`) under the strict CSP. Renderer C#-string golden tests are BLIND
  to this — only the Playwright browser test catches it.
- **Browser test: wait on selectors, not network-idle.** The served page holds an open SSE `/events` stream,
  so `WaitUntil = NetworkIdle` never settles — use `WaitUntilState.Load` + `WaitForSelector`/`WaitForFunction`.
  The test SKIPS cleanly (Xunit.SkippableFact) when Chromium is unavailable; the deterministic served-doc-shell
  guards (Core + Server tests) cover the same symptoms on every OS.
- **Playwright passes `--hide-scrollbars` to headless Chromium by DEFAULT, forcing every scrollbar to 0
  width.** So a test that measures a scroll affordance (Charter #68) measures the *flag*, not the
  stylesheet — it passes while proving nothing (passes-but-blind). Opt out per-launch with
  `options.IgnoreDefaultArgs = new[] { "--hide-scrollbars" }`
  (`ReviewLoopBrowserTests.TryLaunchAsync(showScrollbars: true)`), and **only** for the tests that need it,
  so no existing layout assertion shifts. The same trap applies to any `scrollWidth`/`clientWidth`/
  `offsetWidth` delta or scrollbar-gutter assertion.
- **`page.WaitForFunctionAsync` is unusable on the served page once it has to POLL.** Playwright's polling
  loop `eval`s its predicate inside the page, and the served-page CSP is `script-src 'unsafe-inline'` with no
  `'unsafe-eval'` — so the browser refuses it (`EvalError: Refused to evaluate a string as JavaScript`). It
  *appears* to work whenever the condition is already true on the first check (which is why the `ready` wait
  survived), then fails the moment a test genuinely waits. Use `WaitForSelectorAsync` (the selector engine is
  CSP-safe) or a bounded C# poll over `EvaluateAsync` (`ReviewLoopBrowserTests.WaitForEventAsync`). `evaluate`
  itself is fine — it goes over CDP, not `eval`.
- **Inside a rendered `:::diagram`, only `pre.mermaid` carries a Charter id.** Mermaid stamps its own ids on
  the `<svg>` and on every `g.node`, so the SDK's generic "nearest ancestor with an `id`" walk stops on one
  of those unless it is short-circuited — which is exactly how a diagram-node note reached the agent with no
  `sourceLine` (#48). `closestAnchored` resolves `pre.mermaid` explicitly; keep any new anchoring path going
  through it rather than re-walking. Mermaid's theme CSS also rides in a `<style>` **inside** the `<svg>`, so
  a "what am I annotating" label built from text nodes reads as a stylesheet unless `style`/`script` are
  skipped — and note an SVG element's `tagName` is lower-case where an HTML element's is upper-case.
- **Blink dispatches NO `click` when Space activates an ALREADY-CHECKED radio**
  (`RadioInputType::HandleKeyupEvent` returns early), so a click-based rule is unreachable from the keyboard;
  handle `keyup` instead — and `preventDefault()` there, because Blink re-reads `checked` *after* the
  listener runs and will re-check a control the listener just cleared (#63).

## Status pointers

- Design of record / roadmap: `docs/plans/` — `01-combine-lavish-and-visual-plan.md` (architecture,
  decisions D1/D2), `02-architecture-b-living-document.md` (dual handoff), and
  `03-git-mediated-team-review.md` (per-author JSONL logs, the fold rules; **normative**, and its §9 build
  order says which steps exist — 1–4 are built, 5–7 are not).
- Distribution + CI: `.github/workflows/`, mirrored from Guardrails' validated pipeline.
- Current state: **shipping — v0.6.0**. Renderer, source-map, loopback review server, annotation loop,
  in-page review panel, answerable `:::question` forms, the **Send to agent** round hand-off, offline
  export, the living-document `--apply`/`resolve` fold, and team-review steps 2–4 are all built. Baseline
  on a clean tree: **623 tests green, 0 warnings** (`dotnet test Charter.sln -c Release`) —
  Core 355 · Server 196 · Cli 57 · Browser 15.
- Product model, review-loop semantics, and the agent-facing consumption contract (poll exit codes,
  `drainError`, `reviewSubmitted`): skill `charter-domain-knowledge`.
