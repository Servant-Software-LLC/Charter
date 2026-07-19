---
maxTurns: 75  # turn-expensive (#94): vendoring an unfamiliar third-party runtime (obtain a pinned build, embed it, add a resource loader) — API/setup discovery before code.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/02-vendor-mermaid-runtime": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Vendor the **Mermaid** diagram runtime **offline** into `Charter.Core` so a rendered `:::diagram` renders
in a **saved, standalone** artifact with **no network** (invariant 1 — portable artifact). Mermaid renders
client-side, so its library must ship **inside** the rendered HTML, embedded/vendored — **NEVER** a CDN
`<script src="https://…">` (that breaks portability and offline use, and is a supply-chain/egress surface
Charter's zero-telemetry posture forbids). This is the direct analogue of how `Charter.Server` embeds
`sdk/charter-annotate.js` — read `src/Charter.Server/SdkResource.cs` and `src/Charter.Server/Charter.Server.csproj`
first and mirror that mechanism, but in `Charter.Core`.

Three deliverables:

1. **The vendored library** at `src/Charter.Core/assets/mermaid.min.js` — a **pinned, real** Mermaid
   minified build (a specific version, e.g. from the `mermaid` npm package's `dist/mermaid.min.js`).
   Record the exact version in a short header comment or an adjacent `assets/MERMAID-VERSION.txt`
   (version-pin + license attribution — Mermaid is MIT). It MUST be the real minified library (hundreds of
   KB), not a hand-written stub or a truncated file.
   - **If you cannot obtain the real pinned build** (no network access to fetch it, no local copy), do NOT
     commit a placeholder or a partial file — write
     `{"needsHuman": "Vendor mermaid.min.js (pinned version) into src/Charter.Core/assets/ — the offline library could not be fetched in this environment"}`
     to the state-out path and stop. A truncated/fake library is worse than an honest halt.
2. **The csproj embed** — add an `<EmbeddedResource>` to `src/Charter.Core/Charter.Core.csproj` including
   `assets/mermaid.min.js` with `<LogicalName>Charter.Core.mermaid.min.js</LogicalName>` (the stable
   manifest key the loader reads it back by), mirroring the Server csproj's SDK embed.
3. **The loader** `src/Charter.Core/MermaidResource.cs` — an `internal static` class (mirror
   `Charter.Server.SdkResource`) that reads the embedded `Charter.Core.mermaid.min.js` manifest resource
   once (cached) and exposes it as a string the renderer can inline. Expose the raw library text (the
   `<pre class="mermaid">` markup and the `mermaid.initialize`/`mermaid.run` init call are emitted by
   `04-implement-diagram-block`, which consumes this loader).

**Scope boundary (harness-enforced):** Write only to `src/Charter.Core/assets/`,
`src/Charter.Core/MermaidResource.cs`, and `src/Charter.Core/Charter.Core.csproj`. Do NOT edit
`CharterRenderer.cs`, `BlockModel.cs`, `SourceMap.cs`, the tests, or `Charter.Server`. An out-of-scope edit
fails the task and consumes a retry.

**Note on the mixed-stack gap (honest):** there is no JS build/lint/bundle toolchain in this repo, so the
vendored library is verified by deterministic STATIC checks only (it exists, it is a non-trivial real
library, it is embedded, it is not a CDN link). That the embedded Mermaid actually renders an SVG in a
browser is a real-browser behavior the breakdown surfaces as a decision/honest-halt (Charter #8) — it is
NOT auto-verified here.

**Completion criteria (match this task's guardrails):** `src/Charter.Core/assets/mermaid.min.js` exists and
is a non-trivial real library; `Charter.Core.csproj` embeds it with LogicalName `Charter.Core.mermaid.min.js`;
`MermaidResource.cs` reads that embedded resource; `Charter.Core` builds.
