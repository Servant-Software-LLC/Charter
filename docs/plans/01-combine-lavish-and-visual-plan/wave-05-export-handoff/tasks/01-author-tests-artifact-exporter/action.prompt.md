## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-05-export-handoff/01-author-tests-artifact-exporter": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Charter's `render`/`review` verbs already produce a portable, SDK-free HTML artifact (the annotation SDK
is injected only at serve time by `Charter.Server.SdkInjector`, never by `Charter.Core`) that already
inlines the vendored Mermaid runtime. `export` builds on that but ADDS true offline self-containment: it
inlines local image assets the plan references and scrubs any local filesystem path from the shipped
file. This task authors the **failing tests + minimal stub** for the new component that does that,
`Charter.Core.ArtifactExporter`. Task `02-implement-artifact-exporter` fills in real logic. Do NOT
implement the real behavior yourself.

Read the real materialized surface first (do not trust a remembered shape):
`src/Charter.Core/CharterRenderer.cs` (the `Render(markdown)` entry point `ArtifactExporter` wraps),
`src/Charter.Server/SdkInjector.cs` (the `data-charter-sdk` marker convention — `export` must never emit
it), `tests/Charter.Core.Tests/RendererGoldenTests.cs` (the test-fixture style this repo uses).

**Write the minimal stub** in a NEW file `src/Charter.Core/ArtifactExporter.cs`:

```csharp
namespace Charter.Core;

public static class ArtifactExporter
{
    public static string Export(
        string markdown,
        string planDirectory,
        long maxAssetBytes = 10_485_760,
        long maxTotalAssetBytes = 52_428_800)
        => throw new NotImplementedException();
}
```

`maxAssetBytes` defaults to 10 MiB (10,485,760 bytes) — the PER-ASSET inlining size cap. `maxTotalAssetBytes`
defaults to 50 MiB (52,428,800 bytes) — a CUMULATIVE cap across every asset inlined into one export, so a
plan referencing many under-the-per-asset-cap images (e.g. 50 images at 9.9 MiB each) cannot still balloon
the "portable" artifact past a few hundred MB. Assets are considered in document order; once the running
total of already-inlined bytes would exceed `maxTotalAssetBytes`, every SUBSEQUENT local image is omitted
(`data-charter-export-omitted="total-cap-exceeded"`) even if it is individually under `maxAssetBytes` — a
document processed earlier already spent the budget. Tests should pass SMALL overrides for both (e.g. a
few hundred bytes) so size-cap tests can use small fixture files rather than allocating real multi-MB
assets.

**Write failing tests** in a NEW file `tests/Charter.Core.Tests/ArtifactExporterTests.cs`, class
trait-tagged `[Trait("Category", "ArtifactExporter")]`. Use a temp directory per test (create it, write
fixture files into it, delete it in a `finally` — mirror the temp-dir-with-cleanup style already used
elsewhere in this repo) as the `planDirectory` argument, so every test is self-contained and needs no
external resource. Cover, each as its own `[Fact]`:

