---
maxTurns: 75  # turn-expensive (#94): authors an in-process loopback HTTP round-trip + SSE harness against a real ReviewServer (unfamiliar transport surface) — the M3 acceptance test.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-03-annotation-feedback/03-author-tests-annotation-api": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing xUnit tests** for the annotation **HTTP API** that extends the wave-2 `ReviewServer`. This
is the TDD "red", achieved **without new stubs**: the tests compile against the EXISTING surface
(`ReviewServer.Start`, `ReviewSession`, the `Annotation` record from `02-implement-session-store`,
`Charter.Core.SourceMap`, `System.Net.Http.HttpClient`) and FAIL **at runtime** because the API endpoints
are not routed yet (the server returns non-200 / no round-trip). Task `04-implement-annotation-api` makes
them pass. Do NOT implement the endpoints.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Server.Tests/`. After this task
completes, the harness runs a `git diff` check and rejects any edit outside that path — including
`src/Charter.Server/` (that is task 04's surface), `Charter.Core`, `Charter.Cli`, or the `.csproj` files.
An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by
a missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to
the state-out path and stop.

Reference the REAL wave-2 surface (already materialized — verify by reading the files, do not trust a
remembered signature): `Charter.Server.ReviewServer.Start(ReviewSession, ReviewServerOptions?)` returns an
`IReviewServer` with `Uri Address`; the capability key rides the `?key=` query string
(`session.Key.Value`); `Charter.Core.SourceMap.Build(markdown).LineForAnchor(anchorId)` maps a block's
content-derived anchor id to its 1-based markdown source line.

Author tests in a new file under `tests/Charter.Server.Tests/`, class trait-tagged
`[Trait("Category", "AnnotationApi")]` (distinct from `Category=AnnotationStore` and wave-2's
`Category=ReviewServer`), driving a real server started with `ReviewServer.Start(...)` on default (loopback,
ephemeral-port) options and disposed in a `finally`/`using`:

1. **Round-trip acceptance (THE M3 acceptance — load-bearing).** Write a temp `.mdx` plan with a couple of
   blocks. Determine a real block **anchor id** for one block and its expected source line via
   `SourceMap.Build(planMarkdown)` (and/or the anchor ids the rendered artifact carries). Then over
   `HttpClient`: **POST** an annotation (a JSON body carrying `kind` = element/text-range/diagram-node,
   the `anchorId`, and a `note`) to `POST /api/{key}/prompts`; then **GET** `/api/poll?key=...` and assert
   the response returns that annotation carrying `SourceLine` == `SourceMap.Build(planMarkdown)
   .LineForAnchor(anchorId)` (the correct markdown source anchor). This proves the browser half's server
   counterpart: submit -> store -> poll -> anchor-resolved-to-source-line.
2. **/api/sessions** — a `GET /api/sessions?key=...` returns the current session descriptor (e.g. the
   source path / a session id) as JSON; without the key it is rejected (non-200).
3. **/events SSE reload** — a `GET /events?key=...` opens a `text/event-stream` response (assert the
   `Content-Type`, and/or that at least one reload event can be read) — the push-based live-reload channel.
4. **CSRF / same-origin on the state-changing POST** — a `POST /api/{key}/prompts` carrying a **foreign
   `Origin` header** (a cross-site origin) is REJECTED (non-200) even WITH a valid key; the happy-path POST
   in test 1 uses a same-origin request (no foreign Origin). This encodes the plan invariant "capability
   key + CSRF on state-changing routes". (The exact CSRF mechanism — same-origin check vs. a per-session
   token — is a design decision the implementer resolves; assert the observable: a foreign-origin POST is
   refused.)

Send the traversal-free `../`-safe requests as normal `HttpClient` calls. Long-poll: `GET /api/poll`
should block briefly and return the queued annotation; keep the test's timeouts bounded (no fixed sleeps in
assertions — poll with a bounded deadline).

**Required coverage (a guardrail greps the AnnotationApi test files — each MUST appear):**
`[Trait("Category","AnnotationApi")]`, `prompts`, `poll`, `sessions`, an SSE token (`events` or
`event-stream`), an anchor-resolution token (`LineForAnchor` / `SourceLine` / `SourceMap`), and a CSRF
token (`Origin` or `Csrf`). Lower-bound presence checks — they do not substitute for real assertions.

**Completion criteria (match this task's guardrails):** the test project BUILDS (all referenced types
already exist), and `dotnet test --filter "Category=AnnotationApi"` FAILS (the endpoints are not routed
yet, so the round-trip does not complete). Failing at runtime is intended; not compiling is a mistake to
fix.
