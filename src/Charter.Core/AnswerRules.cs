using System.Globalization;

namespace Charter.Core;

/// <summary>
/// One <c>--answers</c> entry Charter refused to honour, and why (Charter #186).
/// </summary>
/// <param name="QuestionId">The <c>:::question</c> id the entry named.</param>
/// <param name="SourceLine">The 1-based markdown line that question starts at, or null when it is unknown.</param>
/// <param name="Reason">
/// A human-readable explanation for the stderr line. Not a contract; do not parse it. It always names the
/// offending value and what would have been acceptable, because the caller's next act is editing that file.
/// <b>ASCII only</b>, like every other line Charter writes to stderr, so the bytes do not depend on the
/// console encoding of the OS that produced them.
/// </param>
public sealed record AnswerRejection(string QuestionId, int? SourceLine, string Reason);

/// <summary>
/// <b>The one place that decides what an answer MEANS</b> — whether a value array records a decision at all
/// (Charter #188), and whether an out-of-band <c>--answers</c> entry may settle a <c>:::question</c>
/// (Charter #186).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one type.</b> The merge and the rules that constrain it cannot live apart: the gate
/// (<see cref="HandoffGate"/>) and the emitter (<see cref="HandoffMarkdown"/>) must resolve every question to
/// the same value or the gate certifies a document other than the one written — and the moment "the external
/// dictionary wins" became "the external dictionary wins IF it passes the rules", the rules became part of the
/// merge. <see cref="Merge"/> replaces <c>HandoffGate.ResolvedAnswer</c> for exactly that reason.
/// </para>
/// <para>
/// <b>The three decisions of Charter #186</b>, each one the opposite of what shipped in 0.24.0:
/// </para>
/// <list type="number">
///   <item><description><b>A supplied value is checked against the question's <c>mode</c> and
///     <c>options</c>.</b> A violation is a REJECTION: the value never becomes the resolved answer, and
///     <c>charter handoff</c> refuses the whole run rather than writing a plan that asserts an impossible
///     decision (see the verb, which exits 1 and writes nothing).</description></item>
///   <item><description><b>An empty or null value is an ERROR, not an erasure.</b> "This question was not
///     answered here" is already spelled by omitting the id, so <c>[]</c> has no honest meaning left — and
///     reading it as an erasure is what let a generator un-answer a question a human had settled.</description></item>
///   <item><description><b>An answers file may FILL an unanswered question and may RE-STATE the answer the
///     plan already records — never REPLACE it.</b> This is the load-bearing one. The living-document model's
///     whole claim is that a resolved question carries its answer inline and that answer is DURABLE; a channel
///     that can quietly replace it makes "durable" false. Recording the answer's SOURCE (Charter #187) was the
///     alternative and it makes an override auditable rather than safe — the flattened plan would still assert
///     the overriding value, <c>plan-breakdown</c> would still read it as settled, and the audit would live in
///     a side file the consumer may never open. An audit trail is the right answer for something you must
///     allow; it is the wrong answer for something you should not do. Re-stating an identical value is
///     accepted so that a generator supplying its whole answer set on every run does not break the day one of
///     those questions gets answered inline: the rule is <i>an answers file may only ADD information</i>.</description></item>
/// </list>
/// <para>
/// <b>Two asymmetries, named rather than left to be discovered.</b>
/// </para>
/// <list type="bullet">
///   <item><description><b>An INLINE <c>answer</c> is never checked for membership; a supplied one is.</b>
///     That is not an oversight — <c>charter-format</c> states it as a rule, because the renderer appends a
///     "Something else" write-in to every select form (Charter #109) and a reviewer's write-in is the
///     decision, not corruption. An <c>--answers</c> file is not a reviewer at a page: it is a machine input
///     with no human behind it, and the flatten already instructs a delegated agent to <i>choose one of the
///     options above</i>. An agent that genuinely needs a write-in has the honest path — record it inline, in
///     the living document, where every other decision lives.</description></item>
///   <item><description><b>A <c>free-text</c> question can only be checked for SHAPE.</b> It declares no
///     options, so there is no set to test a value against, and the checkable facts reduce to its mode's:
///     one value, and not a blank one. The rule is <i>a supplied answer must be something the question's
///     DECLARED shape can accept</i>; <c>free-text</c> simply declares less shape than <c>single</c>.</description></item>
/// </list>
/// <para>
/// <b>Pure in its inputs.</b> No clock, no path, no I/O — the same contract <see cref="PlanInventory"/> and
/// <see cref="HeadlessRecord"/> keep, so a verdict is reproducible from the plan text and the answers alone.
/// </para>
/// </remarks>
public static class AnswerRules
{
    /// <summary>
    /// <b>The ONE definition of "these values record a decision"</b> (Charter #188): at least one value, and
    /// no value that is empty or whitespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to be <c>Count &gt; 0</c> — counting ELEMENTS, not content — in three places at once: the
    /// forensic record's <c>answered</c>, the renderer's resolved-question display, and the flatten's
    /// Answered/Open branch. So <c>[""]</c> reported the question answered and the flattened plan emitted
    /// <c>**Q: …** — Answered:</c> with nothing after it, which is exactly what a bad <c>jq</c> in a
    /// machine-generated answers file produces. Any strict gate built on it then certified a blank decision as
    /// a made one.
    /// </para>
    /// <para>
    /// <b>A blank value ANYWHERE disqualifies the whole array</b>, rather than the array counting as answered
    /// on the strength of its non-blank members. A <c>multi</c> answer carrying a blank element is a defective
    /// answer, not a partial one — the blank came from the same producer as the real values, so nothing about
    /// it is more trustworthy than a lone <c>[""]</c>.
    /// </para>
    /// </remarks>
    public static bool IsDecision(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The answer a <c>:::question</c> will actually flatten with: the <c>--answers</c> entry when the file
    /// carries this id AND that entry passes <see cref="Check(HeadlessQuestion, IReadOnlyList{string})"/>,
    /// else the answer recorded INLINE in the plan.
    /// </summary>
    /// <remarks>
    /// A rejected entry falls back to the inline answer rather than winning or erasing, which is what makes the
    /// strict gate compose: a rejected value never becomes the resolved answer, so a question it failed to
    /// settle is still open and still blocks. The verb refuses the run outright before this is ever reached
    /// (exit 1, nothing written); this fallback is the in-library guarantee that no caller of
    /// <see cref="HandoffGate.Evaluate"/> or <see cref="HandoffMarkdown.Emit"/> can certify a rejected value
    /// however it reaches them.
    /// </remarks>
    public static IReadOnlyList<string> Merge(
        QuestionSpec spec, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return Merge(
            spec.Id, QuestionSpec.Token(spec.Mode), spec.Options, spec.Answer, answers);
    }

    /// <summary>The same merge, for the record's view of a question. See the <see cref="QuestionSpec"/> overload.</summary>
    public static IReadOnlyList<string> Merge(
        HeadlessQuestion question, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        ArgumentNullException.ThrowIfNull(question);

        return Merge(question.Id, question.Mode, question.Options, question.Answer, answers);
    }

    /// <summary>
    /// Judge every <c>--answers</c> entry that names a <c>:::question</c> in <paramref name="markdown"/>, in
    /// document order. Empty means the file is usable against this plan.
    /// </summary>
    /// <remarks>
    /// <b>Every violation is reported, never just the first</b> — a pipeline fixing an answers file one exit
    /// code at a time is a pipeline running many times to learn what it could have learned once. Ids matching
    /// no question are deliberately NOT judged here: an unmatched id is reported and never a veto (Charter
    /// #172 §4.2), and turning one into an exit code by the back door would break a rule that was decided
    /// separately.
    /// </remarks>
    public static IReadOnlyList<AnswerRejection> CheckAll(
        string markdown, IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        if (answers is null || answers.Count == 0)
        {
            return Array.Empty<AnswerRejection>();
        }

        var rejections = new List<AnswerRejection>();
        foreach (var question in PlanInventory.Build(markdown).Questions)
        {
            if (answers.TryGetValue(question.Id, out var supplied)
                && Check(
                    question.Id, question.Mode, question.Options, question.Answer, supplied,
                    question.SourceLine) is { } rejection)
            {
                rejections.Add(rejection);
            }
        }

        return rejections;
    }

    private static IReadOnlyList<string> Merge(
        string questionId,
        string modeToken,
        IReadOnlyList<string> options,
        IReadOnlyList<string> inlineAnswer,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answers)
    {
        if (answers is null || !answers.TryGetValue(questionId, out var supplied))
        {
            return inlineAnswer;
        }

        return Check(questionId, modeToken, options, inlineAnswer, supplied, sourceLine: null) is null
            ? supplied
            : inlineAnswer;
    }

    /// <summary>
    /// The rule kernel, in the order that gives the caller the most actionable sentence first: the value's own
    /// legality, then its legality for this question's mode, then — last — whether it is allowed to land at
    /// all. Ordering matters because a value can break several rules at once, and "Cassandra is not one of the
    /// options" is a more useful first sentence than "you may not overwrite Postgres".
    /// </summary>
    private static AnswerRejection? Check(
        string questionId,
        string modeToken,
        IReadOnlyList<string> options,
        IReadOnlyList<string> inlineAnswer,
        IReadOnlyList<string> supplied,
        int? sourceLine)
    {
        AnswerRejection Reject(string reason) => new(questionId, sourceLine, reason);

        if (supplied is null || supplied.Count == 0)
        {
            return Reject(
                "the value is empty (`[]` or `null`), and an empty value is not a way to un-answer a "
                + "question. \"This question was not answered here\" is already spelled by OMITTING the id, so "
                + "an empty one can only ever delete a decision that was already recorded.");
        }

        foreach (var value in supplied)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Reject(
                    "one of the values is blank. A blank string is what a mis-written generator produces, "
                    + "never a decision -- it would flatten as \"Answered:\" with nothing after it.");
            }
        }

