# Handoff — offline export and the Guardrails handoff

Once the plan is approved in the review loop, two commands finish the job: `charter export` captures a
shareable offline snapshot (optional), and `charter handoff` converts the plan into the plain CommonMark
that Guardrails `plan-breakdown` consumes (required to feed the pipeline).

## `charter export` — a self-contained, offline artifact

```
charter export plan.charter.md -o plan.html
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
charter handoff plan.charter.md -o plan.md --answers answers.json
```

`handoff` reads the reviewed plan and rewrites **every `:::` directive** — `:::note`, `:::warn`,
`:::comparison`, `:::diagram`, `:::custom-html`, `:::question`, … — into **plain CommonMark**. This is the
**headless** half of Charter's dual handoff (invariant 5, *dual handoff to Guardrails*): the flattened
`plan.md` is what the autonomous Guardrails `plan-breakdown` path consumes. The **interactive**
`/plan-breakdown` doesn't need it — it reads the `.charter.md` directly, interpreting the `:::` blocks via
the `charter-format` skill. Reach for `handoff` when you're feeding the headless path.

> **Guardrails compatibility.** Direct `.charter.md` ingestion (the interactive path) requires
> **Guardrails ≥ `1.0.0-preview.48`** — the release that implements it (Guardrails #390–393). Against **any
> earlier Guardrails**, run `charter handoff` and feed the flattened `plan.md` instead: the flatten path has
> **no version floor** and is supported permanently. When unsure which the target Guardrails supports,
> `handoff` always works.

`--answers` is **optional**. A `:::question` already resolved **inline** — its `answer` filled in by
`charter poll --apply` / `charter resolve` during review — hands off as **Answered** on its own, because
`handoff` reads the inline answer. `--answers` is for questions **not** already answered inline: supply it
to resolve them (a matching `id` in `--answers` takes precedence over an inline answer). Omit it, and any
question with no inline answer hands off as an **open question** — a legitimate, common case when the human
hasn't decided yet.

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

- `single` / `bool` / `number` → a **one-element** array (`["Postgres"]`, `["true"]`, `["3"]`).
- `multi` → the **selected values** (`["us-east-1", "eu-west-1"]`).
- `free-text` → the **text as one element** (`["Keep the read path Postgres-only for v1."]`).

This file is **hand-authored** — you write it from the answers you drained during review. It is
deliberately a plain file-in/file-out shape with no dependency on a running review server: `handoff` is
an offline command.

### Open question vs Answered

For each `:::question`, `handoff` emits one of two plain-markdown lines:

- **Answered** (a matching `id` in `--answers`, or an `answer` already filled in inline) → an
  **"Answered:"** line carrying the chosen value(s).
- **Open** (no inline `answer` and no matching `--answers` id) → an **"Open question (unresolved)"** line.
  Guardrails sees an unresolved decision it can surface for a human.

So the same plan hands off differently depending on what you supply:

```
# with --answers db-choice → ["Postgres"]
Answered: Which datastore should the service use? → Postgres

# without an answer for it
Open question: Which datastore should the service use?
```

### The end-to-end shape

1. Author `plan.charter.md` (`references/authoring-from-source.md`, `references/authoring-plans.md`).
2. `charter render` to check, `charter review` to get in-browser feedback — drain it with `charter poll`
   and fold answers inline via `poll --apply` / `charter resolve` (`references/review-loop.md`); revise
   until approved.
3. *(Optional)* Build `answers.json` for any `:::question` not already resolved inline (or to override one).
4. Optionally `charter export plan.charter.md -o plan.html` for a shareable offline snapshot.
5. `charter handoff plan.charter.md -o plan.md [--answers answers.json]` → hand `plan.md` to the headless
   Guardrails `plan-breakdown` path. (The interactive `/plan-breakdown` skips this and reads the
   `.charter.md` directly.)
