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
  demo.sh / demo.ps1   # a stepped driver for the loop, both shells
  README.md            # runbook: prerequisites, what to click, what to say, what to do when it breaks
```

Two conventions worth keeping:

**The committed `.charter.md` is the output of `PROMPT.md`, not something hand-written.** An
example whose chart was authored by hand proves nothing about whether the prompt works. Regenerate
it rather than editing it in place.

**Drivers work on a copy.** `charter resolve` writes answers back into the chart, so a script that
runs against the committed file leaves the next run starting from an already-answered plan. Copy to
a scratch directory first — `desk-pet/demo.sh` shows the pattern.
