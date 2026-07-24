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
    public static int Execute(string planPath) => ExecuteAsync(planPath).GetAwaiter().GetResult();

    private static async Task<int> ExecuteAsync(string planPath)
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
                return await ResolveViaServerAsync(live.Value.Client, live.Value.Session, planPath).ConfigureAwait(false);
            }
        }

        return ResolveViaSidecar(canonical, planPath);
    }

    private static async Task<int> ResolveViaServerAsync(ReviewClient client, PollSession session, string planPath)
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

        var result = await AnswerApplication
            .ApplyAndCommitAsync(client, session, answers.Items, drainCts.Token)
            .ConfigureAwait(false);
        return ReportApply(result, planPath, answers.Items.Count, "the review store");
    }

    private static int ResolveViaSidecar(string canonical, string planPath)
    {
        var sidecarPath = ReviewSidecar.PathForPlan(StateDirectory.Sidecars(), canonical);
        var state = ReviewSidecar.Rehydrate(sidecarPath);
        if (state.Answers.Count == 0)
        {
            Console.Error.WriteLine(
                $"charter resolve: no queued answers for {planPath} (no live review session, and the sidecar is empty or absent).");
            return ReviewExitCodes.CleanEmpty;
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
