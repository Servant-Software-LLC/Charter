---
maxTurns: 75  # turn-expensive (#94): authors an in-process loopback HTTP round-trip harness against a real ReviewServer (transport surface) — the :::question answer-submission acceptance test.
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/13-author-tests-answer-submission": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author **failing xUnit tests** for the `:::question` **answer-submission** route on `Charter.Server`, in a
new file under `tests/Charter.Server.Tests/`, class trait-tagged `[Trait("Category", "AnswerApi")]`. TDD
"red" **without stubs**: compile against the EXISTING server surface (`Charter.Server.ReviewServer.Start`,
`ReviewSession`, `System.Net.Http.HttpClient`) and FAIL at **runtime** because the answers routes are not
routed yet. Task `14-implement-answer-submission` makes them pass. Do NOT implement the endpoints.

Read the REAL wave-3 surface first (materialized — verify by reading, do not trust a remembered signature):
`src/Charter.Server/ReviewServer.cs` (the `/api/{key}/prompts` POST + `/api/poll` GET + the CSRF gate
`AnnotationApi.IsAllowedOrigin`), `src/Charter.Server/AnnotationApi.cs`, and a wave-3 AnnotationApi test for
the in-process HttpClient round-trip pattern (start `ReviewServer.Start(session)` on default loopback
ephemeral-port options, dispose in a `finally`/`using`, hit `server.Address` with `HttpClient`).

The **design decision this encodes** (stated so the implementer matches it): answers use a **dedicated**
route, NOT the annotation `/api/{key}/prompts` + `/api/poll` pair — an answer's shape (questionId, selected
values, target) differs from an annotation (anchorId, note), and reusing `/api/poll` would force a breaking
change to the wave-3 annotation poll contract (its response is a bare annotation array — a #193 shared-golden
break). So author against a **new** `POST /api/{key}/answers` (submit) + `GET /api/answers?key=…` (drain).

Author these facts:

1. **Round-trip acceptance (load-bearing).** Over `HttpClient`: **POST** a structured answer JSON body
   (`questionId`, a `mode`, selected `values` (an array/string), and `target` = `human` or `agent`) to
   `POST /api/{key}/answers`; assert 200. Then **GET** `/api/answers?key=…` and assert the answer round-trips
   carrying the same `questionId`, the same selected `values`, and the same `target`. (Deserialize the
   response with `JsonDocument`/`JsonSerializer` into an anonymous/loose shape so the test compiles against
   the existing surface — do NOT require a new server type to exist.)
2. **Target routing observable.** Assert the stored/returned answer preserves `target` (human vs agent) —
   the field the downstream handoff routes on; a `human`-target answer and an `agent`-target answer are both
   accepted and echo their target.
3. **CSRF / same-origin on the state-changing POST.** A `POST /api/{key}/answers` carrying a **foreign
   `Origin`** header is REJECTED (non-200) even with a valid key (reuse the invariant wave-3 applies to
   `/prompts`). The happy-path POST in test 1 is same-origin.
4. **Capability-key gate.** A `GET /api/answers` WITHOUT the key (or with a wrong key) is rejected (non-200).

Keep timeouts bounded (no fixed sleeps in assertions — poll a drain with a bounded deadline if needed).

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Server.Tests/`. The harness runs a
`git diff` check after this task and rejects any edit outside that path — including `src/Charter.Server/`
(task 14's surface), `Charter.Core`, or the `.csproj`. An out-of-scope edit fails the task and consumes a
retry. If you hit a compile error from a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` and stop.

**Required coverage (a guardrail greps the AnswerApi test files — each MUST appear):**
`[Trait("Category","AnswerApi")]`, an `answers` token, a `questionId` token, a `target` token, a CSRF token
(`Origin`), a **drained round-trip assertion** — an `Assert.…(…)` call whose statement references the
round-tripped `questionId`/`values`/`target` (proving the GET-drained value is checked, not just the POST
status) — and a real `[Fact]`/`[Theory]`.

**Completion criteria (match this task's guardrails):** `tests/Charter.Server.Tests` BUILDS (all referenced
types already exist), and `dotnet test --filter "Category=AnswerApi"` FAILS (the answers routes are not
routed yet). Failing at runtime is intended; not compiling is a mistake to fix.
