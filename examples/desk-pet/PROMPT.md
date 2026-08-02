# The prompt

This is the whole input. Paste it into Claude Code (with the `charter` skill installed) and the
agent authors `desk-pet.charter.md` from nothing.

It deliberately says **"chart"**, not "plan" — exercising the verb trigger added in
[#101](https://github.com/Servant-Software-LLC/Charter/issues/101). Before that change this prompt
activated nothing and the agent would simply have started writing code.

---

```text
Chart this out for me before you build anything.

I want a tiny terminal pet — call it Desk Pet — that lives in my shell and gets hungry when I
stop committing. I run `deskpet`, it shows me my pet's mood. I commit something, it perks up.
That's the entire product. One repo, one pet, one JSON state file. No server, no accounts.

Work through the behaviour, not the architecture — it's a toy, the interesting parts are how it
should make someone feel. In particular I haven't decided what actually counts as "feeding" it,
and I want to see the trade-offs side by side before I pick.

Flag anything you think is a bad idea, and leave the decisions you can't make for me open.
```

---

## Why this prompt produces a rich chart

Nothing in it names a block type. The richness comes from the shape of the request, which is the
point — a demo where the human dictates "add a comparison block" proves nothing.

| Phrase in the prompt | Block it naturally elicits |
|---|---|
| "Chart this out **before you build anything**" | Activates the skill at all (#101) |
| "gets hungry when I stop committing" | `:::diagram` — a mood state machine is the obvious way to express decay |
| "I haven't decided what counts as feeding it … **trade-offs side by side**" | `:::comparison`, annotatable per row |
| "**Flag anything you think is a bad idea**" | `:::warn` — the incentive problem |
| "**leave the decisions you can't make for me open**" | `:::question`, authored open |
| "a `post-commit` hook" (implied by "when I commit") | `:::diff`, annotatable per line |

## Rehearsal note

Live authoring is the impressive version, but it is also the version that can wander. The
committed `desk-pet.charter.md` next to this file is a known-good result of this prompt — if the
live run produces something weaker, fall back to it and keep going. The runbook says where.
