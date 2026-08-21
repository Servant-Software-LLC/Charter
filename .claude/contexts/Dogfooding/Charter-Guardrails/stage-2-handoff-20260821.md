# Stage 2 (#201 / #226) — handoff to the Guardrails session

Written 2026-08-21 by the Charter-side session. Paste the block below to the Guardrails
session, or hand them this file.

Verified at time of writing: charter 0.23.0 (0 open issues), guardrails 1.8.0, all six
skills stamped to their tool. Issue states checked live — #452 and #459 had closed since
this session last looked, #458 had not.

---

```
=== Stage 2 (#201 / #226) — context and advice from the Charter-side session ===

Authoring the next charter is the right next artifact. Three things Stage 1 paid to
learn belong IN it, and one of them is a plan-time decision you cannot fix later.

--- 1. THE WRITESCOPE DECISION IS THE MOST IMPORTANT THING IN THIS CHARTER ---

Stage 1's tasks 02 and 06 both declared `src/Guardrails.Core/` as writeScope and ran in
the SAME tier, in parallel. Both had to allocate a diagnostic code, both appended near
the same marker, and the union conflicted. That cost a run abort, a corrupted SSOT
delivered to master, and ~$20 of re-attempts.

#458 is still OPEN and makes this sharper than it was: `AiMergeResolver.cs:86` is
literally `conflictedFiles[0]`, so **any union conflicting in 2+ files can never be
AI-resolved — deterministic and total**. Stage 1 conflicted in exactly two files;
`DiagnosticCodes.cs` was the second, which is why it still carried markers after a
"successful" merge.

#451 fixed the symptom, so you now get an honest needs-human rollback instead of a
corrupted delivery. That is a real improvement and it still stops the run.

Stage 2 (#226, the resolver) touches Core again — plausibly the registry, the action
model, DiagnosticCodes for new GR codes, and the scheduler. The surface is WIDER than
Stage 1's, not narrower.

So decide it in the charter, deliberately:
  - Can the Core-touching tasks be given DISJOINT writeScope (file-level, not
    `src/Guardrails.Core/`)?
  - If they genuinely must overlap, should they be SEQUENCED rather than parallel —
    accepting slower wall-clock to avoid a union that cannot resolve?
  - Whatever you choose, say WHY in the charter. `/guardrails-review` now has a probe
    for the trait-filter deadlock (#455) but none for "two same-tier tasks share a
    writeScope", so this one is still on the author.

--- 2. THE DIAGNOSTIC-CODE ALLOCATION IS A KNOWN COLLISION POINT ---

Two Stage 1 tasks independently needed a new GR code and both edited the same
"CURRENT next-free code" marker. The agents actually handled it WELL — task 02 skipped
GR2043 with a comment explaining it was allocated by the concurrent change — and the
merge still could not combine them.

If Stage 2 allocates codes, either pre-allocate them IN THE CHARTER (name the exact
codes each task takes) or give one task sole ownership of DiagnosticCodes.cs. Do not
leave two tasks to negotiate it at merge time; that negotiation is what has no
mechanism.

--- 3. SCOPE: WHAT DOES "STAGE 2" ACTUALLY COVER? ---

The epic's open children are #226 (the resolver), #227 (budget probes), #228
(escalation ladder), #229 (guardrails-review flags an untagged prompt-action), #230
(run report split by tier), #231 (steering), #349 (surface which model actually ran).

Stage 1's charter named #227/#228/#231 as v2 bets and tracked them by number — worth
keeping that discipline. But #349 and #230 are both "you cannot see what the tiering
did", and #226 without either means shipping a resolver whose effects are invisible.
Worth an explicit :::question on whether Stage 2 is #226 alone or #226 + observability.

--- TOOLCHAIN STATE (verified 2026-08-21) ---

  charter 0.23.0   — 0 open issues; skills charter / charter-format / charter-drain all 0.23.0
  guardrails 1.8.0 — skills plan-breakdown / guardrails-review / guardrails-domain-knowledge all 1.8.0

RESTART your session before authoring. Both installers say so and it is not boilerplate:
the loaded copy does not change when the file on disk does. plan-breakdown 1.8.0 carries
the #455 fix (task-level test filters scoped to the pair's own class); charter-format
0.23.0 carries the consumer-facing version range and the "do not guess at unknown
blocks" rule.

--- WHAT THE CHARTER REVIEW LOOP CAN DO NOW THAT IT COULD NOT IN STAGE 1 ---

Worth knowing, because it changes how the review round goes:

  - Threads are two-way. The reviewer can REPLY in a thread (v0.22.0), and the reply
    reaches you in the round it was written, on the `replies` array of the poll
    envelope with `replyTo` set. In Stage 1 a reviewer who disagreed with your reply
    could only resolve the note or start an unlinked one.
  - Your replies are attributed to YOU, not to the human whose log carries them. So
    push back in the thread when you think a note is a misreading — it will read as
    yours. Do NOT pass `--as-human`; the flag is now visible and it is for writing in
    their words, not yours. The reply flag is `--to`, not `--id`.
  - `render`/`review`/`handoff` WARN when a `:::question` has no `recommended`, and
    when a deferral names no tracking issue. Both fire at authoring time. Fix them
    before you hand the plan over rather than letting the reviewer find them.
  - A deferral pointing at a CLOSED issue is worse than an untracked one — it reads as
    covered. Check the ones you cite.

--- REMAINING HARNESS RISK FOR THE RUN ITSELF ---

  #458 OPEN — the 2+-file union limitation above. The single biggest run risk.
  #460 OPEN — `guardrails validate` reports only loader errors when loading fails, so
              semantic checks are skipped. A plan can look valid because it failed
              early.
  #456 OPEN — `--revalidate-task` refuses worktree mode, so a task stranded by a
              defective guardrail costs a full re-attempt rather than a re-check.

#452 (overwatcher no-op) and #459 are now CLOSED — the supervisor should actually
produce verdicts this time, which it did not in Stage 1.
```

---

## The one line to emphasise when handing this over

The charter is not the bottleneck — **the parallelism decision inside it is**. Stage 1 was
well-planned and still lost a run to two tasks sharing `src/Guardrails.Core/`, and #458
means that exact shape is now *guaranteed* to halt rather than merely likely to.

Cheap to decide while authoring; expensive to discover mid-run. Everything else here is
context; that is the part that belongs in the charter as a settled `:::question` with a
`recommended` lean.
