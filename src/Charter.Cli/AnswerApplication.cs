using Charter.Core;
using Charter.Server;

namespace Charter.Cli;

/// <summary>
/// The shared inline-apply kernel for <c>charter poll --apply</c> and <c>charter resolve</c>: map drained
/// answers to the id → value(s) shape, write them into the plan's <c>:::question</c> blocks via
/// <see cref="QuestionResolution.ApplyToFile"/> (which refuses a duplicate-id plan and a concurrent external
/// edit), and — when applying against a live server — COMMIT the applied prefix only AFTER the write
/// succeeds. The ordering is the whole point (§1.6): peek → apply → commit, so a failed apply never removes an
/// answer from the store and the reviewer's decision stays recoverable.
/// </summary>
internal static class AnswerApplication
{
    /// <summary>
    /// The outcome of an apply: <see cref="Applied"/> is false when the inline write was refused (the answers
    /// are preserved), with <see cref="Error"/> naming why. <see cref="Committed"/> is meaningful only for the
    /// live-server path — true once the applied answers were removed from the store; false when the commit
    /// call failed (harmless — a re-run re-applies idempotently).
    /// </summary>
    public readonly record struct ApplyResult(bool Applied, bool Committed, string? Error);

    /// <summary>
    /// Map drained answers to the <c>id → value(s)</c> dictionary <see cref="QuestionResolution.ApplyToFile"/>
    /// consumes. Answers are submit-ordered (oldest first), so a repeated question id is LAST-WINS — the
    /// reviewer's most recent submission for that question.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<string>> ById(IReadOnlyList<Answer> answers)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var answer in answers)
        {
            if (!string.IsNullOrEmpty(answer.QuestionId))
            {
                map[answer.QuestionId] = answer.Values ?? (IReadOnlyList<string>)Array.Empty<string>();
            }
        }

        return map;
    }

    /// <summary>
    /// Write <paramref name="answers"/> inline into the plan at <paramref name="planPath"/>. A refusal
    /// (<see cref="DuplicateQuestionIdException"/>, a concurrent-edit <see cref="IOException"/>, or an access
    /// error) is caught and returned as a NON-applied result — the caller preserves the answers and signals
    /// apply-failure rather than losing them. Any other exception propagates (an unexpected fault).
    /// </summary>
    public static ApplyResult ApplyToPlan(string planPath, IReadOnlyList<Answer> answers)
    {
        try
        {
            QuestionResolution.ApplyToFile(planPath, ById(answers));
            return new ApplyResult(Applied: true, Committed: false, Error: null);
        }
        catch (DuplicateQuestionIdException ex)
        {
            return new ApplyResult(Applied: false, Committed: false, Error: ex.Message);
        }
        catch (IOException ex)
        {
            return new ApplyResult(Applied: false, Committed: false, Error: ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ApplyResult(Applied: false, Committed: false, Error: ex.Message);
        }
    }

    /// <summary>
    /// Apply <paramref name="answers"/> inline against a LIVE server, then — only on a successful write —
    /// commit the applied prefix back through <paramref name="client"/> so the server removes them from its
    /// store/sidecar. A failed apply returns without committing (answers preserved); a failed commit is
    /// non-fatal (the write already landed, and a re-run re-applies idempotently).
    /// </summary>
    public static async Task<ApplyResult> ApplyAndCommitAsync(
        ReviewClient client, PollSession session, IReadOnlyList<Answer> answers, CancellationToken cancellationToken)
    {
        var result = ApplyToPlan(session.SourcePath, answers);
        if (!result.Applied)
        {
            return result;
        }

        var committed = await client.CommitAnswersAsync(answers.Count, cancellationToken).ConfigureAwait(false);
        return result with { Committed = committed };
    }
}
