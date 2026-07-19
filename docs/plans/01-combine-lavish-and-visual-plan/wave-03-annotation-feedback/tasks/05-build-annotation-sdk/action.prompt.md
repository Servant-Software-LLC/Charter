## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-03-annotation-feedback/05-build-annotation-sdk": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the lean embedded **JS annotation SDK** at `sdk/charter-annotate.js` — the browser half of Charter's
comment-in-place review loop, adapted lean from **Lavish** (MIT, attributed). It is the ONLY JS in the
project; keep the C#<->JS boundary narrow (invariant 6).

The SDK is injected into the served HTML **only at serve time** (task `06-wire-sdk-into-server` embeds this
file and wires it into `ReviewServer`); the saved artifact on disk stays SDK-free (invariant 1 — portable
artifact). Write plain, dependency-free ES that runs directly in a browser `<script>` (no bundler, no npm
imports — there is no JS build toolchain in this repo).

Requirements:

- **Namespace.** Expose a single global namespace `CharterAnnotate` (e.g.
  `window.CharterAnnotate = (function () { ... })();`) with an `init()` entry point. `CharterAnnotate` is the
  contract marker the server's served-content guardrail asserts, so it MUST appear verbatim.
- **Three annotation kinds.** Support anchoring a human note to (a) an **element** (a rendered block, keyed
  by its stable block id / `data-` anchor attribute), (b) a **text-range** (a selection within a block), and
  (c) a **diagram-node** (a node inside a `:::diagram` Mermaid render, keyed by node identity). Each
  annotation carries the block/anchor id, the kind, and the note text.
- **postMessage boundary.** Communicate over a `postMessage`-based channel (the narrow, defined C#<->JS
  boundary) — do not reach into server internals directly. Submitting an annotation POSTs it to
  `/api/{key}/prompts` (the endpoint task 04 routes); receiving a live-reload event listens on `/events`
  (SSE). Read the capability key from the page URL's `?key=` query string.
- **MIT / Lavish attribution.** Open the file with a header comment attributing the adaptation to Lavish
  (MIT). The tokens `MIT` and `Lavish` MUST appear in the file.

Keep it lean and purpose-built — NOT a full Lavish clone (D2: keeping the SDK minimal is what makes the
re-port drift manageable).

**Scope boundary:** your `writeScope` is `sdk/` only. Do NOT modify `src/`, `tests/`, or any `.csproj`
(task 06 does the embedding + server wiring). An out-of-scope edit fails the task and consumes a retry.

**Note on verification (honest gap):** there is no JS test/lint/bundle toolchain in this repo (greenfield
`sdk/`, no node/npm), so this task's guardrails are deterministic STATIC checks only — the file exists and
carries its load-bearing surface (postMessage, the three kinds, the `CharterAnnotate` namespace, MIT/Lavish
attribution). The SDK's browser BEHAVIOR (does clicking actually create an annotation that round-trips) is
verified by the C# server-half round-trip test (task 03/04) plus a human/Playwright browser check the
breakdown surfaces as a decision — it is NOT auto-verified here.

**Completion criteria (match this task's guardrails):** `sdk/charter-annotate.js` exists with real content
and contains `postMessage`, `CharterAnnotate`, `element`, a text-range token, a diagram/node token, and the
`MIT` + `Lavish` attribution.
