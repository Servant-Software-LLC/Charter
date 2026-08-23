namespace Charter.Core;

/// <summary>
/// What <c>charter handoff --fail-if-needs-human</c> found: the questions and defects that make an unattended
/// run unsafe to continue, and the <c>--answers</c> ids that matched nothing in the plan (Charter #172).
/// </summary>
/// <param name="Blockers">Everything that must be settled, in document order. Empty means nothing blocks.</param>
/// <param name="UnmatchedAnswerIds">
/// Ids present in the <c>--answers</c> file that no <c>:::question</c> in the plan carries, in the file's own
/// order. Charter has always discarded these SILENTLY, which is how a pipeline learns nothing from
/// "your answers file had three ids and none of them matched" — a stale id, a renamed question, or a
/// generator writing against the wrong plan all look like a clean run.
/// </param>
/// <param name="Questions">
/// Every READABLE <c>:::question</c> with the answer this run actually resolved it to, in document order — the
/// gate's own resolution pass, carried out rather than thrown away (Charter #187). It is what
/// <see cref="HandoffManifest"/> serializes, which is what keeps "one resolution pass, two artifacts" true:
/// the manifest never re-merges, so it cannot describe a different document from the one the gate judged.
/// </param>
/// <param name="MalformedQuestions">
/// How many <c>:::question</c> bodies would not parse. A separate count rather than something a consumer
/// derives from <paramref name="Blockers"/>, because a malformed question is absent from
/// <paramref name="Questions"/> entirely — Charter has no id to list it under — so without this a plan with a
/// broken question looks complete and entirely answered.
/// </param>
public sealed record HandoffGateResult(
    IReadOnlyList<HandoffBlocker> Blockers,
    IReadOnlyList<string> UnmatchedAnswerIds,
    IReadOnlyList<HandoffQuestionResolution> Questions,
    int MalformedQuestions)
{
    /// <summary>True when something in the plan needs a human before an unattended crew should proceed.</summary>
    public bool NeedsHuman => Blockers.Count > 0;
}

/// <summary>
/// One <c>:::question</c> as THIS run resolved it — the answer the flatten will print, and which input it came
/// from (Charter #187).
/// </summary>
/// <param name="Id">The question's declared id.</param>
/// <param name="Title">The question put to the reviewer. Presentational; not part of the manifest's contract.</param>
/// <param name="Target">The question's <c>target</c> token.</param>
/// <param name="SourceLine">The 1-based line the block starts at IN THE PLAN — never in the flattened output.</param>
/// <param name="Answer">The merged answer: the accepted <c>--answers</c> entry, else the plan's inline one.</param>
/// <param name="Source">
/// <see cref="HandoffGate.InlineAnswerSource"/> or <see cref="HandoffGate.AnswersFileAnswerSource"/>, or null
/// when <paramref name="Answer"/> records no decision — no value means no source, and a third token would be a
/// state a consumer has to learn.
/// </param>
public sealed record HandoffQuestionResolution(
    string Id,
    string Title,
    string Target,
    int SourceLine,
    IReadOnlyList<string> Answer,
    string? Source)
{
    /// <summary>
    /// True when <see cref="Answer"/> records a DECISION — <see cref="AnswerRules.IsDecision"/>, the one
    /// predicate the record, the flatten, the renderer and the missing-lean lint all read (Charter #188).
    /// </summary>
    /// <remarks>
    /// <b>This is NARROWER than the headless record's <c>answered</c>, and the difference is the point.</b> The
    /// record's is "the plan's own inline answer records a decision" — a pure function of the plan text, by
    /// contract. This is "the MERGED answer records a decision", so it sees the <c>--answers</c> file. One field
    /// name over two artifacts with two scopes is exactly the shape of defect Charter #188 was, so the manifest's
    /// contract test asserts it rather than leaving a reader to assume they match.
    /// </remarks>
    public bool Answered => AnswerRules.IsDecision(Answer);
}

