# Recap mode — reviewing a change that already happened

Charter's renderer and review loop are **direction-agnostic**. The same blocks and the same
comment-in-place surface work whether they describe a change *to be made* (a plan) or one *already made*
(a recap of a diff). Recap is the second direction, and everything downstream — `review`, `poll`,
`reply`, `resolve`, `export` — is unchanged. There is no separate recap loop to learn.

## When to use it

When the human wants to **look over a change that exists** and comment on it in place: a branch before it
is merged, a PR they would rather read as a document than as a file list, a "walk me through what you
just did." The deliverable is a `.charter.md`, so their notes come back to you through the ordinary drain.

**When NOT to use it.** A recap is not an execution report. Whether the tasks passed, what was retried,
how long a run took, which gates fired — that is Guardrails' `uber-report`, which owns that data. Charter
owns the *render + review surface over a diff*. If you find yourself writing about the run rather than the
change, you are in the wrong tool, and the two reports will contradict each other the first time they
disagree.

## The flow: seed → enrich → review

### Step 1 — Seed it

```sh
charter recap <range> -o <name>.charter.md
```

`<range>` is whatever git would take. `main..HEAD` recaps that range's commits; a single ref like `HEAD~3`
or `main` recaps that ref against the working tree. Add `--repo <dir>` to read a repository other than the
current directory.

You get a valid `.charter.md` containing exactly what the diff *states*:

- an overview table — range, files changed, `+`/`-` totals, commit count
- a commit table, when the range has one
- one **`:::diff` per file**, annotatable per line, under a heading naming the file and how it changed

That is the mechanical floor and it is deliberately all of it. `recap` is not a generator — the same rule
as `charter convert`. It never writes a summary, never groups changes into themes, never draws a
`:::diagram`, never invents a `:::question`. All four are judgment, and the binary holds no model.

**Read its stderr.** Every run prints what is still missing, and it will also warn you when the seed is
not the whole truth:

- a **capped** file (`--max-diff-lines`, default 400 per file) — the block says so too, but you should
  decide whether the rest matters
- a **binary** file — listed, with no reviewable diff

### Step 2 — Enrich it (this is the actual work)

Read the seed, then read enough of the diff to understand *why*, not just *what*. Then:

| Add | Where |
|---|---|
| **A summary** — what changed and why, in a few sentences a reviewer can read first | Top, under the title |
| **Theme grouping** — reorganize the per-file sections under headings like "The fix", "Test coverage", "Incidental cleanup" | Replaces the flat file list where it helps |
| **A `:::diagram`** — when the *shape* changed: a new component, a moved boundary, a changed sequence | Near the summary |
| **`:::note` / `:::warn`** — a consequence the diff does not show, a risk that survived | Beside the change it concerns |
| **`:::question`** — what you actually need the reviewer to decide | Wherever the decision belongs |
| **`:::comparison`** — when you chose between approaches and they should see the trade-off | Near that change |

Two judgment calls worth making deliberately:

- **Delete what does not earn its place.** A lockfile's 300-line diff is noise. Cut the block and say so
  in a line of prose — a recap the reviewer scrolls past is worse than a short one they read.
- **Lead with the risky change, not the alphabetically first file.** The seed is ordered by git; you know
  which change deserves attention.

A recap with no `:::question` is legitimate — sometimes you are reporting, not asking. But if there is a
decision you deferred while making the change, this is where it goes.

### Step 3 — Review it

Exactly as for a plan:

```sh
charter review <name>.charter.md
charter poll  <name>.charter.md --watch --apply
```

The reviewer annotates a diff **line**, a diagram **node**, or any block; you drain, revise, and
`charter reply` in-thread. See `review-loop.md`.

## Why the generated diffs use widened fences

A seed sometimes emits `::::diff` and `` ```` `` instead of the usual three characters. That is not
noise — it is the only correct output, and the reason is worth knowing if you hand-edit a diff block.

A diff of a markdown file can contain lines that are themselves markdown structure. Two of them destroy a
three-character block, **silently**:

- a context line whose trimmed text is `:::` **closes** the `:::diff` early — every later line vanishes
  from the block with no error. Being inside a code fence does *not* protect it: the container's close
  check runs first.
- a line reading `:::note` **opens a nested directive** and swallows the tail.

Widening past what the body contains defeats both. Diffing any `.charter.md` produces the first case, so
this is ordinary, not exotic. If you hand-author a `:::diff` whose body contains `:::` or a code fence,
widen it the same way.

## What recap does NOT change

- **The block catalog is the same** — recap emits only catalog blocks, and you enrich with catalog blocks.
  There is no `:::file-tree`; it has no renderer. See the `charter-format` skill.
- **Single-writer still holds** — you are the only writer of the recap, exactly as with a plan.
- **Handoff still works** — a recap is a `.charter.md`, so `charter handoff` converts it to plain
  CommonMark. Useful when a review turns up follow-up work: the answered questions become Guardrails'
  input, which is the natural bridge from "we reviewed what happened" to "here is what happens next."
