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
global.json                     # pins the .NET 10 SDK band (10.0.100, rollForward latestFeature)
Directory.Build.props           # ImplicitUsings, Nullable, TreatWarningsAsErrors, AnalysisLevel 10.0
src/
  Charter.Core/                 # renderer, block catalog, session model, exporter, shared doc shell (net10.0 lib)
    assets/mermaid.min.js       # vendored Mermaid v11.16.0 (MIT), embedded → Charter.Core.mermaid.min.js
    assets/charter.css          # bundled stylesheet, embedded → Charter.Core.charter.css (CharterStyles/CharterDocument)
    ReviewLog*.cs               # the PURE review-record fold (schema + the 8 order-independent rules)
    HeadlessRecord.cs / PlanWalk.cs  # the `headless` forensic record (source map + questions + notes); one joined walk
    QuestionIdentity.cs         # the :::question DECLARED-SHAPE fingerprint (an answer's "anchor", #75/3)
    AnswerRules.cs              # the ONE answer semantics: IsDecision (#188) + Merge/Check for --answers (#186)
    HandoffManifest.cs          # `handoff --manifest`'s chain-of-custody file (schema 1) — its OWN artifact (#187)
    HandoffAnswers.cs           # a --answers file's values AND the hash of the text they came from (one parse)
    BareFileName.cs             # the ONE no-local-path rule, shared by the record and the manifest
    PlanHash.cs                 # the ONE hash recipe: sha256 of the DECODED text re-encoded UTF-8 (NOT sha256sum)
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
    ReviewLogWatch.cs           # the two-stage `.review/` watch behind /events + its keep-alive re-check (#88)
sdk/charter-annotate.js         # the ONLY browser JS (annotation SDK, adapted from Lavish, MIT); serve-time only
tests/
  Charter.Core.Tests/           # xunit (net10.0) — renderer/exporter/format golden + security tests
  Charter.Server.Tests/         # xunit — loopback serve, annotation/answer API, sidecar, served-doc-shell guard
  Charter.Cli.Tests/            # xunit — CLI process + poll/resolve + skills + solo-footprint
  Charter.Browser.Tests/        # xunit + Microsoft.Playwright (Chromium) — headless review-loop acceptance (#8)
