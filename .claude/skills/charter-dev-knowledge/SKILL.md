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
    HeadlessRecord.cs / PlanWalk.cs  # the `headless` forensic record (source map + questions + notes); one joined walk
    QuestionIdentity.cs         # the :::question DECLARED-SHAPE fingerprint (an answer's "anchor", #75/3)
  Charter.Cli/                  # `charter` dotnet tool + native binary (Exe; System.CommandLine + Spectre.Console)
    ReviewExitCodes.cs          # the 0/2/3/4/5 contract shared by `poll` and `resolve` — SSOT
    HeadlessExitCodes.cs        # the SEPARATE 0/2 contract for `headless` — NOT the drain vocabulary
    HeadlessCommand.cs          # `headless` = ArtifactExporter + HeadlessRecord + the derived-path convention
    CharterVersion.cs           # the version SSOT (informational version, +build stripped)
  Charter.Server/               # loopback review server + annotation API; embeds ../../sdk/charter-annotate.js
    AnchorResolution.cs         # the ONE drain-time anchor→line kernel
    GitCommand.cs / GitTracking.cs   # the ONLY git shell-out — READ-only; `ls-files` decides "is .review/ tracked"
    ReviewSidecar.cs / StaleAnnotationQueue.cs  # durability sidecar (schema 2) + the #67 replaced-plan quarantine
    ReviewLog*.cs               # all review-log I/O: writer, store, ledger, server-less drain, panel view
sdk/charter-annotate.js         # the ONLY browser JS (annotation SDK, adapted from Lavish, MIT); serve-time only
tests/
  Charter.Core.Tests/           # xunit (net8.0) — renderer/exporter/format golden + security tests
  Charter.Server.Tests/         # xunit — loopback serve, annotation/answer API, sidecar, served-doc-shell guard
  Charter.Cli.Tests/            # xunit — CLI process + poll/resolve + skills + solo-footprint
  Charter.Browser.Tests/        # xunit + Microsoft.Playwright (Chromium) — headless review-loop acceptance (#8)
