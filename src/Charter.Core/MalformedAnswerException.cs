namespace Charter.Core;

/// <summary>
/// Thrown by <see cref="QuestionResolution.ApplyToFile"/> when a queued answer carries a character
/// <see cref="AnswerRules"/> forbids — a control character other than <c>U+000A</c>, or one of the two
/// Unicode line/paragraph separators (Charter #202).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this refuses rather than sanitising.</b> Writing a cleaned-up value would make the plan and the
/// reviewer's decision disagree about the answer's text, with the difference recorded nowhere — the exact
/// divergence Charter #187's provenance stamps exist to make visible. Writing the value as submitted would
/// put a bare CR into the <c>.charter.md</c>, and from there into every flatten of it, permanently.
/// </para>
/// <para>
/// <b>Why it is loud rather than a skip.</b> <see cref="QuestionResolution.Apply"/> leaves such a question
/// untouched, which is the right in-library guarantee and the wrong thing to do QUIETLY: a byte-identical
/// write scores as a successful apply, so <c>poll --apply</c> and <c>charter resolve</c> would commit the
/// answer out of the store and the sidecar while reporting success — Charter #203's destruction, one channel
/// over. Refusing before the write is what keeps the decision recoverable, and the callers map it to
/// <c>ReviewExitCodes.ApplyFailed</c>: <i>the inline apply did not happen; the answers are preserved.</i>
/// </para>
/// </remarks>
public sealed class MalformedAnswerException : InvalidOperationException
{
    /// <summary>Create the exception for the question <paramref name="questionId"/> and its
    /// <paramref name="reason"/> (an <see cref="AnswerRejection.Reason"/>-shaped sentence).</summary>
    public MalformedAnswerException(string questionId, string reason)
        : base(BuildMessage(questionId, reason))
    {
        QuestionId = questionId;
        Reason = reason;
    }

    /// <summary>The <c>:::question</c> id whose queued answer was refused.</summary>
    public string QuestionId { get; }

    /// <summary>Why it was refused. Human-readable and ASCII; not a contract, and never parsed.</summary>
    public string Reason { get; }

    private static string BuildMessage(string questionId, string reason)
        => $"refusing to apply answers: the answer to '{questionId}' is not writable -- {reason}";
}