        // `multi` is the only mode that takes more than one value; every other mode takes exactly one.
        if (!IsMultiSelect(modeToken) && supplied.Count != 1)
        {
            return Reject(
                $"mode `{modeToken}` takes exactly one value; {supplied.Count} were supplied "
                + $"({Quote(supplied)}).");
        }

        if (IsSelectMode(modeToken))
        {
            foreach (var value in supplied)
            {
                if (!options.Contains(value, StringComparer.Ordinal))
                {
                    return Reject(
                        $"\"{value}\" is not one of this question's options ({Quote(options)}). An --answers "
                        + "file is a machine input, not a reviewer at the page: the page's \"Something else\" "
                        + "write-in records a HUMAN's departure from the framing, and it lands in the plan's "
                        + "own `answer`. Record it there.");
                }
            }
        }
        else if (string.Equals(modeToken, "bool", StringComparison.Ordinal)
            && !string.Equals(supplied[0], "true", StringComparison.Ordinal)
            && !string.Equals(supplied[0], "false", StringComparison.Ordinal))
        {
            return Reject(
                $"\"{supplied[0]}\" is not a `bool` answer. The rendered form submits `true` or `false`, and "
                + "the handoff reference documents the same pair.");
        }
        else if (string.Equals(modeToken, "number", StringComparison.Ordinal)
            && !double.TryParse(
                supplied[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return Reject($"\"{supplied[0]}\" is not a number, and this question's mode is `number`.");
        }

        // LAST: the question already carries a decision, and this entry is not that decision. Re-stating it
        // verbatim is accepted -- an answers file may only ADD information, never replace it.
        if (IsDecision(inlineAnswer) && !supplied.SequenceEqual(inlineAnswer, StringComparer.Ordinal))
        {
            return Reject(
                $"this question already carries a decision recorded INLINE in the plan ({Quote(inlineAnswer)}), "
                + $"and an --answers file may not replace one ({Quote(supplied)}). A recorded answer is the "
                + "living document's durable half: change it in the .charter.md (`charter resolve` / "
                + "`charter poll --apply` do it atomically), or drop this id.");
        }

        return null;
    }

    /// <summary>True for the one mode that accepts more than one value.</summary>
    private static bool IsMultiSelect(string modeToken)
        => string.Equals(modeToken, "multi", StringComparison.Ordinal);

    /// <summary>True for the two modes that declare an option set the answer must come from.</summary>
    private static bool IsSelectMode(string modeToken)
        => string.Equals(modeToken, "single", StringComparison.Ordinal) || IsMultiSelect(modeToken);

    /// <summary>Values as a readable, deterministic list for a stderr line.</summary>
    private static string Quote(IReadOnlyList<string> values)
        => string.Join(", ", values.Select(value => $"\"{value}\""));
}