/// <summary>One thing standing between the flattened plan and an unattended run.</summary>
/// <param name="Kind">
/// A stable hyphenated token: <c>unanswered-human-question</c>, <c>undecidable-agent-question</c>,
/// <c>malformed-question</c>, <c>unknown-directive</c>, <c>duplicate-question-id</c>.
/// </param>
/// <param name="Id">The question's id, or null where the defect has none (an unknown directive).</param>
/// <param name="Title">The question put to the reviewer, or null where there is none.</param>
/// <param name="Target">The question's <c>target</c> token, or null where it is unknown or not applicable.</param>
/// <param name="SourceLine">The 1-based markdown line it is about, or null for a document-wide fact.</param>
/// <param name="Detail">A human-readable explanation for the stderr line. Not a contract; do not parse it.</param>
public sealed record HandoffBlocker(
    string Kind, string? Id, string? Title, string? Target, int? SourceLine, string Detail);

/// <summary>
/// The strict-mode predicate for <c>charter handoff</c> (Charter #172): given a plan and the
/// <c>--answers</c> file that will be merged into it, decide whether the flattened output is safe to hand to
/// an unattended breakdown.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is evaluated AFTER <c>--answers</c> is merged</b>, which is the whole point of the flag: a pipeline
/// supplies an answers file and wants to know it was complete. That is why this could not simply reuse
/// <see cref="HeadlessRecord.NeedsHuman"/> — that property is computed from the plan text alone and has no
/// answers parameter, and giving it one would break its "pure function of the plan text" contract. What IS
/// shared is the <see cref="PlanInventory"/> both read, so the two can never disagree about which questions a
/// plan has.
/// </para>
/// <para>
/// <b>Three places where this is deliberately STRICTER than the record.</b> Each one is a case the record
/// treats as a warning because every other verb does, and where "every other verb" is the wrong precedent for
/// a flag whose entire purpose is refusing to certify what nobody checked:
/// </para>
/// <list type="number">
///   <item><description><b>The <c>target: agent</c> carve-out is narrowed to DECIDABLE agent questions.</b>
///     Charter #172 as filed assumed agent questions never count, on the stated grounds that the flattened
///     path "branches on <c>human</c> vs <c>agent</c>". It does not: neither literal the flatten emits
///     appears anywhere in Guardrails' source, docs or skills. So delegating is not a routing decision some
///     downstream honours — it is prose asking the next agent to decide. An agent question carrying
///     <c>options</c> or a <c>recommended</c> lean gives it something to decide WITH, and is delegated. A bare
///     free-text agent question with no lean gives it nothing, is invisible to both of Charter's existing
///     gates (<c>FindQuestionsMissingRecommendation</c> skips agent questions AND is scoped to select modes),
///     and would have Charter certify "no human needed" while the downstream invents an answer. That is not a
///     delegation; it is an unconstrained invention.</description></item>
///   <item><description><b>An unparseable <c>:::question</c> body blocks</b> — the record agrees, but note
///     WHY it matters more here: a body with a trailing comma flattens as
///     <c>&gt; **Malformed question (could not parse): …**</c>, which DELETES the question's title, id and
///     target from the handed-off document entirely.</description></item>
///   <item><description><b>An unknown <c>:::foo</c> directive blocks.</b> The record files it as a warning,
///     on the reasoning that widening the escalation rule would make the flag almost always true. That
///     reasoning does not transfer: a misspelled <c>:::questoin</c> classifies as an unknown directive, so
///     under the record's rule a hidden <c>target: human</c> decision exits 0 from BOTH verbs. Charter cannot
///     tell a misspelled question from a container the catalog genuinely does not define — the body is
///     preserved as prose either way and nothing interprets it — so the strict gate resolves the ambiguity
///     toward the human, which is the same principle the anchor model uses when it orphans rather than
///     misattributes.</description></item>
/// </list>
/// <para>
/// <b>What it does NOT do is refuse to write.</b> That is the caller's contract, and it is deliberate: every
/// exit 2 in this pipeline means <i>the output exists, go read it</i>. See the flag's help text and
/// <c>HeadlessExitCodes</c>.
/// </para>
/// </remarks>
public static class HandoffGate
{
    /// <summary>Wire tokens for <see cref="HandoffBlocker.Kind"/>.</summary>
    public const string UnansweredHumanQuestion = "unanswered-human-question";

