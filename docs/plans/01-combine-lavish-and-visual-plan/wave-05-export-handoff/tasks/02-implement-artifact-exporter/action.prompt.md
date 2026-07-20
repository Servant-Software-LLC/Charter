## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-05-export-handoff/02-implement-artifact-exporter": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement `Charter.Core.ArtifactExporter` so the `Category=ArtifactExporter` tests
(`tests/Charter.Core.Tests/ArtifactExporterTests.cs`, authored by task 01) pass. **Fill real logic over
the existing stub; do NOT edit the tests.** Read the current shape of the stub and its neighbors first
(do not assume a remembered shape): `src/Charter.Core/ArtifactExporter.cs`,
`src/Charter.Core/CharterRenderer.cs` (`Render(markdown)` — the entry point you wrap; do not modify it),
`src/Charter.Server/SdkInjector.cs` (the `data-charter-sdk` marker your output must never contain).

`Export(markdown, planDirectory, maxAssetBytes, maxTotalAssetBytes)` should:

1. **Call `CharterRenderer.Render(markdown)` first**, unmodified, to get the same HTML `render`/`review`
   already produce (including the already-inlined vendored Mermaid runtime — leave that alone).
2. **Split the HTML into SCRIPT and non-SCRIPT regions BEFORE scanning for local assets, and never touch a
   SCRIPT region.** This is load-bearing, not an optimization: `CharterRenderer.Render` inlines the REAL
   vendored Mermaid library (`MermaidResource.Library`, ~3.5 MB of minified JS) inside a `<script>` tag
   whenever the document has a `:::diagram` block, and that vendored blob contains literal JS
   template-string fragments that LOOK like an HTML `<img src="...">` tag to a naive regex scan (e.g.
   Mermaid's own `` `<img src="${e}" alt="${r}"` `` fragment). Scanning the WHOLE rendered string for
   `<img src="...">` would misclassify that JS text as a local image reference and corrupt the runtime by
   rewriting it to `about:blank`. Use a regex that captures every `<script[^>]*>...</script>` region (there
   may be one or two — the library script and the separate init/bootstrap script `MermaidRuntimeMarkup`
   emits), set those regions ASIDE UNTOUCHED, run steps 3-6 below ONLY over the remaining non-script text,
   then reassemble by substituting the untouched script regions back into their original positions. Task
   01's test 12 is the regression guard for this exact interaction — read it before implementing.
3. **Find every local `src="..."` attribute value** in the non-script HTML — **TAG-AGNOSTIC: match
   `src="..."` on ANY element, never scope the regex to `<img\s`.** A `src` value that is NOT `http://`,
   `https://`, or already `data:` is "local" and handled by steps 4-7, regardless of whether it sits on an
   `<img>` (from markdown image syntax) or a `<video>`/`<iframe>`/`<source>` (only reachable via a
   `:::custom-html` block, the raw-HTML escape hatch). This is deliberate: a tag-scoped `<img\s+[^>]*src=`
   regex would silently exclude `:::custom-html`'s `<video src>`/`<iframe src>` from ever being inlined or
   confined, leaving exactly the local-path leak this task exists to close — the tag-agnostic form closes
   it for free. It covers both a plain relative path (`./diagram.png`) and an explicit `file://` URI,
   uniformly, since both are "local" by the same test. (An element's `src` is a plain attribute-value scan;
   this does NOT require an HTML/DOM parser — `Charter.Core` still adds no new dependency.)
4. **Resolve the src to an absolute path CONFINED to `planDirectory`, trailing-separator-safe.** Strip a
   `file://` prefix if present, then resolve relative to `planDirectory` with `Path.GetFullPath`. The
   containment check MUST be separator-safe, not a bare string-prefix check: compute
   `normalizedRoot = Path.GetFullPath(planDirectory).TrimEnd(DirectorySeparatorChar)`, accept only when the
   resolved path EQUALS `normalizedRoot` or starts with `normalizedRoot + DirectorySeparatorChar` — mirror
   `Charter.Server.PathConfinement.Resolve`'s exact shape (read that file; do not depend on it, reimplement
   the same logic locally since `Charter.Core` must not depend on `Charter.Server`). A bare
   `resolved.StartsWith(planDirectory)` check is WRONG: it would wrongly accept a sibling directory whose
   name merely shares `planDirectory` as a raw text prefix (e.g. `planDirectory=".../plan"` incorrectly
   matching `".../plan-evil/secret.png"`) — task 01's test 13 is the regression guard for this. Any path
   that fails confinement is treated exactly like a missing file: never read it.
