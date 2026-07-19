## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/14-implement-answer-submission": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement the `:::question` **answer-submission** route in `Charter.Server` so the `Category=AnswerApi` tests
(`tests/Charter.Server.Tests`, task 13) pass. **Do NOT edit the tests.** Read the current shape of
`src/Charter.Server/ReviewServer.cs` first (materialized — verify, do not assume line numbers): note how
`HandleApiAsync` dispatches `/api/{key}/prompts` (POST) and `/api/poll` (GET), how `HandlePromptsAsync`
gates on the capability key + `AnnotationApi.IsAllowedOrigin` (CSRF) and enqueues into the per-session
`AnnotationStore`, and how `HandlePollAsync` drains it.

Add, **mirroring that pattern** and **without changing the existing `/prompts` + `/poll` annotation
contract**:

- **`POST /api/{key}/answers`** — gate on the capability key (path segment, like `/prompts`) + the CSRF
  same-origin check (`AnnotationApi.IsAllowedOrigin`). Parse a structured answer JSON body (`questionId`,
  `mode`, selected `values`, `target` ∈ `human`/`agent`) into a small `Answer` record (a new file, e.g.
  `src/Charter.Server/Answer.cs`), and enqueue it into a per-session **`AnswerStore`** (a new file mirroring
  `AnnotationStore` — a locked buffer; reuse the same single-writer/drain design). Preserve `target`.
- **`GET /api/answers?key=…`** — gate on the capability key (query string, like `/poll`), drain and return
  the queued answers as JSON.
- Construct the `AnswerStore` in `ReviewServer.Start` alongside the existing `AnnotationStore`, held for the
  server's lifetime.

**Design decision (why dedicated routes, not the poll stream):** an answer's shape differs from an
annotation, and reusing `/api/poll` (whose response is a bare annotation array) would be a breaking change
to the wave-3 poll contract and its golden tests (#193). So answers get their own `POST /api/{key}/answers`
+ `GET /api/answers` pair. Unifying answers into a single feedback stream is a possible wave-5 refinement
(when the handoff/feedback contract is finalized) — do NOT do it here.

The `target` (human/agent) is stored + echoed for the downstream handoff to route on; wave-4 does not
interpret it beyond preserving it.

**Scope boundary (harness-enforced):** Write only to `src/Charter.Server/`. Do NOT edit the tests,
`Charter.Core`, or the `sdk/`. An out-of-scope edit fails the task and consumes a retry. If the authored
tests are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` and stop rather than editing them.

**Completion criteria (match this task's guardrails):** `dotnet test --filter "Category=AnswerApi"` passes
(the answer round-trip, target preservation, CSRF rejection, and the key gate) AND the previously-green
`Category=AnnotationApi` / `Category=ReviewServer` tests still pass (the `/prompts` + `/poll` contract is
unchanged).