    /// <summary>An agent-targeted question with no options and no recommended lean.</summary>
    public const string UndecidableAgentQuestion = "undecidable-agent-question";

    /// <summary>A <c>:::question</c> body that would not parse, so its target is unknown.</summary>
    public const string MalformedQuestion = "malformed-question";

    /// <summary>An unrecognized <c>:::foo</c> directive, which may be a misspelled <c>:::question</c>.</summary>
    public const string UnknownDirective = "unknown-directive";

    /// <summary>Two or more <c>:::question</c> blocks sharing an id.</summary>
    public const string DuplicateQuestionId = "duplicate-question-id";

    /// <summary>
    /// Wire token: the resolved answer came from the <c>answer</c> recorded INLINE in the plan, so
    /// <c>planSha256</c> alone reproduces the decision.
    /// </summary>
    public const string InlineAnswerSource = "inline";

    /// <summary>
    /// Wire token: the resolved answer came from the <c>--answers</c> file, so reproducing the decision also
    /// needs <c>answersSha256</c>.
    /// </summary>
    public const string AnswersFileAnswerSource = "answers-file";

    /// <summary>
    /// Evaluate <paramref name="markdown"/> against the answers that will be merged into its flatten.
    /// Deterministic and pure — the same inputs always give the same verdict, and nothing is read from disk.
    /// </summary>
    public static HandoffGateResult Evaluate(
        string markdown, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        var inventory = PlanInventory.Build(markdown);
        var blockers = new List<HandoffBlocker>();

        foreach (var note in inventory.Notes)
        {
            if (note.Kind == HeadlessNoteKind.MalformedQuestion)
            {
                blockers.Add(new HandoffBlocker(
                    MalformedQuestion, null, null, null, note.SourceLine,
                    "a :::question body would not parse, so its target is unknown and the flattened plan "
                        + "carries no id, title or target for it at all"));
            }
            else if (note.Kind == HeadlessNoteKind.UnknownDirective)
            {
                blockers.Add(new HandoffBlocker(
                    UnknownDirective, null, null, null, note.SourceLine,
                    "an unrecognized ::: directive is passed through as prose that nothing interprets; a "
                        + "misspelled :::question hides its target here"));
            }
        }

        foreach (var duplicateId in inventory.DuplicateQuestionIds)
        {
            blockers.Add(new HandoffBlocker(
                DuplicateQuestionId, duplicateId, null, null, null,
                "two or more :::question blocks share this id, so an answer resolves into all of them and "
                    + "`charter poll --apply` / `charter resolve` refuse the write"));
        }

        var resolutions = new List<HandoffQuestionResolution>(inventory.Questions.Count);
        foreach (var question in inventory.Questions)
        {
            // ONE merge per question per run, and its result is KEPT (Charter #187). The gate used to merge,
            // read the boolean and discard the value; the manifest then needed the same values and would have
            // merged a second time, which is a second chance to describe a document other than the one written.
            var resolved = AnswerRules.Merge(question, answers);
            resolutions.Add(new HandoffQuestionResolution(
                question.Id,
                question.Title,
                question.Target,
                question.SourceLine,
                resolved,
                ClassifyAnswerSource(question.Id, answers, resolved)));

            if (AnswerRules.IsDecision(resolved))
            {
                continue;
            }

            if (string.Equals(question.Target, "human", StringComparison.Ordinal))
            {
                blockers.Add(new HandoffBlocker(
                    UnansweredHumanQuestion, question.Id, question.Title, question.Target, question.SourceLine,
                    "an open question routed to a human, with nobody there to answer it"));
                continue;
            }

            if (!IsDecidable(question))
            {
                blockers.Add(new HandoffBlocker(
                    UndecidableAgentQuestion, question.Id, question.Title, question.Target, question.SourceLine,
                    "an open question delegated to an agent that carries neither `options` nor a "
                        + "`recommended` lean, so there is nothing to decide it with"));
            }
        }

        return new HandoffGateResult(
            blockers, UnmatchedAnswerIds(inventory, answers), resolutions, inventory.MalformedQuestions);
    }

