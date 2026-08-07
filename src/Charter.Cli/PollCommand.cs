using Charter.Server;

namespace Charter.Cli;

/// <summary>
/// Orchestrates <c>charter poll</c>: discover the running review session (registry, explicit descriptor, or
/// <c>--url</c>), prove it live via <see cref="ReviewClient"/>, drain queued annotations + answers + the
/// reviewer's round hand-off, and emit the single stdout envelope — and, under <c>--apply</c>, write the
/// drained answers INLINE into the plan's <c>:::question</c> blocks (the living-document write, via
/// <see cref="QuestionResolution.ApplyToFile"/>). The server-facing pieces live in Charter.Server; this only
/// sequences them and maps outcomes to exit codes.
/// </summary>
internal static class PollCommand
{
    // The single clean stderr line for the no-session case; stdout still carries a session:null envelope.
    private const string NoSessionMessage = "charter poll: no running review session.";

    // Per-liveness-probe budget; short because a live loopback server answers instantly.
    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(5);

    // Drain budgets: a generous bound for one --wait long-poll cycle (server long-polls ~30s), a short one
    // for the non-blocking immediate drain. Both are upper guards, not fixed sleeps — the server responds as
    // soon as it has (or decides it has no) data.
    private static readonly TimeSpan WaitDrainDeadline = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ImmediateDrainDeadline = TimeSpan.FromSeconds(15);

    /// <summary>Run the verb synchronously (the System.CommandLine action is sync). Returns the exit code.</summary>
    public static int Execute(string? input, string? sessionPath, string? url, bool wait, bool apply)
        => ExecuteAsync(input, sessionPath, url, wait, apply).GetAwaiter().GetResult();

