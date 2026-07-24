using Charter.Core;
using Charter.Server;

namespace Charter.Cli;

/// <summary>
/// Orchestrates <c>charter poll</c>: discover the running review session (registry, explicit descriptor, or
/// <c>--url</c>), prove it live via <see cref="ReviewClient"/>, drain queued annotations + answers, and emit
/// the single stdout envelope — and, under <c>--apply</c>, write the drained answers INLINE into the plan's
/// <c>:::question</c> blocks (the living-document write, via <see cref="QuestionResolution.ApplyToFile"/>). The
/// server-facing pieces live in Charter.Server; this only sequences them and maps outcomes to exit codes.
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
            // No live session (or ambiguous — ResolveSessionAsync already listed the candidates to stderr).
            // Emit the parseable session:null envelope either way so an agent always gets JSON, and exit 3.
            if (!resolution.Ambiguous)
            {
                Console.Error.WriteLine(NoSessionMessage);
            }

            Console.WriteLine(PollEnvelope.Serialize(null, Array.Empty<Annotation>(), Array.Empty<Answer>()));
            return 3;
        }

        using var client = resolution.Client;
        using var drainCts = new CancellationTokenSource(wait ? WaitDrainDeadline : ImmediateDrainDeadline);

        var annotations = await client.DrainAnnotationsAsync(wait, drainCts.Token).ConfigureAwait(false);
        var answers = await client.DrainAnswersAsync(drainCts.Token).ConfigureAwait(false);

        // stdout: always exactly one envelope (key omitted). VERBATIM server wire shapes, no reshaping. Emitted
        // BEFORE the --apply write so the agent always receives the drained answers even if the inline write
        // fails (RunVerb maps that to exit 1) — the envelope is the primary contract, the write is the effect.
        Console.WriteLine(PollEnvelope.Serialize(resolution.Session, annotations, answers));

        // --apply is the living-document write: splice the drained answers INLINE into the plan's :::question
        // blocks. resolution.Session (non-null whenever Client is) carries the server-reported SourcePath, so
        // this locates the plan even on the --url path. Skipped when nothing was answered (no spurious rewrite).
        if (apply && answers.Count > 0)
        {
            ApplyAnswersToPlan(resolution.Session!, answers);
        }

        // 0 => drained >= 1 item; 2 => live session but nothing queued.
        return annotations.Count + answers.Count >= 1 ? 0 : 2;
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

    // Write the drained answers INLINE into the session's plan file, resolving each :::question. Maps the
    // drained Answers to the id -> values shape QuestionResolution.Apply consumes; answers are submit-ordered,
    // so a repeated question id is LAST-WINS (the reviewer's most recent submission). The write is atomic
    // (temp + rename in the plan's own directory), so the review server's per-request re-read never sees a
    // half-written file — the single discrete writer the living-document model requires (§1.4).
    private static void ApplyAnswersToPlan(PollSession session, IReadOnlyList<Answer> answers)
    {
        var answersById = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var answer in answers)
        {
            if (!string.IsNullOrEmpty(answer.QuestionId))
            {
                answersById[answer.QuestionId] = answer.Values ?? (IReadOnlyList<string>)Array.Empty<string>();
            }
        }

        QuestionResolution.ApplyToFile(session.SourcePath, answersById);
    }

    // The outcome of session discovery: a live client+session, plain no-session, or an ambiguous refusal
    // (candidates already listed to stderr). Client is non-null only for the live case.
    private readonly record struct SessionResolution(ReviewClient? Client, PollSession? Session, bool Ambiguous)
    {
        public static SessionResolution None => new(null, null, false);
    }
}
