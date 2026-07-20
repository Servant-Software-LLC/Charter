## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-06-agent-skill-polish/02-refresh-readme-truthfulness": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Bring **`README.md`** up to date with what Charter actually does now. The current README still calls the
project an **"early scaffold"** and frames the renderer / review server / annotation loop as **future**
milestones ("How it will work (roadmap)", "_Coming with the first release._"). That is no longer true: the
renderer, the loopback review server, the in-place annotation loop, offline export, and the Guardrails
handoff are all **implemented and shipping in the binary**.

**Verify before you write.** Read `src/Charter.Cli/Program.cs` for the authoritative verb list. The real
commands are exactly:
- `charter render <plan.mdx> -o <out.html>` — render a plan to one portable HTML artifact.
- `charter review <plan.mdx> [--no-open]` — serve it over the loopback review server (`127.0.0.1`) and open
  the browser for in-place annotation.
- `charter export <plan.mdx> -o <out.html>` — write a self-contained, offline artifact.
- `charter handoff <plan.mdx> -o <out.md> [--answers <answers.json>]` — emit plain CommonMark for Guardrails.
- `charter --version`.

Do this:
- **Replace the stale status.** Remove the "Status: early scaffold" blockquote and the "_Coming with the
  first release._" line. State the truthful status: the renderer, review server, annotation loop, export, and
  handoff are implemented; the tool builds, tests green, and packs as a `dotnet` tool / native binary.
- **Turn the "roadmap" into real usage.** DELETE the "How it will work (roadmap)" heading entirely and
  replace it with a present-tense **`## Usage`** section (that exact heading) documenting the four verbs above
  with a concrete author → review → handoff example. Write it in the present tense — describe what the
  commands DO now, not what "you will run" later. You MAY point readers at the bundled usage skill at
  **`skills/charter/`**.
- **Stay honest about what is still future.** Keep a short, accurate forward-looking note for the genuinely
  deferred items — v2 recap mode, hosted share/publish, telemetry — all explicitly **out of v1** per
  `docs/plans/01-combine-lavish-and-visual-plan.md`. Do not claim published binaries, a live Homebrew tap, or
  a NuGet release exist yet (they are gated on a first real release); "build from source" plus the real verbs
  is the accurate usage story today.
- **Keep** the "Where it fits" pipeline diagram, the Acknowledgements, and the License sections intact.

**Scope boundary (harness-enforced):** Write only to `README.md`. After this task completes, the harness runs
a `git diff` check and rejects any edit outside that file — including `src/`, `tests/`, and `skills/`. An
out-of-scope edit fails the task immediately and consumes a retry. If a command's exact shape is unclear,
resolve it by reading `src/Charter.Cli/Program.cs` — do not invent.

**Completion criteria (match this task's guardrails):** `README.md` no longer contains any of the stale /
future-framing markers "early scaffold", "Coming with the first release", "How it will work", "(roadmap)",
"lands next", "next milestones", or "you will run"; it has a present-tense `## Usage` heading; and it
documents each real verb — `charter render`, `charter review`, `charter export`, and `charter handoff` — in
that Usage section.