5. **Track a running total of inlined bytes across the whole call.** Inline an asset ONLY when ALL hold:
   the file exists, is readable, resolves inside `planDirectory` (step 4), its OWN length is `<=
   maxAssetBytes`, AND adding its length to the running total so far would not exceed
   `maxTotalAssetBytes`. Process assets in the order they appear in the document; once the running total
   would be exceeded, every SUBSEQUENT asset omits with reason `total-cap-exceeded` even if it is
   individually small — task 01's test 14 is the regression guard.
6. **Inline (when step 5's checks all pass):** replace `src="..."` with `data:<mime>;base64,<...>`. Pick
   `<mime>` from the file extension: `.png`→`image/png`, `.jpg`/`.jpeg`→`image/jpeg`, `.gif`→`image/gif`,
   `.svg`→`image/svg+xml`, `.webp`→`image/webp`, `.bmp`→`image/bmp`, `.mp4`→`video/mp4`,
   `.webm`→`video/webm`, `.pdf`→`application/pdf`, `.css`→`text/css`, anything else→
   `application/octet-stream` (still self-contained and path-free even when the browser can't natively
   render it — an honest degradation, never a leak).
7. **Otherwise — OMIT, never inline and never leak the path:** rewrite `src="..."` to `src="about:blank"`,
   and add `data-charter-export-omitted="<reason>"` (`reason` is `too-large`, `total-cap-exceeded`,
   `not-found`, or `unreadable` — a path-traversal / confinement-escape case uses `not-found`, since it
   must be indistinguishable from a genuinely absent file to the artifact's consumer) plus
   `data-charter-export-filename="<basename>"` (just `Path.GetFileName` of the original reference — never
   the directory portion; this basename-on-omission is intentional and SAFE, unlike step 8's redaction,
   because an omitted `<img>` element's context already tells the reader "this was an image", so a bare
   filename carries no extra information). The FULL original local/temp-dir path must not appear ANYWHERE
   in the output string after this — not in an attribute, not in a comment.
8. **Redact every OTHER surviving local `file://` reference to the BARE constant `file:///[redacted]` — NO
   basename appended, ever.** This pass runs AFTER step 3-7's `src=`-scoped scan, so it only ever sees a
   `file://` reference that did NOT sit inside a `src="..."` attribute — in practice this means an
   `<a href="file://...">` (from a markdown link), or a `file://` inside a `:::custom-html` block on a
   NON-`src` attribute (e.g. an `href`). Replace the WHOLE `file://<path>` text with the literal string
   `file:///[redacted]` and nothing else. Do NOT keep any fragment of the original path, including the
   basename: `Path.GetFileName` on a `file://` URI whose last segment is actually a DIRECTORY (e.g.
   `file:///C:/Users/David`, no trailing content) returns `"David"` — exactly the private information
   redaction exists to hide — and nothing in the URI text alone reliably distinguishes a directory-shaped
   reference from a file-shaped one. Dropping the basename UNCONDITIONALLY, in every case, is simpler than
   a shape heuristic and has no leak surface. Do this as a final pass over the whole HTML string (still
   excluding the untouched script regions from step 2). A SCHEMELESS local reference with no `file://`
   prefix on a non-`src` attribute (e.g. a `:::custom-html` CSS `url('C:\local\bg.png')`, which is not even
   an HTML attribute) is explicitly OUT OF SCOPE — `Charter.Core` has no CSS/HTML-DOM parser and this task
   adds none; only a `src="..."` attribute (any tag, step 3-7) and any `file://`-schemed reference anywhere
   (this step) are handled.
9. **Leave remote (`http://`/`https://`) references untouched** — export inlines local assets only.
10. Reassemble the script and non-script regions (step 2) and return the resulting HTML string. Never write
    the `data-charter-sdk` marker anywhere (this method never calls `SdkInjector`, and must not — `export`
    is a static-artifact concern, not a serve-time one).

Keep the C#↔JS boundary narrow (invariant 6) and the artifact portable (invariant 1): `ArtifactExporter`
only post-processes HTML text and reads local files it already confined — it introduces no new JS, no
network call, and no dependency on `Charter.Server`.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/ArtifactExporter.cs`. Do NOT edit
the tests, `CharterRenderer.cs`, any `.csproj`, or any file in `src/Charter.Cli/` (a later task wires the
CLI verb). An out-of-scope edit fails the task and consumes a retry. If the authored tests are genuinely
wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path and stop rather than editing
them.

**Completion criteria (match this task's guardrails):** `dotnet test --filter "Category=ArtifactExporter"`
passes, and `src/Charter.Core/Charter.Core.csproj` still carries NO `ProjectReference` to
`Charter.Server` (this component must stay pure `Charter.Core` — it cannot possibly pull in
`SdkInjector`/`SdkResource` by construction, not merely by convention).
