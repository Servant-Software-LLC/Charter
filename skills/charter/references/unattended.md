# Unattended runs — `charter headless`

```
charter headless plan.charter.md [--out-dir <dir>]
```

`charter headless` is the **unattended sibling of `charter review`** — for a firstmate crewmate, a CI job,
or any run with no human present. It **neither serves nor waits**: no server, no browser, no long-poll. It
renders the artifact, writes a forensic record beside it, and exits.

It is **not a replacement for review**. Review *elicits* decisions; headless *records that they were never
made*. If a human is available, run `charter review` — headless will not get you an answer.

## What it writes — two files, at derived names

- **The artifact.** The **same self-contained, offline, SDK-free** HTML `charter export` produces — the same
  exporter, **byte-identical**, pinned by a test. There is no second render path.
- **The record.** A JSON file holding what the review server would have held only in memory.

There is **no `-o`**. Both names are derived from the plan's own file name, so a collecting harness computes
them from the plan path with nothing passed and nothing configured:

| Plan | Artifact | Record |
|---|---|---|
| `storage.charter.md` | `storage.charter.html` | `storage.charter.headless.json` |

The stem is the plan's file name minus its **final** extension. They land **beside the plan** by default;
`--out-dir` **relocates the pair without renaming it** (the directory is created if missing). A derived name
that would land on the plan itself is **refused with exit 1**, never rendered over your source.

stdout is two plain lines; anything a human must deal with goes to stderr:

```
Exported plan.charter.md -> /abs/path/plan.charter.html
Recorded plan.charter.md -> /abs/path/plan.charter.headless.json
```

## The record

```json
{
  "schema": 1,
  "charterVersion": "<the tool version that produced this>",
  "plan": "plan.charter.md",
  "planSha256": "9f2c…",
  "artifact": "plan.charter.html",
  "needsHuman": true,
  "questions": [
    { "id": "db-choice", "title": "Which datastore should the service use?",
      "mode": "single", "target": "human", "options": ["Postgres", "DynamoDB"],
      "answered": false, "answer": [], "anchorId": "b0821…", "sourceLine": 42 }
  ],
  "notes": [
    { "kind": "missing-version-marker", "message": "…", "sourceLine": null }
  ],
  "sourceMap": { "b0821…": 42 }
}
```

- **`planSha256`** — the SHA-256 of the plan text as Charter read it. This is what proves *which revision* of
  the plan the artifact beside it was rendered from; the artifact alone cannot say.
- **`questions[]`** — every `:::question` in document order, with its `target`, its `answered` state, and its
  `answer`. `answered` is simply "a non-empty `answer`". `options` are kept even on an answered question —
  the **rejected** option is the rationale.
- **`notes[]`** — Charter's **own** diagnostics, the stderr warnings an agent-launched run may never show a
  human, made durable. `kind` is a **hyphenated** token: `missing-version-marker`,
  `unsupported-version-marker`, `duplicate-question-id`, `malformed-question`, `unknown-directive`.
  `sourceLine` is `null` for a document-wide note. These are **not** auto-generated review comments —
  synthesizing review prose is your job, not Charter's.
- **`sourceMap`** — anchor id → 1-based markdown line, in ascending line order.

**`anchorId` + `sourceMap` is the point of the record.** Together they let a human trace an artifact element
back to the markdown line it came from **offline, after the fact** — the round-trip the live review server
provides, made durable for a run nobody watched.

**The record is a pure function of the plan text and the tool version.** No clock, no random, no local path
(the plan and artifact appear as bare file names). So two runs over the same plan are **byte-identical** —
a harness can diff them cleanly — and the file is as safe to collect and hand on as the artifact beside it.

## Exit codes — a separate vocabulary from the drain's

`charter headless` has **its own** exit codes (`src/Charter.Cli/HeadlessExitCodes.cs`). They are
**deliberately not** `charter poll`/`charter resolve`'s `ReviewExitCodes`. Do not read one as the other —
the drain's `2` means *"a queue was found and it was empty"*, which is close to the opposite of this `2`.

| Code | Meaning | What you do |
|---|---|---|
| **0** | Both files are on disk and **nothing is outstanding**. | Proceed. |
| **2** | Both files are on disk **AND a human must decide or fix something**. An **escalation, not a failure** — nothing was lost, and the evidence was persisted *before* the code was decided. | Stop and escalate. Read `needsHuman`, `questions[]` and `notes[]` to learn which decisions went unmade. |
| **1** | Verb error — plan not found, a derived name that would overwrite the plan, an I/O failure. **The files may not exist.** | Fix the invocation. |

`needsHuman` in the record and the exit code are the **same fact**, so a harness reading the file and one
reading `$?` cannot disagree. On exit `2`, stderr also names each outstanding item with its line.

## What raises `needsHuman` — exactly three things

1. An **open `:::question` whose `target` is `human`** — the decision review existed to elicit, with nobody
   there to make it.
2. A **`:::question` whose body will not parse** — its `target` is unknown, and assuming `agent` would let a
   crew sail past a decision nobody can even read.
3. **Duplicate `:::question` ids** — an answer would resolve into every block sharing the id, so both
   `poll --apply` and `resolve` refuse the write and the plan cannot be settled unattended at all.

Nothing else raises it. A **missing or unsupported format-version marker** and an **unknown `:::foo`
directive** land in `notes[]` and **do not** change the exit code — every other verb treats those as warnings
too, and widening the rule would make the flag almost always true and therefore worthless.

> **Exit `0` does not mean every question is answered.** An open `:::question` with `target: agent` raises
> nothing and changes no exit code — by design, because **you** are the agent it is addressed to. Read
> `questions[]` on a `0` as well as on a `2`, and answer the `target: agent` ones yourself.

## What `charter headless` is not

- **Not a review.** Nothing is served, nobody answers a `:::question`, and no annotation is ever collected.
- **Not a handoff.** It writes no CommonMark. Beware the collision: *"the headless Guardrails path"* elsewhere
  in this skill means **which ingestion path Guardrails uses** (`charter handoff`'s flattened output). The
  `charter headless` verb means **how Charter runs with no human present**. They meet nowhere —
  `headless` emits no handoff markdown, and `handoff` writes no record.
- **Not a substitute for `export`.** If all you want is the artifact, `charter export -o <path>` still names
  its own output. `headless` adds the derived-path convention, the record, and the escalation code.