docs/plans/                     # the plan-of-record (SSOT for design)
skills/                         # the SHIPPED skills (`charter`, `charter-format`) installed by `charter skills install`
install.sh / install.ps1        # SDK-free binary installers
.github/workflows/              # ci.yml (Playwright chromium install step), release.yml, bump-tap.yml
```

TFM `net10.0`; `TreatWarningsAsErrors=true`. Deterministic locked restore (`packages.lock.json`) is deferred
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
pwsh tests/Charter.Browser.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium
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
- **`WaitForEventAsync` asks "has this EVER happened", so the SECOND one in a test returns instantly.** It
  polls for `count > 0` over a tap that accumulates for the whole page life, so `Click(save);
  WaitForEventAsync("submitted")` is a real wait the first time and a no-op every time after — every assertion
  following the second save then races the render. It surfaced as `Selecting_a_note_jumps_to_its_anchor…`
  finding one card where it expected two, on **WebKit, under full-suite load only**, on a branch that had not
  touched that path. Use `SaveComposerAsync` (captures the count, clicks, waits for +1) or
  `WaitForEventCountAsync` directly; `WaitForEventAsync` is safe only for an event that can happen once per
  page. Four tests had two `submitted` waits and all four were latently racy.
- **`document.elementFromPoint` is VIEWPORT-relative, and `scrollIntoView` scrolls EVERY scrollable ancestor.**
  Both halves bite in the same test. A badge below the fold hit-tests as a miss however correct it is, so a
  probe has to bring it into view first — but doing that *between* two readings of a scrolled block is what
  hides the defect, because centring a badge that lives inside a `<pre>` scrolls the `<pre>` back to column
  one. Scroll once, before either reading, and never again (`MarkerAffordanceTests`). Same family as
  `ClickAsync`'s `scrollIntoViewIfNeeded`.
- **A `box-shadow` marker has no element to query, so assert WHERE IT IS PAINTED.** An INSET shadow paints on
  the element's own background layer, underneath every descendant; an OUTER one is clipped to outside the
  border box. That difference is the whole of #167's second half and it is measurable without pixels: derive
  the bar's column from the computed shadow plus the box's rect, then check whether any descendant that paints
  a background or a border overlaps it. A pixel diff would have needed a PNG decoder and would have said less.

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
  saturates the event loop (Lavish's lesson). `PlanWatch` is that watch, and it is a `FileSystemWatcher` on the
  plan's DIRECTORY filtered to the one file name — so the handle survives the file's own round trips (an editor
  saving by replace-and-rename, `git checkout -- plan.charter.md`) and dies only when the DIRECTORY goes.
- **Both `/events` watches are best-effort, so both have the same keep-alive net (#88 `.review/`, #92 the plan
  file).** `PlanWatch.Poll()` and `ReviewLogWatch.Poll()` are called on the one beat (`ReviewServer.Beat`, 15s)
  — never a second timer, never per-request work. Four rules the plan-file half is built on: (1) the change
  signal is the file's **length + last-write time**, which is exactly the pair the watcher's own `NotifyFilter`
  (`Size | LastWrite | FileName`) is built from, so the net's blind spot is a *subset* of the fast path's — a
  content hash per beat was rejected as reading the whole plan forever to buy a case the watcher misses anyway;
  (2) the beat **re-arms whenever it finds a revision the watcher never reported**, since that is direct
  evidence the handle is untrustworthy, and it is the only re-arm trigger that costs nothing on a quiet stream
  (a quiet beat is ONE `FileInfo` stat, and `Directory.Exists` is consulted only when the plan is missing);
  (3) **re-arm, THEN announce**, in every path, including the watcher callback — which re-bases the beat's
  baseline *before* it notifies, or the next beat re-reports the same revision and the page navigates twice;
  (4) a **missing plan file is silent** — no frame at all, last stamp held — because `reload` is a full
  navigation and the reviewer is mid-`git checkout`. Pre-#92 this was a bare watcher armed once at SSE connect
  with no fallback: one dropped notification ended live reload for the whole life of that connection.
  `ReviewServerOptions.EventStreamBeat` is an **internal** test seam (same shape as `StartCore`'s injected port
  supplier) so a stream-level test can prove a beat's report reaches the client in under a second.
- **`FileSystemWatcher` is best-effort, and a check-then-arm chain has a window (#88).** `.review/` is created
  lazily (§5.0), so `ReviewLogWatch` is a TWO-STAGE watch: a directory-name bridge on the plan's own directory,
  then the inner `*.jsonl` watch. It used to look for the directory first and arm the bridge *only if it was
  missing* — and a `.review/` created in the gap between that check and the bridge going live was seen by
  neither, so the stream stayed **blind for its whole life** (the watch arms once, at SSE connect) and a
  teammate's pulled log silently never refreshed the panel. Fix: **arm the bridge FIRST, unconditionally**, then
  look — every ordering is then covered by one of the two arms. Second rule from the same bug: the watcher is
  bound **by handle**, so a `.review/` removed and restored (a branch switch) leaves a watcher that can never
  fire again — `OnArrived` replaces it rather than trusting a `Directory.Exists` that is true again by then.
  Third: the `/events` keep-alive beat calls `ReviewLogWatch.Poll()` as a bounded safety net (one directory
  stat per beat; a single `Directory.Exists` when there is no `.review/`), because the OS drops notifications
  under buffer pressure and delivers none at all on some filesystems. Fourth, and it cost a second red CI:
  **never assert an arm by waiting on a notification.** `Directory.Delete` raises its own `Deleted` events for
  the files it removes, and the OS delivers them on its own schedule — measured at 3–4 in 40 arriving *after*
  the test had already reset its event. A test that waits for "any callback" latches onto one of those and
  then reads `IsArmed` an instant too early (`windows-latest`, 13 ms, deterministic loss of a race). Read the
  arm state **inside** the callback instead: it asserts the real contract — *re-arm, THEN announce* — and is
  the only way to tell the arrival apart from the removal's own noise. That noise is **wanted**, by the way:
  the comments really did vanish, so a re-read then is correct — do not "fix" it by suppressing events from a
  watcher you have replaced.
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
- **A generic "nearest ancestor with an `id`" walk is WRONG, and there is exactly one place that knows why.**
  Two regions in a rendered plan hold markup Charter did not author — `pre.mermaid` (Mermaid stamps ids on the
  `<svg>` and every `g.node`) and `div.custom-html-scroll` (an author's verbatim body, ids and all) — and
  neither kind of id exists in `SourceMap`, so a walk that accepts one hands the agent a note with no
  `sourceLine` (#48) or, for a duplicated author id, a note pointing at the wrong block (#166).
  `sdk/charter-annotate.js` states the rule ONCE as a containment predicate (`isAnchorElement` /
  `insideOpaqueRegion`) used by BOTH the write path (`closestAnchored`) and the read path (`anchorElement`);
  route any new anchoring path through those rather than re-walking, and never re-add a per-region
  short-circuit — the one that used to exist for #48 returned `pre.mermaid` unconditionally and thereby made a
  NESTED diagram silently un-annotatable. Mermaid's theme CSS also rides in a `<style>` **inside** the
  `<svg>`, so a text-derived label reads as a stylesheet unless `style`/`script` are skipped — and note an SVG
  element's `tagName` keeps its lower-case local name where an HTML element's is upper-cased.
- **`RenderBody`'s anchor pass iterates TOP-LEVEL nodes only** (`foreach (var node in document)`), so a
  container nested inside another — a `:::custom-html` or `:::diagram` inside a `::::note` or a list item —
  renders with **no id**. Anything walking the DOM for an anchor must treat that as "keep climbing", never as
  "here is the anchor" (which yields `anchorId: null`) and never as "no anchor". Two more consequences worth
  knowing before you file a bug: `hasDiagram` is computed from the same top-level loop, so a nested
  `:::diagram` never gets the Mermaid runtime inlined and renders as its own source text; and a nested block
  has no `SourceMap` entry of its own, so the enclosing block's line is the honest answer for it.
- **Blink dispatches NO `click` when Space activates an ALREADY-CHECKED radio**
  (`RadioInputType::HandleKeyupEvent` returns early), so a click-based rule is unreachable from the keyboard;
  handle `keyup` instead — and `preventDefault()` there, because Blink re-reads `checked` *after* the listener
  runs and will re-check a control the listener just cleared (#63).
- **Charter reads git; it never writes git.** `GitCommand.Read` (server) and `GitWorkingTree` (CLI, #154/#194)
  are the only shell-outs, and every failure (git absent, not a repo, hung, sandboxed) returns the "no" answer
  so the caller degrades to the solo-safe path. Never add a mutating git call — **and that includes writing a
  `.gitignore`.** `skills install` only ADVISES when its target resolves inside a work tree (#194); it never
  lays ignore rules. Two independent reasons, both worth keeping: a tool that edits ignore rules in a repo the
  user never named — reached only through a symlink from `$HOME` — is worse than one that says nothing; and a
  `.gitignore` in a SUBDIRECTORY overrides a negation in the repo root, so the tempting self-ignoring `*` in
  each skill folder makes `skills install --project` stop delivering the skill at all against the common
  `.claude/*` + `!.claude/skills/` convention — and the symptom of an over-broad ignore rule is that nothing
  appears. `SkillsInstallGitAdvisoryTests` pins that experiment with a real repo. If an opt-in `--gitignore`
  is ever added, the ROOT `.gitignore` is the only safe place to write.
- **`git rev-parse` resolves the process working directory to its PHYSICAL path**, so it sees straight through
  a symlink or a Windows junction — which is the whole of #194, where `~/.claude/skills` was a symlink into a
  dotfiles repo. `GitWorkingTree.LocateWorkTree` takes `--show-toplevel` and `--show-prefix` from ONE call, so
  the repo root AND the target's position under it both come from git; never derive either by reasoning about
  the path string, which is a no-op in exactly the case that matters. Testing this needs a real reparse point:
  Windows refuses `Directory.CreateSymbolicLink` without Developer Mode or elevation, so fall back to a
  **junction** (`cmd /c mklink /J`), which needs neither and which git resolves identically.
- **A CLI help assertion must use SINGLE TOKENS.** System.CommandLine word-wraps help to the console width, so
  a multi-word phrase can split across lines on one agent and not another — and a `DoesNotContain` over a
  phrase then fails in the dangerous direction, passing because the old text *wrapped* rather than because it
  is gone. `HandoffManifestTests.HandoffHelp_…` is the shape to copy: assert distinctive single tokens
  (`--manifest`, `plan.manifest.json`, `INVOCATION`), never a sentence.
- **`PlanHash.Sha256Hex` is NOT `sha256sum`.** It hashes the **decoded text re-encoded as UTF-8**, and Charter
  reads files with `File.ReadAllText`, which strips a UTF-8 BOM and decodes UTF-16/32 per the mark. So the
  three hashes in `handoff --manifest` equal `sha256sum` only for a BOM-less UTF-8 file. A test comparing one
  to `SHA256.HashData(File.ReadAllBytes(...))` must therefore control the file's encoding, and a test asserting
  the DIVERGENCE (`HandoffManifestTests.OnAUtf16AnswersFile_…`) is what stops someone "fixing" the recipe.
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
