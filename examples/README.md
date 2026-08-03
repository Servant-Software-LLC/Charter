# Examples

Complete, runnable Charter examples. Each one is a real `.charter.md` plus everything needed to
drive it through the author → review → handoff loop.

| Example | What it shows |
|---|---|
| [`desk-pet/`](desk-pet/) | The full loop from a single prompt: the agent picks its own blocks, a human annotates a table row / diagram node / diff line, answers fold back into the file, and the result flattens to CommonMark. Doubles as the demo runbook. |

## What an example contains

```
<name>/
  PROMPT.md            # the prompt a human actually types — the entry point
  <name>.charter.md    # the chart that prompt produced (committed, known-good)
  RUNBOOK.md           # the real charter commands, in order, with what to click and what to say
  README.md            # what the example is, prerequisites, and how to run it
```

Two conventions worth keeping:

**The committed `.charter.md` is the output of `PROMPT.md`, not something hand-written.** An
example whose chart was authored by hand proves nothing about whether the prompt works. Regenerate
it rather than editing it in place.

**No driver scripts.** An example teaches the tool's actual surface, so the runbook lists real
`charter` commands rather than wrapping them. A wrapper hides the journey and implies Charter needs
one.

**Run against a scratch copy.** `charter resolve` writes answers back into the chart, so working
directly on the committed file leaves the next run starting from an already-answered plan.
