## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-06-agent-skill-polish/01-author-charter-skill": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the **bundled `charter` usage skill** — the skill that ships WITH Charter and teaches a *downstream
agent* (one that will USE Charter to draft a plan) the **author → review → handoff** workflow. Write it at
the NORMAL repo path **`skills/charter/SKILL.md`** plus **`skills/charter/references/`** for playbook depth.
This directory does not exist yet — create it. It is a distribution artifact bundled with the binary, so it
lives at a normal repo path, **not** under `.claude/` — write the files directly with `Write` (no special
escape hatch is needed or wanted).

**This is NOT the internal dev-agent knowledge.** `.claude/skills/charter-domain-knowledge/SKILL.md` and
`.claude/skills/charter-dev-knowledge/SKILL.md` are for agents working ON Charter's own codebase. Read them
only as **structure/voice models** if useful — do NOT copy their content into the shipped skill, and do NOT
edit them.

### Ground every claim in the REAL, materialized tool (do not guess)

Before writing, read:
- **`src/Charter.Cli/Program.cs`** — the authoritative list of CLI verbs.
- **`docs/plans/01-combine-lavish-and-visual-plan.md`** — the SSOT for the block catalog and the load-bearing
  invariants. **CITE the block catalog from here; do not fork or re-invent it** (invariant 3: the format is
  single-sourced — the skill references it, the renderer owns it).

**The ONLY CLI verbs that exist** (verify against `Program.cs`):
- `charter render <plan.mdx> -o <out.html>` — render the plan to one **portable** HTML artifact (the SDK is
  injected only at serve time, never into this file — invariant 1).
- `charter review <plan.mdx> [--no-open]` — serve the rendered + SDK-injected plan over the **loopback**
  review server (`127.0.0.1`, per-session capability key — invariant 4) and open the browser for in-place
  annotation.
- `charter export <plan.mdx> -o <out.html>` — write a self-contained, **offline** artifact (local assets
  inlined as `data:` URIs, local paths scrubbed, SDK-free).
- `charter handoff <plan.mdx> -o <out.md> [--answers <answers.json>]` — convert the plan's `:::` directives
  to **plain CommonMark** for Guardrails `plan-breakdown` (invariant 5).
- `charter --version`.

**Do NOT reference any command that does not exist** — not `charter poll`, not `charter serve`, not
`charter annotate`, not `charter publish` — **not even to say they don't exist.** Describe only what exists.
In particular, the feedback drain is **not** a CLI verb: `charter review` serves the loopback server, the
human annotates elements / text ranges / diagram nodes and submits `:::question` answers in the browser, and
those are drained from the server's HTTP endpoints **`GET /api/poll`** (annotations, long-poll) and
**`GET /api/answers`** (question answers), each carrying the session `?key=`. Describe the drain via these
HTTP endpoints honestly — there is no `charter poll` command yet.

**Handoff reality:** `charter handoff` converts `:::note`/`:::warn`/`:::comparison`/`:::diagram`/`:::question`
etc. to plain markdown. An **unanswered** `:::question` becomes an **"Open question"** line; supplying
`--answers` (a hand-authored JSON like `{ "q1": ["A"], "q2": ["free text"] }` mapping question id → value(s))
resolves it to an **"Answered:"** line. The handoff is plain markdown by design — Guardrails does not parse
Charter's directives.

### Required shape of `skills/charter/SKILL.md` (keep it LEAN)

1. **YAML frontmatter** with a `name:` (e.g. `charter`) and a `description:` (a one-line "use this when…"
   trigger).
2. A **When to use** section.
3. The **AUTHOR → REVIEW → HANDOFF workflow**, naming the real verbs `charter render`, `charter review`,
   `charter export`, and `charter handoff` in order.
4. A **block catalog** section covering the `:::` directive blocks and the **`:::question`** elicitation
   block (cite the catalog from the SSOT plan).
5. **Pointers to `references/`** for depth (the lean-SKILL-with-references convention: SKILL.md stays short;
   the playbooks hold the detail). SKILL.md must reference the `references/` path.

### `skills/charter/references/` (author ALL THREE of these named playbooks — required, each substantive)

Use these exact filenames; each must carry real depth (a stub of a few bytes will fail the guardrails):

- **`references/authoring-plans.md`** — the block catalog in depth + a short sample `.mdx` skeleton.
- **`references/review-loop.md`** — running `charter review`; in-browser annotation of elements / text ranges
  / diagram nodes; and — the load-bearing part — **draining feedback via `GET /api/poll`** (queued
  annotations) **and `GET /api/answers`** (`:::question` answers) on the loopback server. There is no
  `charter poll` CLI verb, so this HTTP drain is how an agent reads the human's feedback — teach it explicitly.
- **`references/handoff.md`** — `charter export` (offline artifact) and `charter handoff` (→ plain CommonMark;
  the `--answers` JSON shape; Open-question vs Answered).

**Scope boundary (harness-enforced):** Write only under `skills/charter/`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that directory — including `src/`, `tests/`,
`README.md`, and anything under `.claude/`. An out-of-scope edit fails the task immediately and consumes a
retry. If a command's exact shape is unclear, resolve it by reading `src/Charter.Cli/Program.cs` — do NOT
invent a verb or flag, and do NOT write `{"needsHuman": …}` for something the materialized code already
answers.

**Completion criteria (match this task's guardrails):** `skills/charter/SKILL.md` exists with YAML
frontmatter (`name:` + `description:`), a "When to use" section, a block catalog naming `:::question`, and a
`references/` pointer; `skills/charter/references/` holds all three named playbooks (`authoring-plans.md`,
`review-loop.md`, `handoff.md`), each substantive; the skill names each real verb `charter render` /
`charter review` / `charter export` / `charter handoff`; it teaches the feedback drain via **both**
`/api/poll` and `/api/answers`; and it references **no** non-existent verb (`charter poll` / `charter serve` /
`charter annotate` / `charter publish` / `charter drain` / `charter preview` / `charter comment` /
`charter share`).
