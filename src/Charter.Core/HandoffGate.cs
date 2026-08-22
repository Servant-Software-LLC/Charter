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
public sealed record HandoffGateResult(
    IReadOnlyList<HandoffBlocker> Blockers,
    IReadOnlyList<string> UnmatchedAnswerIds)
{
    /// <summary>True when something in the plan needs a human before an unattended crew should proceed.</summary>
    public bool NeedsHuman => Blockers.Count > 0;
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

        foreach (var question in inventory.Questions)
        {
            if (ResolvedAnswer(question.Id, question.Answer, answers).Count > 0)
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

        return new HandoffGateResult(blockers, UnmatchedAnswerIds(inventory, answers));
    }

    /// <summary>
    /// The answer a <c>:::question</c> will actually flatten with: the <c>--answers</c> entry when the file
    /// carries this id, else the answer recorded INLINE in the plan.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="HandoffMarkdown"/>'s emitter on purpose. A gate that computed "answered"
    /// differently from the emitter would certify a document other than the one written — the exact failure
    /// this flag exists to prevent, one level up. Note that it preserves TODAY's behaviour verbatim, including
    /// the external file winning unconditionally and an empty value re-opening a settled question; changing
    /// that is Charter #186, and it is a one-place change now rather than two.
    /// </remarks>
    internal static IReadOnlyList<string> ResolvedAnswer(
        string questionId,
        IReadOnlyList<string> inlineAnswer,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
        => answers is not null && answers.TryGetValue(questionId, out var values) ? values : inlineAnswer;

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
