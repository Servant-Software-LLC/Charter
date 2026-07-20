## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (here, wave-qualified):
  `{ "wave-05-export-handoff/04-author-tests-handoff-markdown": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Charter's whole reason to exist is to feed Guardrails: the reviewed plan is handed off as **canonical
plain markdown** — invariant 5, "no MDX crosses the handoff." Charter's `:::` directives (`:::note`,
`:::warn`, `:::diagram`, `:::comparison`, `:::diff`, `:::question`) are Charter-authoring syntax Guardrails
never parses, so the handoff must convert every DIRECTIVE FENCE LINE to plain CommonMark before Guardrails
ever sees it. This task authors the **failing tests + minimal stub** for the new component that does that,
`Charter.Core.HandoffMarkdown`. Task `05-implement-handoff-markdown` fills in real logic. Do NOT implement
the real behavior yourself.

**"No directive leaks" means no LINE begins with `:::` — not "the substring `:::` never appears anywhere."**
This distinction is load-bearing, not pedantic: a Charter plan's ORDINARY PROSE is free to *talk about*
directive syntax (e.g. a sentence like `` use `:::note` for callouts `` — precisely the pattern this very
plan-of-record's own "Format & block catalog" section uses, and precisely the kind of content a real
Charter-about-Charter plan would legitimately contain). `HandoffMarkdown` passes Prose/Heading/List/Table/
Code blocks through VERBATIM — it never touches their text — so a mid-sentence mention of `:::note` as
documentation correctly survives into the handoff and is NOT a directive leak. Directive syntax is only
ever RECOGNIZED at the START of a line (mirroring how Markdig's own custom-container extension recognizes
it, and mirroring this repo's own established line-anchored-marker convention — see
`wave-04-rich-blocks/guardrails/02-union-clean.ps1`'s `(?m)^<<<<<<<` check for the identical false-positive
class). Every test below that asserts "no directive leaks" checks for a LINE-START `:::` (a `(?m)^:::`-style
regex), never a bare substring search.

Read the real materialized surface first (do not trust a remembered shape): `src/Charter.Core/BlockModel.cs`
(`BlockDocument.Parse`, `BlockKind`, each block's `RawContent` — for a `:::` container this SPANS the
whole container INCLUDING its opening `:::kind` and closing `:::` fence lines), `src/Charter.Core/QuestionSpec.cs`
(`QuestionSpec.Parse` — the question schema you resolve/flag), `src/Charter.Core/CharterRenderer.cs` (the
renderer you prove the handoff still parses through), `tests/Charter.Core.Tests/RendererGoldenTests.cs`
(the fixture style this repo uses).

**Write the minimal stub** in a NEW file `src/Charter.Core/HandoffMarkdown.cs`:

```csharp
namespace Charter.Core;

