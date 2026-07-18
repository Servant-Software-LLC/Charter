## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-02-review-server/01-scaffold-server-projects": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Scaffold the two NEW projects wave 2 needs, and register them in the solution — **structure only, no
behavior** (a later task authors the tests, another implements the server).

Create:

1. **`src/Charter.Server/Charter.Server.csproj`** — a `net8.0` class library (`Microsoft.NET.Sdk`).
   It renders plans via the wave-1 renderer, so give it a `ProjectReference` to
   `..\Charter.Core\Charter.Core.csproj`. Do NOT add an HTTP-transport dependency here — the
   Kestrel-vs-HttpListener choice belongs to the implementation task; leave the csproj minimal. Add NO
   `.cs` files (an SDK-style project compiles with zero sources; the stubs land in the next task).
2. **`tests/Charter.Server.Tests/Charter.Server.Tests.csproj`** — an xUnit test project mirroring
   `tests/Charter.Core.Tests/Charter.Core.Tests.csproj` **exactly** for package versions
   (`Microsoft.NET.Test.Sdk` 17.11.1, `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2),
   `IsPackable=false`, `net8.0`, with a `ProjectReference` to
   `..\..\src\Charter.Server\Charter.Server.csproj`. Add NO test files (the next task authors them).
3. **Register both projects in `Charter.sln`** (e.g. `dotnet sln Charter.sln add <csproj>` for each).

Repo build policy (ImplicitUsings, Nullable, TreatWarningsAsErrors, AnalysisLevel) is inherited from the
repo-root `Directory.Build.props` — do NOT restate it in the new csproj files.

**Scope:** your `writeScope` is `src/Charter.Server/`, `tests/Charter.Server.Tests/`, and `Charter.sln`.
Do NOT modify `Charter.Core`, `Charter.Cli`, or any existing tests.

**Completion criteria (match this task's guardrail):** both new projects exist and are registered in
`Charter.sln`; `Charter.Server.csproj` references `Charter.Core`; `Charter.Server.Tests.csproj`
references `Charter.Server`; and `dotnet build Charter.sln` succeeds.