1. **Relative local image inlines as a data: URI.** A markdown document containing
   `![Diagram](./diagram.png)` where `diagram.png` is a small real file written into the temp
   `planDirectory`: `ArtifactExporter.Export(...)` returns HTML whose `<img src="...">` for that image is
   `data:image/png;base64,<...>` (the exact base64 of the fixture's bytes) — NOT the original relative
   path. Assert the output contains `data:image/png;base64,`.
2. **`file://`-referenced local image also inlines.** A markdown image referencing the fixture via an
   absolute `file:///...` URI inlines identically to case 1 — assert the output contains the `data:` URI
   AND does NOT contain the literal `file://` path text anywhere.
3. **MIME by extension.** At least one non-PNG extension (e.g. `.svg` or `.jpg`) inlines with the matching
   MIME (`image/svg+xml`, `image/jpeg`) — assert the exact `data:<mime>;base64,` prefix.
4. **Oversized asset is omitted, not inlined, and its path never leaks.** Pass a small `maxAssetBytes`
   override (e.g. `100`) and a fixture file bigger than that. Assert: the output does NOT contain a
   `data:` URI for it; the `<img>` `src` is `about:blank`; the element carries
   `data-charter-export-omitted="too-large"`; the element carries `data-charter-export-filename` equal to
   just the fixture's basename (e.g. `big.png`, never the full temp-dir path); and the FULL original local
   path/temp-dir text is **absent** from the output entirely (not merely the `src` attribute — search the
   whole output string).
5. **Missing asset is omitted the same way.** Reference a file that does not exist in `planDirectory`:
   assert `data-charter-export-omitted="not-found"`, `about:blank`, and no leaked path.
6. **Path traversal is refused, not read.** Reference `../outside.png` (a file that exists just OUTSIDE
   `planDirectory`, one level up) — assert it is treated exactly like a missing asset (omitted, never
   inlined, never read) even though the file genuinely exists on disk. `ArtifactExporter` must confine
   asset reads to `planDirectory` and never follow a path that escapes it.
7. **A non-image local reference (e.g. a link) is redacted, not omitted — to a BARE constant, no basename
   kept.** A markdown link `[Notes](file:///C:/some/local/notes.txt)` (rendered as `<a href="file://...">`)
   has its ENTIRE `file://` URI rewritten to the literal constant `file:///[redacted]` — assert the output
   contains `file:///[redacted]` and does NOT contain the original path text (`C:/some/local`, `notes.txt`,
   or any other fragment of the original URI) anywhere. **Do NOT append the original basename** (unlike the
   image-omission case in test 4/5, which DOES keep a basename): a `file://` reference to a bare directory
   (`file:///C:/Users/David`) has no reliable way to distinguish "file" from "directory" in its last path
   segment, and keeping ANY fragment of the original text risks leaking exactly the username/codename the
   redaction exists to hide. The redaction constant is intentionally basename-free for every case, not just
   directory-shaped ones — simpler and unconditionally safe beats "safe except when the shape is
   ambiguous."
8. **A `:::custom-html` `<video src="...">` (or any OTHER element's `src=`) inlines exactly like an
   `<img src>` — the local-reference scan is TAG-AGNOSTIC.** `:::custom-html` (the raw-HTML escape hatch)
   containing e.g. `<video src="file:///C:/local/clip.mp4"></video>` — the SAME treatment as test 1/2:
   assert the output contains `data:video/mp4;base64,` (add `.mp4`→`video/mp4` to the MIME map, plus
   `.webm`→`video/webm`, `.pdf`→`application/pdf`, `.css`→`text/css` — small, cheap additions) and does
   NOT contain the original path or `file://` anywhere. **This is a deliberate, load-bearing scope
   decision, not an accident:** scan for any `src="..."` attribute VALUE regardless of which element it's
   on — never scope the regex to `<img\s` specifically. A tag-scoped `<img\s+[^>]*src="..."` pattern would
   silently exclude `<video src>`/`<iframe src>`/`<source src>` from `:::custom-html`, leaving exactly the
   local-path leak the whole task exists to close; the tag-agnostic `src="..."` scan closes it for free
   with no added complexity, at the cost of a video/pdf/css asset being inlined with a generic
   `application/octet-stream` MIME if its extension isn't in the map (still self-contained and path-free,
   just possibly not natively renderable by the browser — an acceptable, honest degradation, never a leak).
   **Only a NON-`src` local reference — a `:::custom-html` `<a href="file://...">`, or a bare CSS
   `url(...)` / SVG `xlink:href` (neither uses the `src` attribute at all) — falls through to the separate
   redaction pass (test 7) or is genuinely OUT OF SCOPE (a CSS `url()`/`xlink:href`, since `Charter.Core`
   has no CSS/SVG parser and none is added here — a documented residual gap, not a bug to silently work
   around).**
9. **Remote assets are left alone.** A markdown image with an `http://`/`https://` source is untouched —
   assert the exact original `http(s)://` URL string survives verbatim in the output (export inlines only
   LOCAL assets; a remote image is explicitly out of scope, not a bug).
10. **No SDK marker.** The exported output NEVER contains the substring `data-charter-sdk` (the
    `Charter.Server.SdkInjector` marker) — `export` must never carry the serve-time annotation SDK
    (invariant 1: portable artifact, SDK injected only at serve time, by the server, never by `export`).
11. **A document with no local references round-trips unchanged in substance.** A plain document with only
    remote/no images produces output whose rendered content still matches what
    `CharterRenderer.Render` alone would produce for the same markdown (`ArtifactExporter.Export` must not
    corrupt ordinary content when there is nothing to inline or redact).
12. **A `:::diagram` block's inlined Mermaid runtime is NEVER touched by the asset scan (the load-bearing
    regression test).** `src/Charter.Core/assets/mermaid.min.js` (the REAL vendored library, read via
    `MermaidResource.Library` exactly as `CharterRenderer` inlines it) contains literal JS string
    fragments that LOOK like an `<img src="...">` tag when naively regex-scanned — e.g. Mermaid's own
    `` `<img src="${e}" alt="${r}"` `` template-literal fragment used to build diagram-label markup at
    runtime. A markdown document containing BOTH a `:::diagram` block (which pulls this exact vendored
    blob into a `<script>` tag, per `CharterRenderer.MermaidRuntimeMarkup`) AND a local image
    (`![Pic](./pic.png)`) must: (a) inline the local image as a real `data:image/png;base64,` URI, exactly
    as test 1; AND (b) leave the `<script>...</script>` block(s) carrying the Mermaid runtime **completely
    byte-identical** to what `CharterRenderer.Render(markdown)` alone would produce for the same input —
    assert this by comparing the `<script>` region of `ArtifactExporter.Export(...)`'s output against the
    `<script>` region of a plain `CharterRenderer.Render(...)` call on the same markdown (e.g. extract via
    the same script-tag regex both call sites use, or simpler: assert the FULL vendored-library marker
    token `__esbuild_esm_mermaid_nm` is present in the exported output UNMODIFIED, and that the naive
    JS-string false-positive `<img src="'+es(this.src)+'"` — copy this exact substring, it is real vendored
    text — has NOT been rewritten to `about:blank`/`data-charter-export-omitted` anywhere in the output).
    This test is the direct regression guard for the interaction the architect's own devil's-advocate pass
    flagged as the highest-risk defect in this whole component: an asset-inlining implementation that
    naively regex-scans the ENTIRE rendered HTML string (including embedded `<script>` JS) will corrupt the
    diagram runtime the instant a future Mermaid build closes that string fragment with a literal `>`.
