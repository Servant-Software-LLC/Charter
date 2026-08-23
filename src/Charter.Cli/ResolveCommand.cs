using Charter.Server;

namespace Charter.Cli;

/// <summary>
/// Orchestrates <c>charter resolve &lt;plan.charter.md&gt;</c>: the §1.6 solo-review companion to
/// <c>poll --apply</c>. A human may review a plan with no agent looping <c>poll --apply</c>; this discrete,
/// single-writer-safe verb applies their queued answers inline. It prefers a LIVE review server (peek → apply
/// → commit over HTTP, exactly as <c>poll --apply</c>), and falls back to the server-owned durable sidecar
/// when no server is running — the case a solo human hits after answering and closing <c>charter review</c>.
/// The inline write goes through <see cref="Charter.Core.QuestionResolution.ApplyToFile"/>, so it inherits the
/// duplicate-id refusal and the concurrent-edit precondition, and is atomic in the plan's own directory.
/// </summary>
internal static class ResolveCommand
{
    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainDeadline = TimeSpan.FromSeconds(15);

    /// <summary>Run the verb synchronously. <paramref name="planPath"/> is an existing plan (Program checks).</summary>
    /// <param name="planPath">The plan whose queued answers to apply.</param>
    /// <param name="applyStaleAnswers">
    /// The reviewer's explicit <b>"yes, apply them anyway"</b> for answers whose <c>:::question</c> has changed
    /// shape since they were given (Charter #75 item 3). Deliberately only on this verb — <c>charter resolve</c>
    /// is the human's verb, and an agent looping <c>poll --apply</c> must not be able to force a stale decision
    /// into the plan on its own say-so.
    /// </param>
    public static int Execute(string planPath, bool applyStaleAnswers = false)
        => ExecuteAsync(planPath, applyStaleAnswers).GetAwaiter().GetResult();

    private static async Task<int> ExecuteAsync(string planPath, bool applyStaleAnswers)
    {
        var canonical = Path.GetFullPath(planPath);

        // Prefer a live server: its store (mirrored to the sidecar) is the freshest source, and going through
        // it keeps the store and sidecar in step via the commit. Fall back to the sidecar only when no server
        // is running — the genuine solo case.
        var live = await TryResolveLiveAsync(canonical).ConfigureAwait(false);
        if (live is not null)
        {
            using (live.Value.Client)
            {
                return await ResolveViaServerAsync(
                    live.Value.Client, live.Value.Session, planPath, applyStaleAnswers).ConfigureAwait(false);
            }
        }

        return ResolveViaSidecar(canonical, planPath, applyStaleAnswers);
    }

    private static async Task<int> ResolveViaServerAsync(
        ReviewClient client, PollSession session, string planPath, bool applyStaleAnswers)
    {
        using var drainCts = new CancellationTokenSource(DrainDeadline);
        var answers = await client.PeekAnswersAsync(drainCts.Token).ConfigureAwait(false);
        if (answers.Failed)
        {
            Console.Error.WriteLine(
                $"charter resolve: {answers.Error}; the queue state is unknown — not resolving.");
            return ReviewExitCodes.DrainFailed;
        }

        if (answers.Items.Count == 0)
        {
            Console.Error.WriteLine($"charter resolve: no queued answers for {planPath}.");
            return ReviewExitCodes.CleanEmpty;
        }

        if (RefuseUnmatched(session.SourcePath, answers.Items)
            || RefuseStale(session.SourcePath, answers.Items, applyStaleAnswers, planPath))
        {
            return ReviewExitCodes.ApplyFailed;
        }

        var result = await AnswerApplication
            .ApplyAndCommitAsync(client, session, answers.Items, drainCts.Token)
            .ConfigureAwait(false);
        return ReportApply(result, planPath, answers.Items.Count, "the review store");
    }

    private static int ResolveViaSidecar(string canonical, string planPath, bool applyStaleAnswers)
    {
        var sidecarPath = ReviewSidecar.PathForPlan(StateDirectory.Sidecars(), canonical);
        var state = ReviewSidecar.Rehydrate(sidecarPath);
        if (state.Answers.Count == 0)
        {
            Console.Error.WriteLine(
                $"charter resolve: no queued answers for {planPath} (no live review session, and the sidecar is empty or absent).");
            return ReviewExitCodes.CleanEmpty;
        }

        if (RefuseUnmatched(canonical, state.Answers)
            || RefuseStale(canonical, state.Answers, applyStaleAnswers, planPath))
        {
            return ReviewExitCodes.ApplyFailed;
        }

        var result = AnswerApplication.ApplyToPlan(canonical, state.Answers);
        if (!result.Applied)
        {
            return ReportApplyFailure(result, "the sidecar");
        }

        // Applied: clear only the answers from the sidecar (preserve any queued annotations), so a re-run does
        // not re-apply the same decisions. Both empty ⇒ WriteState deletes the sidecar (no husk).
        ReviewSidecar.WriteState(sidecarPath, canonical, state.Annotations, Array.Empty<Answer>());
        Console.WriteLine($"Resolved {state.Answers.Count} answer(s) into {planPath}");
        return ReviewExitCodes.Drained;
    }

