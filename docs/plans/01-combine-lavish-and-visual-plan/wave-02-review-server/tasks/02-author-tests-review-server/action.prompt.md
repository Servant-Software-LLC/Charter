---
maxTurns: 75  # turn-expensive (#94): authors an in-process loopback HTTP integration test against a server it is only stubbing (unfamiliar transport surface) on top of the pure-unit tests.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-02-review-server/02-author-tests-review-server": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing xUnit tests** plus the **minimal `NotImplementedException` stubs** for the
`Charter.Server` review server. This is the TDD "red": the tests MUST COMPILE and FAIL against the
stubs. Do NOT implement the real logic — the next task does that.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Server.Tests/` and the seven stub
files `src/Charter.Server/SdkInjector.cs`, `src/Charter.Server/CapabilityKey.cs`,
`src/Charter.Server/PathConfinement.cs`, `src/Charter.Server/ReviewServerOptions.cs`,
`src/Charter.Server/ReviewSession.cs`, `src/Charter.Server/IReviewServer.cs`,
`src/Charter.Server/ReviewServer.cs`. After this task completes, the harness runs a `git diff` check and
rejects any edit outside these paths — including `Charter.Core`, `Charter.Cli`, the `.csproj` files, or
`Charter.sln`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Produce:

1. **Stubs** (compile-only skeletons in `namespace Charter.Server;`; every member throws
   `NotImplementedException`, except plain data/config which may hold trivial defaults):
   - `SdkInjector.cs` — `public static string Inject(string html, string sdkScript)`: returns HTML with
     the serve-time SDK `<script>` injected (before `</body>` when present, else appended), WITHOUT
     mutating the input. The injected script MUST carry the stable marker attribute `data-charter-sdk`.
   - `CapabilityKey.cs` — `public sealed class CapabilityKey` with `public string Value { get; }`,
     `public static CapabilityKey Create()` (a fresh random per-session key), and
     `public bool Matches(string? presented)`.
   - `PathConfinement.cs` — `public static string? Resolve(string root, string requestPath)`: the full
     path if it stays inside `root`, else `null` (reject `..` traversal and absolute-path escapes).
   - `ReviewServerOptions.cs` — a config type: `IPAddress BindAddress` (default `IPAddress.Loopback` —
     loopback-only), `int Port` (0 = ephemeral), `bool OpenBrowser`.
   - `ReviewSession.cs` — `public sealed class ReviewSession` binding a source `.mdx` path, its confined
     root, and a `CapabilityKey`; `public static ReviewSession Create(string sourcePath)`.
   - `IReviewServer.cs` / `ReviewServer.cs` —
     `public interface IReviewServer : IDisposable { Uri Address { get; } }` and
     `public sealed class ReviewServer : IReviewServer` with
     `public static ReviewServer Start(ReviewSession session, ReviewServerOptions? options = null)`
     (starts a loopback HTTP server serving the rendered + SDK-injected plan), `Uri Address { get; }`,
     and `Dispose()`.

2. **Tests** in `tests/Charter.Server.Tests/`, each class trait-tagged `[Trait("Category","ReviewServer")]`
   (so a guardrail can `--filter "Category=ReviewServer"` to this task's tests only):
   - **SDK-injection tests** — `SdkInjector.Inject` returns HTML that contains the original body AND the
     injected script carrying `data-charter-sdk`; the input string is not mutated; the script lands before
     `</body>` when present.
   - **Capability-key tests** — a `Create()`d key `Matches` its own `Value`; a different or empty/`null`
     presented key does NOT match.
   - **Path-confinement tests** — an in-root relative path resolves to a path under `root`; a `..`
     traversal and an absolute path outside `root` each resolve to `null`.
   - **Loopback serve integration test** — start a real server with `ReviewServer.Start(...)` on the
     default (loopback) options, then over `HttpClient`: (a) a GET carrying the session's capability key
     returns 200, with a body containing the rendered plan content AND the `data-charter-sdk` marker, and
     `Address` is a `127.0.0.1` loopback URI; (b) a GET WITHOUT the key is rejected (non-200); (c) a GET of
     a `..`-traversal path is rejected (non-200). Bind an ephemeral port (`Port = 0`), read the chosen port
     from `Address`, and `Dispose()` the server in a `finally`/`using`.

   **Required coverage (a guardrail greps the ReviewServer test files for these — each MUST appear):**
   `Inject`, `Capability`, `Confin`, `Loopback` (or `127.0.0.1`), and a `.Start(` call. A missing token
   fails the task. These are lower-bound presence checks — they do not substitute for real, meaningful
   tests.

**Completion criteria (match this task's guardrails):** the solution BUILDS with the tests + stubs
present, and `dotnet test --filter "Category=ReviewServer"` FAILS (the stubs throw). Failing is intended;
not compiling is a mistake to fix.