13. **Path-confinement rejects a sibling directory sharing `planDirectory` as a raw STRING prefix (the
    trailing-separator-safety regression test).** Create `planDirectory` as e.g. `<temp>/plan` AND a
    SIBLING directory `<temp>/plan-evil` containing a real file `secret.png`. Reference it via a markdown
    image path engineered to resolve to `<temp>/plan-evil/secret.png` (e.g. `../plan-evil/secret.png`).
    Assert it is treated exactly like a missing/traversal asset (omitted, never inlined) — a NAIVE
    "does the resolved path start with the string `planDirectory`" check would WRONGLY accept this (since
    `"<temp>/plan-evil/secret.png"` textually starts with `"<temp>/plan"`), so this test specifically
    catches a confinement check that compares raw string prefixes instead of a directory-separator-safe
    prefix (mirror `Charter.Server.PathConfinement.Resolve`'s `normalizedRoot +
    Path.DirectorySeparatorChar` pattern, read that file for the exact shape).
14. **The cumulative total-asset cap stops inlining once spent, even under the per-asset cap.** Pass a
    small `maxTotalAssetBytes` override (e.g. `150`) alongside a normal `maxAssetBytes` default, and TWO
    fixture images each individually under both caps (e.g. two 100-byte files) referenced in document
    order. Assert the FIRST inlines as a real `data:` URI, and the SECOND is omitted with
    `data-charter-export-omitted="total-cap-exceeded"` even though it alone would fit under
    `maxAssetBytes` — the running total from the first asset already spent the budget.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Core.Tests/ArtifactExporterTests.cs`
and `src/Charter.Core/ArtifactExporter.cs` (the stub). After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including any other file in
`src/Charter.Core/`, any `.csproj`, or `src/Charter.Cli/`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol elsewhere, do NOT edit that
file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Required coverage (a guardrail greps the ArtifactExporterTests file — each MUST appear):**
`[Trait("Category", "ArtifactExporter")]`, `ArtifactExporter.Export`, `data:`, `file:///\[redacted\]`,
`data-charter-export-omitted`, `data-charter-sdk`, `mermaid` (the script-region regression test),
`total-cap-exceeded`, and at least one real `[Fact]` or `[Theory]` attribute. Lower-bound presence checks
— they do not substitute for the real assertions above.

**Completion criteria (match this task's guardrails):** `tests/Charter.Core.Tests` BUILDS with the
`ArtifactExporterTests` present and the stub compiling (all referenced types already exist), and
`dotnet test --filter "Category=ArtifactExporter"` FAILS (every test throws `NotImplementedException`
against the stub). Failing at runtime is intended; not compiling is a mistake to fix.
