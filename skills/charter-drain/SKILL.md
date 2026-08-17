---
name: charter-drain
description: Use when a human hands you a Charter review round to pick up — "drain my review notes", "/charter-drain <plan>", "the review page told me to paste this to you", or after a reviewer clicks "Send to agent" on a served .charter.md. Attaches to the running review server for that plan, drains the queued annotations and question answers, applies the answers inline, and keeps listening for the rest of the review round. This is the agent side of the Charter review loop; a human should never run it themselves.
---

# Charter — drain a review round

> **This skill is version `@CHARTER_VERSION@`.**
>
> **Before your first `charter` command, run `charter --version` and compare.** It is one command, and it
> tells you two different things at once:
>
> | What you see | What it means | What to do |
> |---|---|---|
> | Version matches, no warning | Everything agrees | Carry on |
> | It prints a `skill(s) are out of date` warning | The copy **on disk** is older than the tool | `charter skills install --force`, **then restart this session** |
> | No warning, but the version differs from `@CHARTER_VERSION@` above | Disk and tool agree; **this session loaded an older copy** | **Restart this session** — re-installing changes nothing you can see |
>
> The second and third rows are different faults with different remedies, and only reading both numbers
> separates them. Installing without restarting is the trap: the files on disk change, the warning clears,
> and you keep running the copy you loaded at session start.
>
> Why this matters more here than for a reference document: this skill is a set of **CLI driving
> instructions**. A stale copy makes you call a surface that has moved — inventing flags that were renamed,
> or concluding a verb does not exist because your copy never listed it. That exact error has happened
> (Charter #138): `reply` shipped, the help banner omitted it, and an agent filed a bug asking for a feature
> that was already there.

A human has reviewed a `.charter.md` in the browser and handed the round to you. Your job is to collect
their feedback, act on it, and stay attached until the review is finished.

## Why this is a skill and not a command you hand to a person

`charter poll` is Charter's **agent IPC** — a thin HTTP client over the review server that is already
running. It is not a human surface, and the review page used to hand a human its raw command line. That
went exactly as badly as it sounds: run in a terminal it works perfectly, prints a wall of single-line JSON
envelopes, one every ~30 seconds forever — and **consumes the round**. Two annotations were drained out of
the queue into a console nobody was reading. Nothing said so.

The failure that matters is not the ugly output; it is that it *succeeded*. Handing over a skill
invocation instead fails loudly in the wrong hands — a human pasting `/charter-drain` into a shell gets
`command not found` and nothing is lost — and it keeps the flag mechanics where they belong.

## Do this

Take the plan path from the invocation (the review page fills it in). Then:

```
charter poll "<plan.charter.md>" --watch --apply
```

- `--watch` keeps the connection open for the rest of the round, so you receive later notes without being
  re-invoked. It re-arms its long poll after every cycle.
- `--apply` writes the drained question answers **inline** into the plan's `:::question` blocks. This is
  the living-document write: an answered question carries its `answer` in the file.

Each cycle emits one JSON envelope on stdout. Read them as they arrive.

## What to do with what you get

**Answers** are applied for you by `--apply` — the plan on disk is already updated. Read them anyway: an
`answer` that differs from the question's `recommended` is a human deliberately overriding your lean, and
the work must not drift back toward the option they rejected.

**Annotations** are yours to act on. Each carries the anchor it was written against, the quoted text for a
text-range note, and the reviewer's comment. For each one, either:

- **revise the plan** — edit the `.charter.md` and let the reviewer see the new version (the served page
  offers them a reload); or
- **reply** — `charter reply <plan> --id <annotation-id> --body "<your response>"` when the right answer is
  a response rather than an edit. A note you neither acted on nor answered is a note the human will assume
  you missed.

Nothing else drains the queue. You are the only reader, so a note you skip is simply gone.

## When the round is over

Stop when the reviewer stops — an empty queue is not the same as a finished review, so do not exit on the
first idle cycle. When the plan is settled and the reviewer is done, the page offers them
`/plan-breakdown <plan>` to hand the approved plan to Guardrails. **Stop your `--watch` first**: a
breakdown started underneath a running drain queues behind it and looks like nothing happened.

## If nothing is listening

`charter poll` discovers the session through a per-user registry, so it needs the review server for that
plan to be running. If it reports no session, the human's `charter review` has exited — tell them, rather
than starting one yourself: the server is theirs, and a second one on a new port would leave their browser
tab pointed at a dead address.