public static class HandoffMarkdown
{
    public static string Emit(string markdown, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers = null)
        => throw new NotImplementedException();
}
```

`answers` is the resolved-answer lookup: question id → the selected/submitted answer value(s), the same
shape as `Charter.Server.Answer.Values` (Charter.Core does not depend on Charter.Server, so this is a
plain dictionary, not that type). `null` or a missing key means the question was never answered.

**Write failing tests** in a NEW file `tests/Charter.Core.Tests/HandoffMarkdownTests.cs`, class
trait-tagged `[Trait("Category", "HandoffMarkdown")]`. Cover, each as its own `[Fact]`:

1. **Prose/heading/list/table/code pass through verbatim.** A document mixing a heading, a paragraph, and
   a fenced code block emits with that same content present, unchanged, in source order (nothing to
   convert — no directive involved).
2. **`:::note` → a labeled blockquote, fence gone.** For `:::note` / `An important note.` / `:::`, the
   output has NO line beginning with `:::` (assert via a `(?m)^:::` regex, not a bare substring search),
   contains a blockquote marker (`>` at line start), contains the label `**Note:**`, and contains the
   note's own text `An important note.`.
3. **`:::warn` → the same shape with the Warning label.** Same as case 2 but asserts `**Warning:**` and no
   line begins with `:::`.
4. **`:::comparison` → its already-plain inner list, fence gone.** For a `:::comparison` wrapping a
   markdown list of options, no output line begins with `:::` and every option's own text survives
   verbatim (the inner content is already plain CommonMark — only the fence lines are stripped).
5. **`:::diagram` → a fenced ` ```mermaid ` code block, fence gone.** For `:::diagram` / `graph TD; A-->B;`
   / `:::`, no output line begins with `:::`, the output contains a ` ```mermaid ` fence opener, and
   contains the raw Mermaid source text `graph TD` verbatim.
6. **`:::diff` → a fenced ` ```diff ` code block, fence gone.** For a `:::diff` wrapping unified-diff-style
   lines, no output line begins with `:::`, the output contains a ` ```diff ` fence opener, and contains at
   least one diff line's text verbatim.
7. **`:::question`, ANSWERED.** For a `:::question` body `{"id":"q1","title":"Pick one","mode":"single","options":["A","B"],"target":"human"}`
   and an `answers` dictionary containing `{"q1": ["A"]}`: no output line begins with `:::`, the output
   contains the question's title `Pick one`, contains the resolved answer `A`, **contains the literal
   resolved-format marker `Answered:` — a distinct assertion from test 8's `Open question` marker, so a
   test file cannot satisfy both scenarios with the same weak "some text is present" assertion**, does NOT
   contain an "open"/"unresolved" marker for this question, and does NOT contain the raw JSON body (assert
   the token
   `"mode"` is absent).
8. **`:::question`, UNANSWERED.** Same question body with `answers` either `null` or lacking a `q1` entry:
   no output line begins with `:::`, the output contains the title `Pick one`, DOES contain a
   clearly-flagged open/unresolved marker (e.g. the literal text `Open question`), and does NOT contain the
   raw JSON body.
9. **No directive FENCE LINES leak, globally (the proxy for invariant 5) — LINE-ANCHORED, not a bare
   substring search.** For a document containing ONE of every directive kind above, assert with a single
   `Regex.IsMatch(output, @"(?m)^:::")` (or equivalent) that returns `false` — no line in the WHOLE output
   begins with `:::`. Do NOT assert `output.Contains(":::")` — see test 12, which proves that stricter form
   is a FALSE POSITIVE trap.
10. **Self-parse round-trip.** `HandoffMarkdown.Emit(markdown)`'s output, fed back through
    `Charter.Core.CharterRenderer.Render(...)`, does NOT throw, and
    `Charter.Core.BlockDocument.Parse(...)Blocks.Count` on that same output is greater than zero for any
    non-empty input — proving the emitted markdown is itself well-formed input to Charter's own Markdig
    pipeline (this is the closest honest analogue of "the handoff round-trips through Charter's own
    renderer": the ORIGINAL document's directive blocks cannot literally survive unchanged — invariant 5
    requires converting them away — so this checks the HANDOFF's own self-consistency and validity, not
    identity with the original block set).
11. **No annotation-loop artifacts leak into the handoff.** The output NEVER contains `data-anchor`,
    `<script`, or `data-charter-sdk` — the handoff is plain markdown TEXT derived from the source `.mdx`,
    never a leaked fragment of rendered/annotated HTML.
12. **Prose that MENTIONS directive syntax mid-sentence is NOT treated as a leak (the false-positive
    regression test — the direct answer to why test 9 is line-anchored, not a bare substring check).** A
    plain paragraph block whose text is e.g. `` Use `:::note` for a callout and `:::diagram` for Mermaid. ``
    (no actual directive container involved — this is ordinary prose describing Charter's OWN syntax, the
    exact pattern this plan-of-record's own "Format & block catalog" table uses) passes through
    `HandoffMarkdown.Emit` UNCHANGED — assert the output contains that exact sentence verbatim, including
    its two `:::` mentions. This document trivially FAILS a bare `output.Contains(":::")` check (test 9's
    REJECTED alternative) while correctly PASSING the real, line-anchored `(?m)^:::` check — proving why
    the line-anchored form is the correct implementation of invariant 5, not just a stylistic choice.
13. **Cross-block source order is preserved in a MIXED document (order-fidelity regression test).** A
    single document containing, in this order, a heading, then a `:::note`, then a `:::diagram`, then a
    trailing paragraph, emits with that SAME relative order preserved — assert via `IndexOf`: the heading's
    text index < the note's text index < the `` ```mermaid `` fence's index < the trailing paragraph's
    text index. An implementation that (e.g.) groups output by block kind, or defers all directive
    conversions to the end, passes every single-block-kind test above while producing a reordered, useless
    handoff — this test is what catches that.

**Scope boundary (harness-enforced):** Write only to `tests/Charter.Core.Tests/HandoffMarkdownTests.cs`
and `src/Charter.Core/HandoffMarkdown.cs` (the stub). After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including any other file in
`src/Charter.Core/`, any `.csproj`, or `src/Charter.Cli/`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol elsewhere, do NOT edit that
file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Required coverage (a guardrail greps the HandoffMarkdownTests file — each MUST appear):**
`[Trait("Category", "HandoffMarkdown")]`, `HandoffMarkdown.Emit`, `mermaid`, `diff`, `Open question`, and
at least one real `[Fact]` or `[Theory]` attribute. Lower-bound presence checks — they do not substitute
for the real assertions above.

**Completion criteria (match this task's guardrails):** `tests/Charter.Core.Tests` BUILDS with the
`HandoffMarkdownTests` present and the stub compiling (all referenced types already exist), and
`dotnet test --filter "Category=HandoffMarkdown"` FAILS (every test throws `NotImplementedException`
against the stub). Failing at runtime is intended; not compiling is a mistake to fix.