    private static async Task<int> ExecuteAsync(
        string? input, string? sessionPath, string? url, bool wait, bool apply)
    {
        var resolution = await ResolveSessionAsync(input, sessionPath, url).ConfigureAwait(false);
        if (resolution.Client is null)
        {
            // A LIVE session always takes precedence; only once none was found does the committed review log
            // become the source. With a plan named, read it — this is the step that closes the loop, because
            // the agent reading a teammate's comments is EXECUTING, not running `charter review`.
            if (!resolution.Ambiguous && !string.IsNullOrEmpty(input))
            {
                var fromLog = DrainReviewLog(input);
                if (fromLog is not null)
                {
                    return fromLog.Value;
                }
            }

            // No live session, and no readable log (or none asked for). Emit the parseable session:null
            // envelope either way so an agent always gets JSON, and exit 3.
            if (!resolution.Ambiguous)
            {
                Console.Error.WriteLine(NoSessionMessage);
            }

            Console.WriteLine(PollEnvelope.Serialize(null, Array.Empty<Annotation>(), Array.Empty<Answer>()));
            return ReviewExitCodes.NoSession;
        }

        using var client = resolution.Client;
        using var drainCts = new CancellationTokenSource(wait ? WaitDrainDeadline : ImmediateDrainDeadline);

        // Annotations drain destructively (ephemeral notes the agent acts on by editing) — the drain is the
        // hand-off, and the reviewer's in-page panel manages them only BEFORE it (edit/retract a still-pending
        // note; once drained they are the agent's). Answers are PEEKED —
        // reported but NOT removed — so a plain poll can never strand an answer with no durable home; they are
        // removed server-side only after a successful --apply commit (§1.6). A drain that could not complete
        // reports an Error (never a silent empty), so a transient failure is not mistaken for "nothing queued".
        var annotations = await client.DrainAnnotationsAsync(wait, drainCts.Token).ConfigureAwait(false);
        var answers = await client.PeekAnswersAsync(drainCts.Token).ConfigureAwait(false);

        // The reviewer's explicit round HAND-OFF ("Send to agent" in the review page), read AFTER the drain so
        // a hand-off made while a --wait poll was outstanding is reported by the very cycle it woke. Like the
        // other drains it reports its own failure rather than a silent "no hand-off".
        var handoff = await client.PeekReviewSubmissionAsync(drainCts.Token).ConfigureAwait(false);
        var submission = handoff.Items.Count > 0 ? handoff.Items[0] : null;
        var drainError = annotations.Error ?? answers.Error ?? handoff.Error;

        // --apply is the living-document write: peek → apply → commit. resolution.Session (non-null whenever
        // Client is) carries the server-reported SourcePath, so this locates the plan even on the --url path.
        // Skipped when nothing was answered (no spurious rewrite). A refused apply PRESERVES the answers (never
        // committed) and exits with a distinct code so the reviewer's decision is recoverable, not lost.
        AnswerApplication.ApplyResult? applied = null;
        IReadOnlyList<Answer> staleAnswers = Array.Empty<Answer>();
        try
        {
            if (apply && answers.Items.Count > 0)
            {
                // Charter #75 item 3: refuse to fold in a decision whose :::question is no longer the one the
                // reviewer was asked. The agent gets the refusal, not an override — `charter resolve
                // --apply-stale-answers` is the human's verb for saying "apply it anyway", and an autonomous
                // loop must not be able to write a stale decision into the plan on its own say-so.
                staleAnswers = AnswerApplication.FindStale(resolution.Session!.SourcePath, answers.Items);
                if (staleAnswers.Count == 0)
                {
                    applied = await AnswerApplication
                        .ApplyAndCommitAsync(client, resolution.Session!, answers.Items, drainCts.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // stdout: always exactly one envelope (key omitted), on EVERY path — the finally is what keeps that
            // true even if the apply throws. VERBATIM server wire shapes, no reshaping. A non-null drainError
            // rides the envelope so the caller sees WHY a drain came back empty.
            //
            // Emitted AFTER the --apply write, and re-resolved against the file that write left behind: the
            // drain resolved each annotation's sourceLine against the plan as it was moments ago, and this same
            // invocation may have just shifted every line below an answered :::question. Handing the agent the
            // pre-apply line makes it edit the wrong block (Charter #49). The apply's own failure still never
            // suppresses the envelope — the reported answers are the primary contract, the write is the effect.
            var reported = applied is { Applied: true }
                ? await AnchorResolution
                    .ResolveAgainstFileAsync(annotations.Items, resolution.Session!.SourcePath)
                    .ConfigureAwait(false)
                : annotations.Items;

            Console.WriteLine(
                PollEnvelope.Serialize(resolution.Session, reported, answers.Items, drainError, submission));
        }

        // The hand-off has now been REPORTED, so clear it: it marks one round, not a standing state, and an
        // uncleared marker would tell the agent "the human is done" on every later poll. Cleared only on a
        // CLEAN drain — when the queue state is unknown the agent has not really been told — and a failed ack
        // is deliberately non-fatal: the marker stays and is reported again (at-least-once, the safe
        // direction, exactly as the answers commit behaves).
        if (submission is not null && drainError is null)
        {
            await client.AckReviewSubmissionAsync(submission.Sequence, drainCts.Token).ConfigureAwait(false);
        }

        if (staleAnswers.Count > 0)
        {
            Console.Error.WriteLine($"charter poll: refusing --apply: {AnswerApplication.StaleAnswerReason(staleAnswers)}");
            Console.Error.WriteLine(
                "charter poll: the queued answers remain in the review store. Ask the reviewer to re-answer them "
                + "in 'charter review', or have them run 'charter resolve <plan> --apply-stale-answers'.");
            return ReviewExitCodes.ApplyFailed;
        }

        if (applied is { Applied: false } refused)
        {
            Console.Error.WriteLine($"charter poll: apply failed: {refused.Error}");
            Console.Error.WriteLine(
                "charter poll: the queued answers remain in the review store; fix the plan and re-run "
                + "'charter poll --apply' (or 'charter resolve') to retry.");
            return ReviewExitCodes.ApplyFailed;
        }

        if (applied is { Applied: true, Committed: false })
        {
            Console.Error.WriteLine(
                "charter poll: applied the answers inline, but could not remove them from the review store; "
                + "they may be re-reported until the next successful drain (a re-apply is idempotent).");
        }

        // A failed drain outranks the clean-empty verdict: the queue state is unknown, so never report
        // "nothing queued". Distinct exit 4 so an agent does not hand off on a false negative.
        if (drainError is not null)
        {
            Console.Error.WriteLine(
                $"charter poll: {drainError}; the queue state is unknown — not reporting 'nothing queued'.");
            return ReviewExitCodes.DrainFailed;
        }

        // A reviewer's round hand-off counts as something arriving even when both queues are empty (they may
        // already have been drained): "the human says this round is done" is exactly what the agent is
        // waiting for, so it must not be reported as a clean-empty nothing-happened.
        return annotations.Items.Count + answers.Items.Count >= 1 || submission is not null
            ? ReviewExitCodes.Drained
            : ReviewExitCodes.CleanEmpty;
    }

    /// <summary>
    /// The server-less read: fold the committed <c>&lt;plan&gt;.review/*.jsonl</c> logs and emit the SAME
    /// envelope shape, or null when there is no log beside <paramref name="planPath"/> at all (which leaves
    /// the caller on the unchanged no-session path, exit 3 — "no session AND no readable log" is still
    /// "nothing to drain", and an agent that branches on 3 keeps behaving as it does today).
    /// </summary>
    /// <remarks>
    /// Exit codes are unchanged in meaning: comments reported ⇒ 0, a readable log with nothing new ⇒ 2, a log
    /// that could not be READ ⇒ 4 (the review state is unknown, never "nothing queued"). The envelope's
    /// <c>session</c> is null because there genuinely is none; the additive <c>source: "review-log"</c> field
    /// is what distinguishes this from the no-session case without changing any existing field.
    /// </remarks>
    private static int? DrainReviewLog(string planPath)
    {
        var consumedDirectory = StateDirectory.Consumed();

        ReviewLogDrainResult drain;
        try
        {
            drain = ReviewLogDrain.Drain(planPath, consumedDirectory);
        }
        catch (Exception ex)
        {
            // A ledger this machine cannot read must not swallow a teammate's comments; report it as a failed
            // drain (state unknown) rather than as an empty one.
            drain = new ReviewLogDrainResult(
                Array.Empty<Annotation>(), HasLog: true, DrainError: ex.Message, Delivered: Array.Empty<string>());
        }

        if (!drain.HasLog)
        {
            return null;
        }

        // No live session, so no round hand-off can exist: `reviewSubmission` is null by construction here,
        // and `source` is named because the envelope now carries BOTH additive fields.
        Console.WriteLine(PollEnvelope.Serialize(
            null,
            drain.Annotations,
            Array.Empty<Answer>(),
            drain.DrainError,
            source: PollEnvelope.ReviewLogSource));

        // Only NOW are these comments delivered. Recording consumption before the write would make the read
        // at-most-once: a broken pipe or a killed process in that window would lose a committed objection on
        // this machine permanently, and every later poll would report a clean empty. A repeat is recoverable;
        // a silent loss is not — the design's asymmetry, applied to delivery.
        try
        {
            ReviewLogDrain.ConfirmDelivered(planPath, consumedDirectory, drain.Delivered);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"charter poll: reported {drain.Annotations.Count} review comment(s) but could not record them "
                + $"as delivered ({ex.Message}); they will be reported again on the next poll.");
        }

        // #81 — say it. The defect this guards was that a replaced checkout silently inherited the previous
        // one's consumption ledger and never saw its teammates' committed comments. A reset that is itself
        // silent would only relocate the surprise: the reader would get a burst of history with no idea why.
        // Not an error and not an exit-code change — the drain succeeded, and more completely than it would
        // have.
        if (drain.LedgerReset is not null)
        {
            Console.Error.WriteLine($"charter poll: {drain.LedgerReset}");
        }

        if (drain.DrainError is not null)
        {
            Console.Error.WriteLine(
                $"charter poll: {drain.DrainError}; the review state is unknown — not reporting 'nothing queued'.");
            return ReviewExitCodes.DrainFailed;
        }

        return drain.Annotations.Count >= 1 ? ReviewExitCodes.Drained : ReviewExitCodes.CleanEmpty;
    }

    private static async Task<SessionResolution> ResolveSessionAsync(string? input, string? sessionPath, string? url)
    {
        // --url bypasses the registry entirely (escape hatch + test seam + zero-key-at-rest path).
        if (!string.IsNullOrEmpty(url))
        {
            var client = ReviewClient.FromCapabilityUrl(url); // FormatException => RunVerb => exit 1
            return await ProbeAsync(client, expectedSourcePath: null, descriptorPath: null).ConfigureAwait(false);
        }

        var sessionsDirectory = StateDirectory.Sessions();

        // --session <path>: read an explicit descriptor file.
        if (!string.IsNullOrEmpty(sessionPath))
        {
            return await ProbeDescriptorAsync(SessionRegistry.Read(sessionPath), sessionPath).ConfigureAwait(false);
        }

        // <plan>: select the descriptor by canonical path.
        if (!string.IsNullOrEmpty(input))
        {
            var descriptorPath = SessionRegistry.PathForPlan(sessionsDirectory, input);
            return await ProbeDescriptorAsync(SessionRegistry.Read(descriptorPath), descriptorPath).ConfigureAwait(false);
        }

        // No selector: auto-select the single LIVE session.
        return await AutoSelectAsync(sessionsDirectory).ConfigureAwait(false);
    }

    private static async Task<SessionResolution> ProbeDescriptorAsync(SessionDescriptor? descriptor, string descriptorPath)
    {
        if (descriptor is null || !Uri.TryCreate(descriptor.Address, UriKind.Absolute, out var address))
        {
            return SessionResolution.None;
        }

        var client = new ReviewClient(address, descriptor.Key);
        return await ProbeAsync(client, descriptor.SourcePath, descriptorPath).ConfigureAwait(false);
    }

    // Probe one candidate. On success, return the live resolution (the caller owns disposing the client). On
    // failure, dispose the client and — when it came from a descriptor — prune the stale descriptor file.
    private static async Task<SessionResolution> ProbeAsync(
        ReviewClient client, string? expectedSourcePath, string? descriptorPath)
    {
        using var cts = new CancellationTokenSource(ProbeDeadline);
        var session = await client.ProbeAsync(expectedSourcePath, cts.Token).ConfigureAwait(false);
        if (session is null)
        {
            client.Dispose();
            if (descriptorPath is not null)
            {
                SessionRegistry.Delete(descriptorPath); // stale hint — remove it
            }

            return SessionResolution.None;
        }

        return new SessionResolution(client, session, Ambiguous: false);
    }

    private static async Task<SessionResolution> AutoSelectAsync(string sessionsDirectory)
    {
        var live = new List<(ReviewClient Client, PollSession Session)>();
        foreach (var entry in SessionRegistry.Enumerate(sessionsDirectory))
        {
            if (!Uri.TryCreate(entry.Descriptor.Address, UriKind.Absolute, out var address))
            {
                SessionRegistry.Delete(entry.Path);
                continue;
            }

            var client = new ReviewClient(address, entry.Descriptor.Key);
            using var cts = new CancellationTokenSource(ProbeDeadline);
            var session = await client.ProbeAsync(entry.Descriptor.SourcePath, cts.Token).ConfigureAwait(false);
            if (session is null)
            {
                client.Dispose();
                SessionRegistry.Delete(entry.Path); // prune stale
                continue;
            }

            live.Add((client, session));
        }

        if (live.Count == 0)
        {
            return SessionResolution.None;
        }

        if (live.Count == 1)
        {
            return new SessionResolution(live[0].Client, live[0].Session, Ambiguous: false);
        }

        // >1 live: ambiguous. List the candidates to stderr and refuse to guess (exit no-session).
        Console.Error.WriteLine("charter poll: multiple live review sessions; pass <plan> or --url to choose one:");
        foreach (var candidate in live)
        {
            Console.Error.WriteLine($"  {candidate.Session.SourceFile}  {candidate.Session.Address}");
            candidate.Client.Dispose();
        }

        return new SessionResolution(null, null, Ambiguous: true);
    }

    // The outcome of session discovery: a live client+session, plain no-session, or an ambiguous refusal
    // (candidates already listed to stderr). Client is non-null only for the live case.
    private readonly record struct SessionResolution(ReviewClient? Client, PollSession? Session, bool Ambiguous)
    {
        public static SessionResolution None => new(null, null, false);
    }
}