    /// <summary>
    /// Which input <see cref="AnswerRules.Merge"/> just took <paramref name="resolved"/> from, or null when it
    /// records no decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It does not re-decide anything — it asks the merge what it did</b>, by identity against the merge's
    /// own return value. That is why it sits on the line after the merge call rather than in a helper somewhere
    /// else: a second implementation of "did the file win?" is a second implementation of the #186 rules, and
    /// the two would eventually disagree — at which point the manifest would name a source the flatten did not
    /// use, on a field whose entire job is saying which hash reproduces the decision.
    /// </para>
    /// <para>
    /// <b>A REJECTED entry therefore reads <c>inline</c>, which is the invariant that matters:</b> Merge falls
    /// back to the inline answer when the supplied value fails the rules, so <c>answers-file</c> can never mean
    /// "the file overrode the plan" (Charter #186 refuses that outright — the verb exits 1 before this is
    /// reached — and this is the in-library guarantee that no other caller can produce one either).
    /// </para>
    /// <para>
    /// A value RE-STATED verbatim by the file reads <c>answers-file</c>, because that is mechanically where the
    /// merge took it from, even though <c>planSha256</c> would reproduce it too. The field is defined by the
    /// mechanism precisely so it cannot drift from the emitter.
    /// </para>
    /// </remarks>
    private static string? ClassifyAnswerSource(
        string questionId,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answers,
        IReadOnlyList<string> resolved)
    {
        if (!AnswerRules.IsDecision(resolved))
        {
            return null;
        }

        return answers is not null
            && answers.TryGetValue(questionId, out var supplied)
            && ReferenceEquals(supplied, resolved)
                ? AnswersFileAnswerSource
                : InlineAnswerSource;
    }

    /// <summary>
    /// True when a delegated question gives the agent something to decide WITH — a declared option set, or the
    /// author's own lean. Anything else is a blank cheque.
    /// </summary>
    /// <remarks>
    /// Worth knowing which half does the work today, because it is not obvious from the rule:
    /// <see cref="QuestionSpec"/> DROPS a <c>recommended</c> that does not name a declared option (an
    /// authoring hint must never be able to break a plan), and a select mode is the only mode that requires
    /// options — so a lean can only survive on a question that already has options. In practice, therefore,
    /// <c>single</c>/<c>multi</c> agent questions are always decidable and <c>free-text</c>/<c>bool</c>/
    /// <c>number</c> agent questions never are. The <c>recommended</c> clause is kept because it is the rule
    /// as designed, and because it is what would make an option-less lean count the day the schema allows one;
    /// it is not load-bearing today. An author whose free-text agent question escalates has three honest
    /// remedies: give it options, answer it inline, or accept that a person should see it.
    /// </remarks>
    private static bool IsDecidable(HeadlessQuestion question)
        => question.Options.Count > 0 || question.Recommended is { Length: > 0 };

    /// <summary>
    /// The <c>--answers</c> ids that match no <c>:::question</c> in the plan, in the file's own order. Ids of
    /// questions whose BODY would not parse cannot be known, so a plan with a malformed question is already
    /// blocking on that count and this list is advisory beside it.
    /// </summary>
    private static IReadOnlyList<string> UnmatchedAnswerIds(
        PlanInventory inventory, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        if (answers is null || answers.Count == 0)
        {
            return Array.Empty<string>();
        }

        var known = new HashSet<string>(inventory.Questions.Select(question => question.Id), StringComparer.Ordinal);
        return answers.Keys.Where(id => !known.Contains(id)).ToList();
    }
}
