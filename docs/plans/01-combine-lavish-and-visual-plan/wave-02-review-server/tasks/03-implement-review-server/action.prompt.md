---
maxTurns: 75  # turn-expensive (#94): implements an in-process loopback HTTP server (Kestrel-vs-HttpListener spike) composing the injection/capability/confinement seams — unfamiliar-transport discovery.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-02-review-server/03-implement-review-server": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement `Charter.Server` over the stubs the previous task authored, so its
`[Trait("Category","ReviewServer")]` tests pass — including the loopback serve integration test.

Fill real logic into `src/Charter.Server/`:
- **`SdkInjector.Inject`** — inject the serve-time SDK `<script data-charter-sdk>…</script>` before
  `</body>` (append if absent) WITHOUT mutating the input. In wave 2 the injected script is a MINIMAL
  placeholder carrying the `data-charter-sdk` marker (a stub the wave-3 annotation SDK replaces); the
  deliverable is the injection MECHANISM, not the SDK body.
- **`CapabilityKey`** — a cryptographically-random per-session key; `Matches` compares in constant time.
- **`PathConfinement.Resolve`** — canonicalize `root` and the requested path and return the full path
  only if it stays under `root`; reject `..` traversal and absolute escapes with `null`.
- **`ReviewSession` / `ReviewServerOptions`** — a session binds the source `.mdx`, its confined root, and
  a fresh `CapabilityKey`; options default `BindAddress` to `IPAddress.Loopback`.
- **`IReviewServer` / `ReviewServer.Start`** — start a **loopback-only** HTTP server that, per request,
  renders the session's source plan via `Charter.Core.CharterRenderer.Render(...)`, injects the SDK
  (`SdkInjector.Inject`), and serves it — but ONLY when the request carries the session's capability key
  (reject with 401/403 otherwise), and ONLY for paths that pass `PathConfinement` (reject traversal with
  403/404). `Address` exposes the bound `http://127.0.0.1:<port>/?key=<key>` URI. Because it renders from
  the source file per request, editing the plan and refreshing shows the update (wave-2 "live reload"; the
  push-based SSE reload lands in wave 3).

**Transport (the wave-2 spike):** choose Kestrel or `HttpListener`. Prefer **`HttpListener`** (BCL, no
extra dependency) to keep Charter a lean single-file / AOT-friendly binary — the invariant that motivates
the whole project; reach for Kestrel only if a concrete need forces it, and if so add the ASP.NET Core
framework reference to `Charter.Server.csproj`. Record your choice + one-line rationale in the state-out.

**Rules:** Do NOT edit any file under `tests/` — the tests are the spec. If an authored test is genuinely
wrong or contradictory, write `{"needsHuman": "<why>"}` rather than changing it. Your `writeScope` is
`src/Charter.Server/` only.

**Completion criteria (match this task's guardrail):** `dotnet test --filter "Category=ReviewServer"` is
all green, and the solution builds.