docs/plans/                     # the plan-of-record (SSOT for design)
skills/                         # the SHIPPED skills (`charter`, `charter-format`) installed by `charter skills install`
install.sh / install.ps1        # SDK-free binary installers
.github/workflows/              # ci.yml (Playwright chromium install step), release.yml, bump-tap.yml
```

TFM `net8.0`; `TreatWarningsAsErrors=true`. Deterministic locked restore (`packages.lock.json`) is deferred
until the dependency set is real — add it the Guardrails way when ready.

**Render contract (SSOT `CharterDocument`):** `CharterRenderer.Render` = `CharterDocument.Wrap(RenderBody(md),
cspMeta: null)`. `render`, `review`, and `export` all wrap the same `RenderBody` output in the same shell
(doctype/html/head/body + one inline `<style>`); only `export` stamps a CSP meta, and the review server
supplies the served-page CSP as an HTTP header. **Never re-add a bare-fragment render path** (#38).
`headless` writes the **exporter's** bytes, not its own render — `HeadlessCommandTests
.Headless_Artifact_IsByteIdenticalToTheExportVerbsArtifact` goes red the day a second render path appears.

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

## Testing lessons (the expensive ones)

### 1. The browser-test blind spot

C#-string golden tests over rendered markup were **blind to four shipped defects**, every one of which a human
hit immediately:

| Defect | What the golden tests saw |
|---|---|
| #37 — an un-interpolated JS template literal leaked into the served HTML, tearing the Mermaid script apart | a string containing the expected substrings |
| #38 — the served page had no doctype, no `<head>`, and zero CSS | a valid fragment |
| #57 — a `:::question` form had **no submit control**, so a human could not answer anything | inputs present, form present |
| #68 / #87 — five block types clipped their content with no way to reach it (a `:::diff` could not scroll on any platform) | markup unchanged |

Worse, one golden **actively protected** a defect.
`AnsweredQuestionRenderTests.Render_OpenQuestion_EmitsByteIdenticalMarkupToThePreFixRenderer` pinned a
`:::question` form **byte for byte** — and that pinned literal contained **no submit button**, because #57 had
not been found yet. The test's own premise was fidelity to the previous renderer, so the harder it locked the
markup down, the more firmly it protected the missing control. A pinned literal only ever asserts *"this has
not changed"*; it can never assert *"this is right."*

**The rule: anything a human sees or clicks needs a browser test that asserts the POSTED PAYLOAD, not just the
DOM.** #57 survived a green browser suite because the test called `form.requestSubmit()` — which works with no
submit button. Click the real control; assert what reached the server.

That pattern has now cost three defects, and the last two were **named as intent**:
`RoundTrip_UnknownKind_DrainsAsElement_NoRegression` pinned #79's silent kind-coercion as a feature, and
`PendingList_KeepsTheSubmitTimeLine_ForTheInPageReviewPanel` pinned #78's panel/drain divergence as a
deliberate design. Both had to be **rewritten, not extended**, when the behaviour was recognised as a bug.
**A test whose name asserts leniency or "no regression" around a degraded value is a place to look for a
defect**, not evidence of one being absent — ask what the lenient branch does when the client is wrong.

### 2. Falsification is the norm here

Every recent fix was proved **RED-then-GREEN**, or by deliberate mutation (delete the guard, watch the test
fail, restore it). **A test that has never been seen to fail is not yet evidence** — it is a hypothesis about
your own code. Show the failure before you claim the fix. `d347471`'s browser test was accepted on exactly
that basis: "fails against the pre-fix code, passes after."

### 3. The stale-binary trap (#69)

`charter --version` warns when installed **skills** lag the tool (`SkillDriftCheck` compares each installed
`SKILL.md`'s `metadata.charter-version` stamp against `CharterVersion.Current`). **Nothing warns when the tool
lags the repo.** An automated pass once greped a page rendered by an old binary against expectations read from
new source and filed a **fixed** bug as live (#69, a duplicate of the already-shipped #57) — with confident
Playwright evidence attached.

**Before diagnosing anything from a rendered page: check `charter --version` against the repo's
`<Version>` in `src/Charter.Cli/Charter.Cli.csproj`.** If they differ, you are reading two different programs.
Prefer `dotnet run --project src/Charter.Cli -- …` over the installed tool when investigating; after a tool
update run `charter skills install --force` too.

### 4. Browser-test traps — Playwright, and the assets the tests scan

- **The served page's CSP refuses `WaitForFunctionAsync` once it has to POLL.** Playwright's polling loop
  `eval`s its predicate *inside* the page, and the served CSP is `script-src 'unsafe-inline'` with no
  `'unsafe-eval'` (`EvalError: Refused to evaluate a string as JavaScript`). It *appears* to work whenever the
  condition is already true on the first check, then fails the moment a test genuinely waits. Use
  `WaitForSelectorAsync` (the selector engine is CSP-safe) or a bounded C# poll over `EvaluateAsync`
  (`ReviewLoopBrowserTests.WaitForEventAsync`). `EvaluateAsync` itself is fine — it goes over CDP, not `eval`.
- **Playwright passes `--hide-scrollbars` to headless Chromium by DEFAULT**, forcing every scrollbar to 0
  width. A test measuring a scroll affordance measures the *flag*, not the stylesheet — it passes while proving
  nothing. Opt out per-launch with `options.IgnoreDefaultArgs = new[] { "--hide-scrollbars" }`
  (`TryLaunchAsync(showScrollbars: true)`), and **only** for the tests that need it, so no existing layout
  assertion shifts. **Five** tests do (`grep "showScrollbars: true"`) — #68's
  `Wide_table_scrolls_in_its_wrapper_…`, #87's `Every_clipping_block_scrolls_and_is_keyboard_reachable_…` and
  `Diff_line_annotation_still_posts_that_lines_own_sub_anchor_…`, and #5's
  `Every_block_type_is_reachable_when_it_clips_…` / `Nothing_overlaps_or_occludes_…`. The two `Every_*` sweeps
  **prove** the opt-out instead of asserting it in prose: each compares the reference region's *declared*
  `::-webkit-scrollbar` height (10px, from `charter.css`) against its *laid-out* gutter, which can only agree
  when a real bar exists. Copy that check into any new scrollbar test — and note the same trap governs any
  `scrollWidth`/`clientWidth`/`offsetWidth` delta.
- **Navigation timeout is set ONCE, on the context, by a single factory.** `NewContextAsync(browser)` applies
  `SetDefaultNavigationTimeout(90_000)`; a test calling `browser.NewContextAsync()` directly bypasses it and
  can reintroduce the #66 flake on a contended `windows-latest` runner. Only *navigation* is relaxed — every
  assertion keeps its own tight deadline, so a genuine hang still fails.
- **A whole-body string scan over rendered output measures the 3.5 MB VENDORED MERMAID BLOB, not Charter.**
  `CharterRenderer.RenderBody` appends the inlined library whenever the plan has a `:::diagram`, and that
  minified build contains plenty of ordinary English tokens — `tabindex` among them. A
  `DoesNotContain("tabindex", body)` guard therefore fails on arrival and *looks* like a real defect. Scope
  the assertion to the element's own opening tag (the `WrapperTag` / `DiagramPreTag` regex shape) or strip
  `<script>…</script>` first (the `ServedDocumentShellTests` shape). Only scan whole-document for markers you
  have checked do **not** occur in `assets/mermaid.min.js`.
- **`setPointerCapture` re-targets the compatibility `click`.** A drag gesture that captures the pointer on
  a container makes the click Chromium synthesizes afterwards land on the *container*, not on the element
  under the cursor. In `:::diagram` that silently downgrades a `diagram-node` annotation to a whole-block
  one — Charter #48 through the back door, with no error anywhere. Track a drag with document-level
  `pointermove`/`pointerup` listeners instead. (Falsified: adding the capture call turns
  `Diagram_node_and_background_still_anchor_to_the_block_after_a_zoom_and_a_pan` red.)
- **`assets/charter.css` is heavily commented, and its comments NAME the selectors and properties the tests
  search for** — so a raw-text scan finds prose and reports it as a declaration. It cost a red run: the comment
  above the scrollbar rules explains why `scrollbar-width`/`scrollbar-color` must live in the
  `@supports not selector(::-webkit-scrollbar)` branch, and it sits *before* that branch, so
  `Stylesheet_TheFirefoxScrollbarBranchStaysMutuallyExclusive`'s "every occurrence is inside the branch" scan
  failed on the *explanation*. `ClipAffordanceTests.Rules()` strips `/* … */` first
  (`Regex.Replace(CharterStyles.Css, @"/\*.*?\*/", "", RegexOptions.Singleline)`); anything scanning that
  stylesheet — or the artifact's inline `<style>` — must too.
- **Wait on selectors/events, not network-idle.** The served page holds an open SSE `/events` stream, so
  `WaitUntil = NetworkIdle` never settles. Use `WaitUntilState.Load` + a selector/event wait. The suite
  SKIPS cleanly (`Xunit.SkippableFact`) when Chromium is unavailable; the deterministic served-doc-shell
  guards (Core + Server tests) cover the same symptoms on every OS.

## Conventions & gotchas (hard-won)

- **Classic `.sln`, not `.slnx`.** CI uses `setup-dotnet 8.0.x`, which cannot read `.slnx`.
- **Apphost rename, not AssemblyName.** Rename the published `Charter.Cli` binary to `charter` in the
  workflow; never set a global `-p:AssemblyName` (it renames every project and collides on publish —
  NETSDK1152).
- **LF-pinned installer.** `/install.sh` is `eol=lf` in `.gitattributes` so its shebang runs on macOS/Linux
  from a Windows (autocrlf) checkout. Review logs are pinned `eol=lf` for the same class of reason — a
  CRLF-writing teammate would fork every line under a union merge.
- **Git with spaces / `git -C`.** Always `git -C <repo>`; assume paths contain spaces.
- **Portability seam.** The renderer emits a standalone artifact; the annotation SDK is injected only at serve
  time — never write it into the saved file.
- **Watch the file, not the tree,** for live reload (`FileSystemWatcher`), or a large parent directory
  saturates the event loop (Lavish's lesson).
- **Inline-JS must be script-parse-safe.** A big minified lib inlined between `<script>…</script>` can carry
  `<!--` / `<script` / `</script` (even inside string/regex literals) that flip the browser's script-data
  tokenizer, tearing the script apart — the lib's tail dumps as visible text and its `<iframe>` template
  literal materializes as a real (CSP-blocked) element, so the lib never defines (#37). Escape those three
  sequences when inlining (`CharterRenderer.MermaidRuntimeMarkup`), and init Mermaid with
  `securityLevel: 'antiscript'` so it renders inline SVG (no sandboxed iframe / `frame-src`) under the strict
  CSP.
- **A rendered `:::diagram`'s review-time pan/zoom lives ONLY in `sdk/charter-annotate.js`** (#51). It
  widens the `<svg>` (`width: base × scale; max-width: none`) and lets the `<pre>` be a scroll container —
  never a CSS transform, which would rasterize the labels the feature exists to make readable and move every
  rect the annotation overlay is painted from. `charter.css`, `CharterRenderer` and `ArtifactExporter` are
  deliberately untouched, so the exported artifact stays byte-identical; if you find yourself adding a
  `.charter-zoom*` rule to `assets/charter.css`, you have just broken invariant 1 and
  `DiagramPanZoomArtifactTests` will say so. Chrome absolutely positioned inside a scroll container must be
  pushed back by `scrollLeft`/`scrollTop` or it rides away with the content.
- **Inside a rendered `:::diagram`, only `pre.mermaid` carries a Charter id.** Mermaid stamps its own ids on
  the `<svg>` and every `g.node`, so a generic "nearest ancestor with an `id`" walk stops on one of those
  unless short-circuited — which is how a diagram-node note reached the agent with no `sourceLine` (#48).
  `closestAnchored` resolves `pre.mermaid` explicitly, **at the anchoring layer**; route any new anchoring
  path through it rather than re-walking. Mermaid's theme CSS also rides in a `<style>` **inside** the `<svg>`,
  so a text-derived label reads as a stylesheet unless `style`/`script` are skipped — and note an SVG
  element's `tagName` keeps its lower-case local name where an HTML element's is upper-cased.
- **Blink dispatches NO `click` when Space activates an ALREADY-CHECKED radio**
  (`RadioInputType::HandleKeyupEvent` returns early), so a click-based rule is unreachable from the keyboard;
  handle `keyup` instead — and `preventDefault()` there, because Blink re-reads `checked` *after* the listener
  runs and will re-check a control the listener just cleared (#63).
- **Charter reads git; it never writes git.** `GitCommand.Read` is the single shell-out, 5s timeout, and every
  failure (git absent, not a repo, hung, sandboxed) returns `null` so the caller degrades to the solo-safe
  answer. Never add a mutating git call.
- **`.review/` is created lazily, on the first append** — not in `ReviewLogWriter`'s constructor. A `charter
  review` that writes no comment must leave no trace beside the plan (plan-03 §5.0). Do not reintroduce an
  eager `EnsureDirectory`; `SoloReviewFootprintTests` and `SoloReviewPathTests` guard it.

## Packaging & distribution

- **NuGet dotnet tool:** `PackageId ServantSoftware.Charter`, `ToolCommandName charter`. Publish is opt-in via
  repo variable `PUBLISH_NUGET=true` + NuGet Trusted Publishing (OIDC) + a `NUGET_USER` secret.
- **Native binaries (no .NET runtime for consumers):** `release.yml` builds self-contained single-file
  binaries for 5 RIDs on a `v*` tag, renames the apphost `Charter.Cli` → `charter` **post-publish**,
  smoke-runs `charter --version`, and uploads archives + `.sha256`.
- **Homebrew:** `bump-tap.yml` regenerates `charter.rb` from `.github/templates/charter.rb.tmpl` and opens a PR
  to `Servant-Software-LLC/homebrew-tap` (needs org secret `TAP_PAT`). Triggered by `workflow_run` of
  "Release" — a GITHUB_TOKEN-created release does not emit `release:published`.
- **macOS codesign/notarize:** a gated step in `release.yml`, auto-skips until the six `MACOS_*` secrets exist.
- **Dry-run:** a `v0.0.0-ci.N` tag exercises binaries + tap without touching NuGet (the `-ci.` guard skips the
  publish job).
- **The version SSOT is `<Version>` in `src/Charter.Cli/Charter.Cli.csproj`**, surfaced through
  `CharterVersion.Current` (never `AssemblyVersion`). `charter skills install` stamps it into each installed
  `SKILL.md`; do **not** hand-write a version into a bundled `skills/**/SKILL.md`.

## Status pointers

- Design of record / roadmap: `docs/plans/` — `01-combine-lavish-and-visual-plan.md` (architecture, D1/D2),
  `02-architecture-b-living-document.md` (dual handoff), `03-git-mediated-team-review.md` (per-author JSONL
  logs, the fold rules, §5.0 solo primacy; **normative**, and its §9 build order says which steps exist).
- Distribution + CI: `.github/workflows/`, mirrored from Guardrails' validated pipeline.
- **Release state and the master test baseline are single-sourced in `charter-domain-knowledge`'s Status
  block** — don't restate them here. The dev-side consequence is §3's trap sharpened: `v0.7.0` is **published**
  while `<Version>` still reads `0.7.0`, so a local build reports a version already on NuGet and "csproj agrees
  with `charter --version`" proves nothing about whether your binary matches the repo. Bump `<Version>` before
  the next tag.
- Product model, review-loop semantics, solo primacy, and the agent-facing consumption contract (poll exit
  codes, `drainError`, `reviewSubmitted`, `anchorStatus`): skill `charter-domain-knowledge`.
