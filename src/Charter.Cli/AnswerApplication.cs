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
    /// The queued answers whose <c>:::question</c> is NO LONGER THE ONE THE REVIEWER WAS ASKED — the answer
    /// carries a fingerprint of the question's declared shape at submit time, a question with that id still
    /// exists in the plan at <paramref name="planPath"/>, and its shape has changed since (Charter #75 item 3).
    /// Empty means "apply normally", which is what an ordinary edited plan always returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule, exactly:</b> an answer is stale iff it carries a <see cref="Answer.QuestionFingerprint"/>,
    /// <see cref="QuestionIdentity.FingerprintOf"/> finds a question with the same id in the plan as it is now,
    /// and the two fingerprints differ. Three deliberate exemptions, each meaning "no evidence, proceed": an
    /// answer with no fingerprint (a queue from before this existed), a plan the answer's question is ABSENT
    /// from, and a plan that cannot be read (handled by the caller, which lets the ordinary apply path report
    /// the I/O failure).
    /// </para>
    /// <para>
    /// <b>The absent-question exemption used to be justified as "the apply is a no-op there, so it cannot corrupt
    /// anything". That was false</b> (Charter #203): the apply really is a no-op in the PLAN, but both callers
    /// then treat the run as successful and COMMIT the answer away — out of the store, out of the sidecar — so
    /// the reviewer's decision is destroyed while <c>$?</c> reads 0. The exemption stands here because staleness
    /// genuinely has no evidence to weigh; the case is now caught one step earlier, by
    /// <see cref="FindUnmatched"/>, which refuses instead.
    /// </para>
    /// <para>
    /// <b>False positive:</b> the drafting agent legitimately edits the question — retitles it, adds or removes
    /// an option, flips its target — after the reviewer answered. The decision is withheld and reported rather
    /// than applied. That is close to a true positive: the reviewer chose among options that no longer read the
    /// same, and nothing is lost (the answer stays queued and re-reported until applied, re-answered, or
    /// explicitly forced).
    /// <b>False negative:</b> the replacement plan's question is byte-identical in its DECLARED shape — same id,
    /// title, mode, target, options. The old decision folds in, but it answers a question that is, as asked,
    /// literally the same question with the same choices. That is the weakest claim that still catches the
    /// reported bug, exactly as <c>ReviewSidecar.IsStale</c>'s "one surviving anchor" is for annotations.
    /// </para>
    /// <para>
    /// A legitimately-edited plan keeps applying its answers normally — prose changes, block insertions, and
    /// other questions' answers being folded in all leave a question's declared shape untouched. That is the
    /// living-document model, and it is what this rule is careful not to break.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Answer> FindStale(string planPath, IReadOnlyList<Answer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        string markdown;
        try
        {
            markdown = File.ReadAllText(planPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No evidence: let the ordinary apply run and report the real I/O problem in its own words.
            return Array.Empty<Answer>();
        }

        var stale = new List<Answer>();
        foreach (var answer in answers)
        {
            if (answer.QuestionFingerprint is null)
            {
                continue;
            }

            var current = QuestionIdentity.FingerprintOf(markdown, answer.QuestionId);
            if (current is not null &&
                !string.Equals(current, answer.QuestionFingerprint, StringComparison.Ordinal))
            {
                stale.Add(answer);
            }
        }

        return stale;
    }

    /// <summary>
    /// The queued answers whose <c>:::question</c> <b>is not in the plan's block model at all</b>, so
    /// <see cref="QuestionResolution.Apply"/> would match nothing and return the markdown unchanged
    /// (Charter #203). Empty means "every answer has somewhere to land", which is what an ordinary plan returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a refusal and not a shrug.</b> The apply's own behaviour is benign — an id it cannot find is
    /// left untouched, by design. What is not benign is what the CALLERS do next: a byte-identical write counts as
    /// a successful apply, so <c>poll --apply</c> commits the batch (<c>CommitAnswersAsync</c> removes it from the
    /// store AND the sidecar) and <c>charter resolve</c> clears the sidecar, both reporting success. The reviewer's
    /// decision is therefore drained, reported applied, and destroyed. Exit 5 already means exactly the right
    /// thing here — <i>the inline apply did not happen; the answers are preserved, never committed</i> — so this
    /// is the treatment Charter #172 gave an <c>--answers</c> FILE's unmatched ids
    /// (<c>HandoffGate.UnmatchedAnswerIds</c>) finally reaching the interactive verbs.
    /// </para>
    /// <para>
    /// <b>It is broader than the nesting bug that found it.</b> Any id the model cannot see qualifies: a
    /// <c>:::question</c> nested inside a <c>::::note</c> (rendered as a live form, invisible to
    /// <see cref="BlockDocument"/>), a question the drafting agent deleted between submit and drain, a question
    /// whose body stopped parsing, or an id a client simply invented.
    /// </para>
    /// <para>
    /// <b>There is no override, deliberately.</b> <c>--apply-stale-answers</c> says "the question changed shape,
    /// apply it anyway" — a decision a human can weigh, because the write still lands. Here the write cannot
    /// land at all, so forcing it would only re-open the destruction. The remedies are real ones: un-nest the
    /// block, restore the question, or re-answer against the plan as it now is.
    /// </para>
    /// <para>
    /// A plan that cannot be READ yields nothing, exactly as <see cref="FindStale"/> does — no evidence, and the
    /// ordinary apply path reports the real I/O failure in its own words.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Answer> FindUnmatched(string planPath, IReadOnlyList<Answer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        string markdown;
        try
        {
            markdown = File.ReadAllText(planPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<Answer>();
        }

        var known = new HashSet<string>(QuestionResolution.QuestionIds(markdown), StringComparer.Ordinal);
        return answers.Where(answer => !known.Contains(answer.QuestionId ?? string.Empty)).ToList();
    }

    /// <summary>
    /// The one sentence both <c>poll --apply</c> and <c>resolve</c> say when they refuse a batch carrying an
    /// answer with nowhere to land. <paramref name="unmatched"/> must be non-empty.
    /// </summary>
    public static string UnmatchedAnswerReason(IReadOnlyList<Answer> unmatched)
    {
        ArgumentNullException.ThrowIfNull(unmatched);

        var ids = string.Join(", ", unmatched.Select(answer => answer.QuestionId).Distinct(StringComparer.Ordinal));
        return $"{unmatched.Count} queued answer(s) name a :::question this plan does not carry as a block "
            + $"({ids}): the inline write would match nothing, so reporting them applied would destroy them. "
            + "Nothing was applied and nothing was discarded. A :::question nested inside another container "
            + "renders as an answerable form but is not a block — move it to the top level, or restore the "
            + "question the answer was given to.";
    }

    /// <summary>
    /// The one sentence both <c>poll --apply</c> and <c>resolve</c> say when they refuse a stale batch, so the
    /// two verbs cannot describe the same refusal differently. <paramref name="stale"/> must be non-empty.
    /// </summary>
    public static string StaleAnswerReason(IReadOnlyList<Answer> stale)
    {
        ArgumentNullException.ThrowIfNull(stale);

        var ids = string.Join(", ", stale.Select(answer => answer.QuestionId).Distinct(StringComparer.Ordinal));
        return $"{stale.Count} queued answer(s) were written against a DIFFERENT version of their :::question "
            + $"({ids}): its title/mode/target/options have changed since, so folding those decisions in would "
            + "answer a question nobody was asked. Nothing was applied and nothing was discarded.";
    }

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
    /// (<see cref="MalformedAnswerException"/>, <see cref="DuplicateQuestionIdException"/>, a concurrent-edit
    /// <see cref="IOException"/>, or an access error) is caught and returned as a NON-applied result — the
    /// caller preserves the answers and signals apply-failure rather than losing them. Any other exception
    /// propagates (an unexpected fault).
    /// </summary>
    public static ApplyResult ApplyToPlan(string planPath, IReadOnlyList<Answer> answers)
    {
        try
        {
            QuestionResolution.ApplyToFile(planPath, ById(answers));
            return new ApplyResult(Applied: true, Committed: false, Error: null);
        }
        catch (MalformedAnswerException ex)
        {
            // Charter #202. The answer cannot be written as text, so it is preserved rather than cleaned:
            // sanitising would make the plan and the reviewer's decision disagree with nothing recording it.
            return new ApplyResult(Applied: false, Committed: false, Error: ex.Message);
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
