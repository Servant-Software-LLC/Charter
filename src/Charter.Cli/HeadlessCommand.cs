using Charter.Core;

namespace Charter.Cli;

/// <summary>
/// <c>charter headless &lt;plan.charter.md&gt; [--out-dir &lt;dir&gt;]</c> — the unattended sibling of the
/// interactive review loop (Charter #7). Renders the plan to the SAME self-contained, SDK-free artifact
/// <c>charter export</c> produces, writes the forensic record beside it, and exits. It starts no server, opens
/// no browser, and waits on nothing, so it is safe inside an autonomous crewmate session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a second <c>export</c>.</b> <c>export</c> already renders the artifact and exits
/// without serving — that requirement of #7 was met before this verb existed, and re-implementing it would be
/// pure redundancy. So this verb calls <see cref="ArtifactExporter.Export"/> unchanged (a test pins the two
/// artifacts byte-identical) and adds only the three things <c>export</c> genuinely does not have:
/// </para>
/// <list type="number">
///   <item><description>a <b>path convention</b> a collecting harness can compute from the plan path alone —
///     <c>export</c> requires you to name <c>-o</c>, which means telling the harness where to look;</description></item>
///   <item><description>the <b>forensic record</b> (<see cref="HeadlessRecord"/>): the anchor → markdown-line
///     source map and the decisions/diagnostics that otherwise live only in the review server's memory;</description></item>
///   <item><description>a <b>scriptable escalation exit code</b> — <c>export</c>'s contract is 0/1, and
///     widening it would change a shipped verb's meaning.</description></item>
/// </list>
/// <para>
/// <b>The path convention.</b> Both outputs are named from the plan's own file name, so nothing has to be
/// passed or configured for a harness to find them: <c>&lt;stem&gt;.html</c> and
/// <c>&lt;stem&gt;.headless.json</c>, where <c>&lt;stem&gt;</c> is the plan's file name without its final
/// extension (<c>storage.charter.md</c> → <c>storage.charter.html</c> +
/// <c>storage.charter.headless.json</c>). They land beside the plan by default — so they travel with the
/// worktree a crew collects — or in <c>--out-dir</c>, keeping the same names, for a <c>data/</c>-style
/// collection directory.
/// </para>
/// </remarks>
internal static class HeadlessCommand
{
    /// <summary>The suffix appended to the plan's stem for the forensic record.</summary>
    private const string RecordSuffix = ".headless.json";

    /// <summary>The suffix appended to the plan's stem for the exported artifact.</summary>
    private const string ArtifactSuffix = ".html";

    /// <summary>
    /// Render <paramref name="planPath"/> and persist both outputs, returning a
    /// <see cref="HeadlessExitCodes"/> value. Any I/O failure propagates to the caller's <c>RunVerb</c>
    /// wrapper, which turns it into one clean stderr line + exit 1.
    /// </summary>
    public static int Execute(string planPath, string? outDirectory)
    {
        if (!File.Exists(planPath))
        {
            Console.Error.WriteLine($"charter headless: input plan not found: {planPath}");
            return 1;
        }

        var fullPlanPath = Path.GetFullPath(planPath);
        var planDirectory = Path.GetDirectoryName(fullPlanPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPlanPath);

        var destination = string.IsNullOrEmpty(outDirectory) ? planDirectory : Path.GetFullPath(outDirectory);
        var artifactPath = Path.Combine(destination, stem + ArtifactSuffix);
        var recordPath = Path.Combine(destination, stem + RecordSuffix);

        // Charter never overwrites its own input. A plan already named `<stem>.html` would otherwise have the
        // artifact rendered straight over the source markdown, destroying it — the derived-name convention's
        // one sharp edge, refused rather than papered over.
        if (SamePath(artifactPath, fullPlanPath) || SamePath(recordPath, fullPlanPath))
        {
            Console.Error.WriteLine(
                $"charter headless: the derived output path would overwrite the plan itself ({planPath}). "
                    + "Rename the plan (the convention is <name>.charter.md) or pass --out-dir.");
            return 1;
        }

        var markdown = File.ReadAllText(fullPlanPath);

        // The plan's own directory is the confinement root the exporter resolves local assets under; reads
        // never escape it. Identical to what `charter export` passes, so the artifacts match byte for byte.
        var html = ArtifactExporter.Export(markdown, planDirectory);

        var record = HeadlessRecord.Build(
            markdown,
            Path.GetFileName(fullPlanPath),
            Path.GetFileName(artifactPath),
            CharterVersion.Current);

        Directory.CreateDirectory(destination);

        // Both files land BEFORE the exit code is decided: the forensic guarantee is that nothing the run
        // observed is lost, whatever the outcome. An escalation is reported after the evidence is on disk,
        // never instead of it.
        File.WriteAllText(artifactPath, html);
        File.WriteAllText(recordPath, record.ToJson());

        Console.WriteLine($"Exported {planPath} -> {artifactPath}");
        Console.WriteLine($"Recorded {planPath} -> {recordPath}");

        if (!record.NeedsHuman)
        {
            return HeadlessExitCodes.Complete;
        }

        ReportOutstanding(record, Path.GetFileName(recordPath));
        return HeadlessExitCodes.NeedsHuman;
    }

    /// <summary>
    /// Name what a human still has to deal with, on STDERR (stdout keeps its two plain written-file lines).
    /// ASCII only, so the message is byte-stable across the Win/macOS/Linux console encodings CI runs on —
    /// the same rule <c>charter convert</c>'s promotion report follows.
    /// </summary>
    private static void ReportOutstanding(HeadlessRecord record, string recordFileName)
    {
        var openForHuman = record.Questions
            .Where(question => question is { Answered: false, Target: "human" })
            .ToList();

        var blockingNotes = record.Notes
            .Where(note => note.Kind is HeadlessNoteKind.MalformedQuestion or HeadlessNoteKind.DuplicateQuestionId)
            .ToList();

        Console.Error.WriteLine(
            $"charter headless: {openForHuman.Count + blockingNotes.Count} item(s) need a human -- exit "
                + $"{HeadlessExitCodes.NeedsHuman}. Everything is on disk; see {recordFileName}.");

        // Whether each escalation carries a lean, on the escalation line itself (Charter #142). This is the
        // difference between a decision a human clears in ten seconds and one they must reconstruct from
        // scratch, and it was previously discoverable only by opening the plan and reading the block.
        foreach (var question in openForHuman)
        {
            var lean = question.Recommended is { Length: > 0 } value
                ? $" [agent recommends: {value}]"
                : " [no recommendation]";
            Console.Error.WriteLine(
                $"  unanswered question '{question.Id}' (line {question.SourceLine}): {question.Title}{lean}");
        }

        foreach (var note in blockingNotes)
        {
            var where = note.SourceLine is { } line ? $" (line {line})" : string.Empty;
            Console.Error.WriteLine($"  {HeadlessNote.Token(note.Kind)}{where}: {note.Message}");
        }
    }

    /// <summary>
    /// True when two already-absolute paths name the same file, using the platform's own case rule — Windows
    /// and macOS compare case-insensitively, Linux does not.
    /// </summary>
    private static bool SamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