    /// <summary>
    /// Whether to refuse this batch because some answer names a <c>:::question</c> the plan does not carry as a
    /// block (Charter #203), reporting WHY and how to proceed.
    /// </summary>
    /// <remarks>
    /// Checked BEFORE staleness and NOT bypassable by <c>--apply-stale-answers</c>. The inline write would match
    /// nothing and return the plan byte-identical, which this verb would score as a successful apply and follow
    /// by clearing the sidecar (or committing the store) — draining the reviewer's decision into nothing while
    /// printing "Resolved 1 answer(s)". The flag is the human's "apply it anyway" for a question that CHANGED;
    /// there is nothing to apply anyway to when the question is not a block at all.
    /// </remarks>
    private static bool RefuseUnmatched(string sourcePath, IReadOnlyList<Answer> answers)
    {
        var unmatched = AnswerApplication.FindUnmatched(sourcePath, answers);
        if (unmatched.Count == 0)
        {
            return false;
        }

        Console.Error.WriteLine($"charter resolve: {AnswerApplication.UnmatchedAnswerReason(unmatched)}");
        Console.Error.WriteLine(
            "charter resolve: the queued answers are preserved. Move the answered :::question to the top level "
            + "(or restore it), then re-run 'charter resolve'.");
        return true;
    }

    /// <summary>
    /// Whether to refuse this batch because some answer's <c>:::question</c> is no longer the one the reviewer
    /// was asked (Charter #75 item 3), reporting WHY and how to proceed. Refusal is all-or-nothing on purpose:
    /// the live store commits a peeked PREFIX (<c>AnswerStore.CommitFront</c>), so applying "the good ones" and
    /// skipping one in the middle would commit the skipped one anyway. Refusing the batch leaves every answer
    /// queued and re-reported until the reviewer settles it — the "nothing lost" guarantee, unchanged.
    /// </summary>
    private static bool RefuseStale(
        string sourcePath, IReadOnlyList<Answer> answers, bool applyStaleAnswers, string planPath)
    {
        if (applyStaleAnswers)
        {
            return false;
        }

        var stale = AnswerApplication.FindStale(sourcePath, answers);
        if (stale.Count == 0)
        {
            return false;
        }

        Console.Error.WriteLine($"charter resolve: {AnswerApplication.StaleAnswerReason(stale)}");
        Console.Error.WriteLine(
            $"charter resolve: re-answer them in 'charter review {planPath}', or re-run with "
            + "--apply-stale-answers to apply them as they stand.");
        return true;
    }

    private static int ReportApply(AnswerApplication.ApplyResult result, string planPath, int count, string store)
    {
        if (!result.Applied)
        {
            return ReportApplyFailure(result, store);
        }

        if (!result.Committed)
        {
            Console.Error.WriteLine(
                $"charter resolve: applied the answers inline, but could not remove them from {store}; "
                + "a re-run re-applies them idempotently.");
        }

        Console.WriteLine($"Resolved {count} answer(s) into {planPath}");
        return ReviewExitCodes.Drained;
    }

    private static int ReportApplyFailure(AnswerApplication.ApplyResult result, string store)
    {
        Console.Error.WriteLine($"charter resolve: apply failed: {result.Error}");
        Console.Error.WriteLine(
            $"charter resolve: the queued answers remain in {store}; fix the plan and re-run 'charter resolve' to retry.");
        return ReviewExitCodes.ApplyFailed;
    }

    // Find a LIVE review server for the plan via the per-user registry, or null. Focused on the plan-specific
    // descriptor (resolve always names a plan), so unlike poll it does not auto-select or list ambiguities. A
    // stale descriptor is left in place — pruning is poll's job; resolve just falls through to the sidecar.
    private static async Task<(ReviewClient Client, PollSession Session)?> TryResolveLiveAsync(string canonical)
    {
        var descriptorPath = SessionRegistry.PathForPlan(StateDirectory.Sessions(), canonical);
        var descriptor = SessionRegistry.Read(descriptorPath);
        if (descriptor is null || !Uri.TryCreate(descriptor.Address, UriKind.Absolute, out var address))
        {
            return null;
        }

        var client = new ReviewClient(address, descriptor.Key);
        using var cts = new CancellationTokenSource(ProbeDeadline);
        var session = await client.ProbeAsync(descriptor.SourcePath, cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            client.Dispose();
            return null;
        }

        return (client, session);
    }
}
