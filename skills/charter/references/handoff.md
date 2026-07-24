# Handoff — offline export and the Guardrails handoff

Once the plan is approved in the review loop, two commands finish the job: `charter export` captures a
shareable offline snapshot (optional), and `charter handoff` converts the plan into the plain CommonMark
that Guardrails `plan-breakdown` consumes (required to feed the pipeline).

## `charter export` — a self-contained, offline artifact

```
charter export plan.mdx -o plan.html
```

`export` writes a **truly offline** HTML artifact:

- every **local asset** the plan references is inlined as a `data:` URI, so nothing loads from disk;
- any remaining **local path** is scrubbed (no `file://` or absolute paths leak into the file);
- it is **SDK-free** — like `render`, the review SDK is never baked in (invariant 1, *portable
  artifact*).

The difference from `render`: `render` produces a portable file but leaves local asset references as-is,
so it's the fast inner-loop check while you draft. `export` produces a file that survives being emailed,
attached to a ticket, or archived — it opens correctly on a machine that has never seen the plan's
directory. Use it when you need a snapshot of the approved plan to hand to a person, not to a tool.

## `charter handoff` — plain CommonMark for Guardrails

```
charter handoff plan.mdx -o plan.md --answers answers.json
```

`handoff` reads the reviewed plan and rewrites **every `:::` directive** — `:::note`, `:::warn`,
`:::comparison`, `:::diagram`, `:::custom-html`, `:::question`, … —
into **plain CommonMark**. This is deliberate (invariant 5, *feeds Guardrails via plain markdown*):
Guardrails does **not** parse Charter's directives, so coupling the two independently-versioned formats
would buy nothing. The output `plan.md` is what you pass to Guardrails `plan-breakdown`.

`--answers` is **optional**. Omit it and every `:::question` hands off as an open question — a legitimate,
common case when the human hasn't decided yet. Supply it to resolve questions to their chosen answers.

### The `--answers` JSON shape

A **flat object** mapping each question's `id` (the `id` you gave the `:::question` block, and the
`questionId` you drained from `GET /api/answers`) to an **array of answer value strings**:

```json
{
  "db-choice": ["Postgres"],
  "regions": ["us-east-1", "eu-west-1"],
  "notes": ["Keep the read path Postgres-only for v1."]
}
```

- Single-select / boolean / number → a **one-element** array (`["Postgres"]`, `["true"]`, `["3"]`).
- Multi-select → the **selected values** (`["us-east-1", "eu-west-1"]`).
- Free-text → the **text as one element** (`["Keep the read path Postgres-only for v1."]`).

This file is **hand-authored** — you write it from the answers you drained during review. It is
deliberately a plain file-in/file-out shape with no dependency on a running review server: `handoff` is
an offline command.

### Open question vs Answered

For each `:::question`, `handoff` emits one of two plain-markdown lines:

- **Unanswered** (no matching `id` in `--answers`, or no `--answers` at all) → an **"Open question"**
  line. Guardrails sees an unresolved decision it can surface for a human.
- **Answered** (a matching `id` with value(s)) → an **"Answered:"** line carrying the chosen value(s).

So the same plan hands off differently depending on what you supply:

```
# with --answers db-choice → ["Postgres"]
Answered: Which datastore should the service use? → Postgres

# without an answer for it
Open question: Which datastore should the service use?
```

### The end-to-end shape

1. Author `plan.mdx` (`references/authoring-plans.md`).
2. `charter render` to check, `charter review` to get in-browser feedback, draining `GET /api/poll` and
   `GET /api/answers` (`references/review-loop.md`); revise until approved.
3. Build `answers.json` from the `:::question` answers you drained.
4. Optionally `charter export plan.mdx -o plan.html` for a shareable offline snapshot.
5. `charter handoff plan.mdx -o plan.md --answers answers.json` → hand `plan.md` to Guardrails
   `plan-breakdown`.
