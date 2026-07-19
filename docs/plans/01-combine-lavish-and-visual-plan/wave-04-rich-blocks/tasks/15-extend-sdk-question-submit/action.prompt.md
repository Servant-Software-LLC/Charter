## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-04-rich-blocks/15-extend-sdk-question-submit": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Extend the embedded JS SDK **`sdk/charter-annotate.js`** to wire the `:::question` `<form>` **submit** to the
server. Read the current file first — it already exposes `window.CharterAnnotate` (element / text-range /
diagram-node annotation over a postMessage boundary, POSTing annotations to `/api/{key}/prompts`, listening
on `/events`, reading the `?key=` capability key). ADD the answer-submission path **without disturbing** any
of that existing behavior, the `CharterAnnotate` namespace, or the MIT/Lavish attribution header.

Add:

- **Question-form submit handling.** When a rendered `:::question` `<form>` (the native form the renderer
  emits — the block root carries the block's stable id and the question id, e.g. `data-question-id`) is
  submitted, intercept it (`preventDefault`), collect the structured answer — the `questionId`, the `mode`,
  the selected `values` (radio → one value, checkbox → many, free-text/number → the field value, bool → the
  checkbox state), and the `target` (read from the form if the renderer emits it) — and **POST** it as JSON
  to `/api/{encodeURIComponent(key)}/answers` (the route task 14 provides), riding the same `?key=`
  capability key the SDK already reads. Emit the submit/submitted/error over the existing postMessage
  channel (so a host frame or a headless driver can observe it), exactly as the annotation `submit()` path
  does. Do NOT reach into server internals — the ONLY crossing is the postMessage channel (page side) or the
  HTTP POST to the defined route (server side) — invariant 6.
- Keep it lean and dependency-free (no bundler, no npm). This stays the ONLY JS in the project and is
  injected serve-time only (invariant 1 — the saved artifact stays SDK-free).

**Scope boundary (harness-enforced):** Write only to `sdk/`. Do NOT modify `src/`, `tests/`, or any
`.csproj` — the existing `Charter.Server.csproj` `<EmbeddedResource>` already points at
`sdk/charter-annotate.js`, so the server re-embeds this file on rebuild with no wiring change. An
out-of-scope edit fails the task and consumes a retry.

**Note on verification (honest gap — Charter #8):** there is no JS test/lint/bundle toolchain in this repo,
so this task's guardrail is a deterministic STATIC check only (the answers-submit surface is present and the
existing SDK surface + attribution survive). The SDK's actual browser BEHAVIOR — that submitting a rendered
question form in a real browser POSTs the answer and it round-trips — is verified server-side by the
`Category=AnswerApi` round-trip test (tasks 13/14) and otherwise surfaced as a real-browser
decision/honest-halt; it is NOT auto-verified here.

**Completion criteria (match this task's guardrails):** `sdk/charter-annotate.js` still exposes
`CharterAnnotate` with the MIT/Lavish attribution and the element/text-range/diagram-node annotation surface,
AND now carries the answers-submit path (a `/api/`…`answers` POST wired to question-form submit).
