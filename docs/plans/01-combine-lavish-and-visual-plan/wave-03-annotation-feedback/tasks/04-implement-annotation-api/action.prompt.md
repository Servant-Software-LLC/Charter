---
maxTurns: 75  # turn-expensive (#94): terminal aggregation/wiring across the HttpListener transport, the AnnotationStore, SSE, and SourceMap resolution — the round-trip's server half.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-03-annotation-feedback/04-implement-annotation-api": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement the annotation **HTTP API** so the `Category=AnnotationApi` tests pass. Fix the implementation,
not the tests.

**First, read the real code you are extending** (do not rely on a remembered shape — this reflects the
plan-authoring-time state and may have shifted): `src/Charter.Server/ReviewServer.cs`. Its private
`Handle(HttpListenerContext)` method is the single request dispatch point — grep for the method name and
for the `?key=` capability gate (`_session.Key.Matches(...)`) and the `PathConfinement.Resolve(...)`
confinement gate; the wave-2 behavior renders the plan for any GET path AFTER those two gates. You will add
the `/api/*` and `/events` routes IN that dispatch, BEFORE the render-serve fallback, keeping the wave-2
read-only serve intact.

Route these endpoints (all still gated on the capability key, per the loopback + capability invariant):

- **`GET /api/sessions`** — return the current session descriptor as JSON (e.g. the source path and/or a
  session id). Reuse `ReviewSession`.
- **`POST /api/{key}/prompts`** — accept a submitted annotation (JSON: `kind`, `anchorId`, `note`).
  **Resolve the anchor**: read the plan markdown from `_session.SourcePath`, build
  `Charter.Core.SourceMap.Build(markdown)`, and set the annotation's `SourceLine =
  sourceMap.LineForAnchor(anchorId)` (the round-trip's deterministic half — the resolved markdown source
  line). Enqueue the resolved `Annotation` into a per-session `AnnotationStore` (construct one in
  `ReviewServer.Start` and hold it on the instance). This is a **state-changing route** → it MUST require
  the capability key AND a **CSRF / same-origin** check: reject a POST carrying a foreign `Origin` header
  (a cross-site origin) even with a valid key. (Choose the mechanism — a same-origin `Origin`-header check
  is the recommended default for a loopback server; a per-session CSRF token is the alternative. Whichever
  you pick, the observable the test asserts is: a foreign-`Origin` POST is refused.)
- **`GET /api/poll`** — long-poll: `await store.WaitForPendingAsync(timeout, ct)` then `Drain()` and return
  the drained annotations as JSON (each carrying its resolved `SourceLine`). Return promptly with the
  queued annotation once one is submitted.
- **`GET /events`** — a `text/event-stream` (SSE) response that pushes a reload event when the source file
  changes (wire a `FileSystemWatcher` on the source FILE — watch the file, not the whole tree — matching
  the plan's live-reload note). At minimum emit an initial/`ping` event so the stream's `Content-Type` and
  liveness are observable.

**Serve the SDK unchanged in THIS task.** Do not touch the SDK injection here — task
`06-wire-sdk-into-server` replaces the placeholder SDK. Keep the existing `SdkInjector.Inject(...)` call and
the `data-charter-sdk` marker exactly as wave 2 left them.

**Scope boundary:** your `writeScope` is `src/Charter.Server/` (you may add a new file such as
`AnnotationApi.cs` and edit `ReviewServer.cs`). Do NOT modify the tests (`tests/Charter.Server.Tests/`),
`Charter.Core`, or `Charter.Cli`. An out-of-scope edit fails the task and consumes a retry. If the authored
tests are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` rather than editing them.

**Completion criteria (match this task's guardrail):**
`dotnet test tests/Charter.Server.Tests/Charter.Server.Tests.csproj --filter "Category=AnnotationApi"`
passes (exit 0) — the round-trip returns the annotation with the correct source line, `/api/sessions` and
`/events` answer, and a foreign-origin POST is refused. The wave-2 `Category=ReviewServer` tests must stay
green (do not regress the read-only serve).
