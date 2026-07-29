---
name: charter-format
description: The normative Charter `.charter.md` block catalog and `:::question` open/resolved schema — the single format source of truth cited by the charter authoring skill (to WRITE blocks) and by Guardrails plan-breakdown (to INTERPRET them). Use whenever you must read, write, or interpret Charter `:::` directive blocks.
format-version: 1
format-min: 1
---

# charter-format — the normative Charter block catalog

A Charter deliverable is a `.charter.md` file: **CommonMark prose plus `:::` directive containers**
(Markdig custom containers), each validated against a C# record in `Charter.Core`. This skill is the
**single source of truth** for that format — the block catalog, each block's semantics, and the
`:::question` open/resolved rule. It is bound to the renderer by a drift test (`Charter.Core.Tests`), so
this catalog and the real `BlockKind` set / `QuestionSpec` fields can never silently diverge.

Do not invent, fork, or vendor this catalog. Cite this skill.

## Format version

This skill declares the format range it understands in its frontmatter:

- `format-version: 1` — the newest catalog version this skill defines (`skillMax`).
- `format-min: 1` — the oldest file format it still understands (`skillMin`).

A `.charter.md` stamps the format it was authored against in a plain-YAML frontmatter marker
(`charter-format-version: F`, readable without this skill). It is consumable iff
`format-min ≤ F ≤ format-version`. **Any change to the catalog below bumps `format-version`** — the drift
test binds the version to the code, so a semantic change with no bump fails the build.

## The block catalog (the only blocks that exist)

Primitives are plain CommonMark. The `:::` directives are the rich, validated blocks.

| Block | Syntax | Semantics |
|---|---|---|
| prose / heading / list / table / code | plain CommonMark | Narrative and data. Annotatable as a whole block, or as a text range inside it. (Per-line sub-anchors exist only for `:::diff`; a code block has none.) |
| note callout | `:::note` | An aside. Rendered as a callout; annotatable as a whole element. |
| warn callout | `:::warn` | A risk the reviewer must not miss. A callout; annotatable as a whole element. |
| comparison | `:::comparison` | Options weighed side by side — a pipe table or list body. Annotatable **per row**. |
| diagram | `:::diagram` | A Mermaid diagram. Body is Mermaid source — raw, **or** wrapped in a fenced ` ```mermaid ` block (both accepted; see below). Rendered theme-aware; annotatable **per node**. |
| diff | `:::diff` | A unified diff. Body is diff lines — raw, **or** wrapped in a fenced ` ```diff ` block (both accepted; see below). Annotatable **per line** (add / remove / context). |
| custom HTML | `:::custom-html` | The sanctioned raw-HTML escape hatch — its body is passed through live (every other surface escapes raw HTML). Reach for it last. |
| **question** | **`:::question`** | The elicitation block — asks the human (or downstream agent) to decide something inside the plan. Validated JSON body; see below. |

There is **no** `:::file-tree` and **no** `:::annotated-code`. They have no renderer — do not author them,
and treat any other unknown `:::foo` as an unknown directive, never as a known block.

**Unknown-directive interop rule.** An unknown `:::foo` is flagged as unknown (never silently promoted to a
note) — but its **body is preserved and parsed through as prose context, never silently dropped**. It is
ordinary plan content that happened to sit behind a directive the catalog does not define (usually a typo);
discarding it would lose real content. Every consumer — renderer, handoff, and any interpreting agent — keeps
both the visible "unknown directive" marker *and* the body.

### `:::diagram` and `:::diff` body forms

Both containers accept **two** body forms, and both mean exactly the same thing. Write either; interpret both.

**Fenced body** (the form the authoring examples use — it keeps editors syntax-highlighting the source):

````markdown
:::diagram
```mermaid
graph TD; Draft --> Review --> Handoff;
```
:::
````

**Raw body** (no inner fence):

````markdown
:::diagram
graph TD; Draft --> Review --> Handoff;
:::
````

`:::diff` is identical, with ` ```diff ` as the inner fence:

````markdown
:::diff
```diff
+ added line
- removed line
  context line
```
:::
````

````markdown
:::diff
+ added line
- removed line
  context line
:::
````

When flattening, the body is emitted as **exactly one** fenced code block of the matching language — an
already-fenced body is unwrapped first, never double-fenced.

## The `:::question` block — open vs. resolved

The body is a JSON object (JSON is a subset of YAML, so the parser stays dependency-agnostic) validated to
`QuestionSpec` in `Charter.Core`. Its fields:

| Field | Type | Required | Meaning |
|---|---|---|---|
| `id` | string | yes | Stable, **document-unique** question id. (Two questions sharing an id is a review-time error — an answer would resolve into both.) |
| `title` | string | yes | The question shown to the reviewer. |
| `mode` | string | yes | One of `single` / `multi` / `free-text` / `bool` / `number`. |
| `options` | array of strings | for `single`/`multi` | The choices. Required and non-empty for the select modes; unused otherwise. |
| `target` | string | yes | `human` or `agent` — who the resolved answer routes to. |
| `answer` | array of strings | no | **The open/resolved marker.** Absent or empty ⇒ the question is **open**. Non-empty ⇒ **resolved**, carrying the chosen value(s). |

The `answer` shape mirrors a submitted answer's values: a `single`/`bool`/`number` answer is one element, a
`multi` answer is the selected values, and `free-text` is the text as one element.

**Open** (as authored):

````markdown
:::question
{ "id": "db-choice", "title": "Which datastore for the read path?",
  "mode": "single", "options": ["Postgres", "DynamoDB"], "target": "human" }
:::
````

**Resolved** (the `answer` key is added on drain — every other key is preserved):

````markdown
:::question
{ "id": "db-choice", "title": "Which datastore for the read path?",
  "mode": "single", "options": ["Postgres", "DynamoDB"], "target": "human", "answer": ["Postgres"] }
:::
````

Interpreting a question: a **resolved** question is a settled decision — fold its `answer` in, keeping the
`options` as rationale. An **open** question must be surfaced, never silently defaulted.
