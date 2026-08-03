using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Charter.Cli;
using Charter.Core;
using Charter.Server;
using Spectre.Console;

// Scaffold entry point. The real CLI — `charter <plan.charter.md>` opening a local review server that
// renders the block plan in the browser for in-place annotation, plus `export` and a `poll` feedback
// loop — lands in later milestones (see README.md). Today the surface is a banner + --version, the
// `render` verb (a Charter plan (.charter.md) -> one portable HTML artifact via the Charter.Core renderer),
// and the `review` verb, which renders the plan and serves it read-only over the loopback review
// server for in-browser preview.

if (args.Length >= 1 && args[0] is "--version" or "-v")
{
    // Route through CharterVersion (the single source of truth), NOT Assembly.GetName().Version — the
    // latter is the numeric AssemblyVersion, which cannot carry a prerelease suffix, so a `-preview.1`
    // build would misreport as its bare `0.2.0`. CharterVersion reads the InformationalVersion, the same
    // value `charter skills install` stamps, keeping the two in agreement.
    Console.WriteLine($"charter {CharterVersion.Current}");

    // Then surface any installed skill whose stamped version has drifted from this binary (Charter #32) — a
    // stderr-only, non-fatal warning that leaves the stdout `charter <ver>` line clean and the exit code 0.
    WarnOnStaleSkills(CharterVersion.Current);
    return 0;
}

// `charter render <plan.charter.md> -o <out.html>`: read the markdown, render it through Charter.Core, and
// write the HTML artifact. Parsed with System.CommandLine; only entered for the `render` verb so the
// banner / --version behavior above stays exactly as-is.
if (args.Length >= 1 && args[0] == "render")
{
    return BuildRenderRoot().Parse(args).Invoke();
}

// `charter review <plan.charter.md> [--no-open]`: render the plan and serve it read-only over the loopback
// review server for in-browser preview. Parsed with System.CommandLine, parallel to `render`; only
// entered for the `review` verb so the banner / --version behavior above stays exactly as-is.
if (args.Length >= 1 && args[0] == "review")
{
    return BuildReviewRoot().Parse(args).Invoke();
}

// `charter export <plan.charter.md> -o <out.html>`: render the plan and, via Charter.Core.ArtifactExporter,
// inline every local asset as a data: URI and scrub any remaining local path into one TRULY offline,
// SDK-free HTML artifact, then write it. Parsed with System.CommandLine, parallel to `render`; only
// entered for the `export` verb so the banner / --version behavior above stays exactly as-is.
if (args.Length >= 1 && args[0] == "export")
{
    return BuildExportRoot().Parse(args).Invoke();
}

// `charter headless <plan.charter.md> [--out-dir <dir>]`: the UNATTENDED path (Charter #7). Render the plan
// to the same self-contained, SDK-free artifact `export` produces AND write the forensic record beside it —
// the anchor->markdown-line source map plus the decisions and diagnostics that otherwise live only in the
// review server's memory — at names derived from the plan's own file name, then exit. Starts no server, opens
// no browser, waits on nothing. Exit 0 nothing outstanding, 2 a human must decide or fix something (both
// files still written). Parsed with System.CommandLine, parallel to `render`; only entered for the `headless`
// verb so the banner / --version behavior above stays exactly as-is.
if (args.Length >= 1 && args[0] == "headless")
{
    return BuildHeadlessRoot().Parse(args).Invoke();
}

// `charter handoff <plan.charter.md> -o <out.md> [--answers <answers.json>]`: read the markdown and, via
// Charter.Core.HandoffMarkdown, convert every ::: directive block into plain CommonMark Guardrails can
// consume — resolving each :::question against the optional --answers file (or flagging it open when no
// answer is supplied) — then write the handoff markdown. Parsed with System.CommandLine, parallel to
// `render`; only entered for the `handoff` verb so the banner / --version behavior above stays as-is.
if (args.Length >= 1 && args[0] == "handoff")
{
    return BuildHandoffRoot().Parse(args).Invoke();
}

// `charter convert <input.md> -o <out.charter.md>`: the mechanical Markdown -> .charter.md SEED transform.
// Via Charter.Core.MarkdownConvert, pass every existing block through unchanged and promote an allow-listed
// "open questions" section's list items into open :::question blocks, then stamp the charter-format-version
// marker via CharterFormat.EnsureVersionMarker so the seed is a valid, round-trippable .charter.md an agent
// enriches. Deliberately NOT an LLM/rich generator (no :::diagram/:::comparison synthesis). Parsed with
// System.CommandLine, parallel to `render`; only entered for the `convert` verb so the banner / --version
// behavior above stays exactly as-is.
if (args.Length >= 1 && args[0] == "convert")
{
    return BuildConvertRoot().Parse(args).Invoke();
}

// `charter skills install [--project] [--target <dir>] [--force]`: extract the skills bundled inside this
// binary (skills/charter + skills/charter-format, embedded as resources) into Claude Code's skills directory
// so `charter-format` becomes discoverable to Guardrails plan-breakdown. Parsed with System.CommandLine,
// parallel to `render`; only entered for the `skills` verb so the banner / --version behavior stays as-is.
if (args.Length >= 1 && args[0] == "skills")
{
    return SkillsCommand.BuildRoot().Parse(args).Invoke();
}

// `charter poll [<plan.charter.md>] [--session <descriptor>] [--url <cap-url>] [--wait] [--apply]`: drain
// queued review feedback (annotations + answers) from a running review server, closing the
// author -> review -> handoff loop. Discovers the session via the per-user registry (or --session / --url),
// proves it live, and writes one JSON envelope to stdout; with --apply it also writes the drained answers
// INLINE into the plan's :::question blocks (the living-document write). Parsed with System.CommandLine,
// parallel to `render`; only entered for the `poll` verb so the banner / --version behavior above stays as-is.
if (args.Length >= 1 && args[0] == "poll")
{
    return BuildPollRoot().Parse(args).Invoke();
}

// `charter resolve <plan.charter.md>`: the solo-review companion to `poll --apply` (§1.6). Apply a human
// reviewer's queued answers INLINE into the plan's :::question blocks when no agent is looping `poll --apply`.
// Prefers a live review server (peek -> apply -> commit), else reads the server-owned durable sidecar. The
// inline write is single-writer-safe (duplicate-id refusal + concurrent-edit precondition, atomic in the plan
// dir). Parsed with System.CommandLine, parallel to `render`; only entered for the `resolve` verb.
if (args.Length >= 1 && args[0] == "resolve")
{
    return BuildResolveRoot().Parse(args).Invoke();
}

// `charter reply <plan.charter.md> --to <comment-id> --body <text>`: the AGENT's voice in the review thread
// (#106). Until now feedback was one-way — a reviewer commented, and the agent either revised the plan or did
// not, which are indistinguishable from the browser. This appends a `reply` record to the agent's own author
// log, so it can accept a comment, push back on it, or ask what was meant, WITHOUT touching the plan (the plan
// stays single-writer) and without the server writing anything. A reviewer with the page open sees it arrive
// over the review-log watch that already exists.
if (args.Length >= 1 && args[0] == "reply")
{
    return BuildReplyRoot().Parse(args).Invoke();
}

// Unknown-verb guard: any non-empty first token that reaches here is neither a known verb/flag (those all
// returned above) nor a help flag — so it is a typo'd or unknown command. Emit a clean error plus the command
// list to stderr and exit NON-ZERO instead of silently falling through to the help banner + exit 0. That
// fall-through was a footgun: `charter renderr plan.charter.md -o out.html && guardrails …` would exit 0 and hand
// Guardrails a stale/missing artifact while every step reported success.
if (args.Length >= 1 && !string.IsNullOrEmpty(args[0]) && args[0] is not ("--help" or "-h" or "-?" or "help"))
{
    Console.Error.WriteLine($"charter: unknown command '{args[0]}'");
    Console.Error.WriteLine("Commands: render, review, export, headless, handoff, convert, skills, poll, resolve, reply. Flags: --version, --help.");
    return 1;
}

// Genuine no-argument (or explicit --help) help path — the ONLY route to `return 0` from here.
AnsiConsole.Write(new FigletText("Charter").Color(Color.Teal));
AnsiConsole.MarkupLine("[grey]Visual, reviewable plans your agent drafts — and you annotate in place.[/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("Status: the local review server is live. Commands: [green]render[/], [green]review[/], [green]export[/], [green]headless[/], [green]handoff[/], [green]convert[/], [green]skills[/], [green]poll[/], [green]resolve[/].");
AnsiConsole.MarkupLine("Try:    [green]charter review <plan.charter.md>[/]  or  [green]charter --version[/]");
return 0;

// Wraps a verb's action body so an expected I/O / listener failure — IOException, UnauthorizedAccessException,
// NotSupportedException, PathTooLongException, System.Net.HttpListenerException — or any other unexpected error
// becomes ONE clean `charter <verb>: <message>` line on stderr + exit 1 (matching the "input plan not found"
// one-liner style) rather than a raw unhandled stack trace. Example: `render -o <an existing directory>` now
// prints `charter render: <message>` instead of dumping `Unhandled exception: System.UnauthorizedAccessException …`.
static int RunVerb(string verb, Func<int> body)
{
    try
    {
        return body();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"charter {verb}: {ex.Message}");
        return 1;
    }
}

// Emit a NON-FATAL warning when a plan's charter-format-version marker is missing or unsupported, then let the
// verb continue: a marker-less .charter.md still renders/reviews/exports/hands off, so this never changes an
// exit code — it only surfaces the format-integrity gap on stderr (Charter #24). The range-enforcement against
// an installed charter-format skill is the breakdown session's job, not the CLI's.
static void WarnOnVersionMarker(string verb, string markdown)
{
    var result = CharterFormat.ValidateVersionMarker(markdown);
    if (result.Status != VersionMarkerStatus.Ok)
    {
        Console.Error.WriteLine($"charter {verb}: warning: {result.Message}");
    }
}

// Emit a NON-FATAL warning when a plan carries two or more :::question blocks sharing an id, then let the verb
// continue. charter-format calls a duplicate question id a REVIEW-TIME error, but the only enforcement used to
// live in QuestionResolution's answer-drain — so `render`, `review` and `handoff` passed duplicates through
// silently and the human answered first and failed afterwards (Charter #48/C6). This surfaces it at the front of
// the loop (review STARTS with render) WITHOUT changing an exit code: a plan that otherwise renders still
// renders, matching the version-marker warning idiom above. Best-effort — a lint failure must never break a verb.
static void WarnOnDuplicateQuestionIds(string verb, string markdown)
{
    IReadOnlyList<string> duplicates;
    try
    {
        duplicates = QuestionResolution.FindDuplicateQuestionIds(markdown);
    }
    catch (Exception)
    {
        return;
    }

    if (duplicates.Count == 0)
    {
        return;
    }

    // ASCII only, so the message is byte-stable across the Win/macOS/Linux console encodings CI runs on
    // (the same rule `charter convert`'s promotion report follows).
    Console.Error.WriteLine(
        $"charter {verb}: warning: duplicate :::question id(s): {string.Join(", ", duplicates)}. "
            + "Question ids must be document-unique -- an answer would resolve into every block sharing an id, "
            + "and `charter poll --apply` / `charter resolve` will refuse the write.");
}

// Emit a NON-FATAL warning to STDERR when an installed `charter` / `charter-format` skill's stamped
// charter-version differs from this running binary (Charter #32) — the skill-version-drift check, mirroring
// Guardrails' #152/#153. Kept OFF stdout so `charter --version` stays a clean `charter <ver>` line, and never
// changes the exit code (still 0). Best-effort: a matching/absent install prints nothing, and any scan
// failure is swallowed rather than allowed to break the version output.
static void WarnOnStaleSkills(string currentVersion)
{
    IReadOnlyList<SkillDriftCheck.StaleSkill> stale;
    try
    {
        stale = SkillDriftCheck.FindStaleSkills(currentVersion);
    }
    catch (Exception)
    {
        return; // a drift scan must never break `charter --version`
    }

    if (stale.Count == 0)
    {
        return;
    }

    Console.Error.WriteLine(
        $"charter: warning: {stale.Count} installed skill(s) are out of date (this tool is {currentVersion}):");
    foreach (SkillDriftCheck.StaleSkill skill in stale)
    {
        Console.Error.WriteLine($"  {skill.Name}: installed {skill.InstalledVersion} at {skill.Directory}");
    }

    Console.Error.WriteLine("  Run `charter skills install --force` to update them.");
}

// Builds the root command hosting the `render` subcommand wired to Charter.Core.CharterRenderer.
static RootCommand BuildRenderRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.charter.md) to render.",
    };
    var outOption = new Option<string>("--out", "-o")
    {
        Description = "Path to write the rendered HTML artifact.",
        Required = true,
    };

    var render = new Command("render", "Render a Charter plan (.charter.md) to one portable HTML file.")
    {
        inputArgument,
        outOption,
    };

    render.SetAction(parseResult => RunVerb("render", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        string outputPath = parseResult.GetValue(outOption)!;

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter render: input plan not found: {inputPath}");
            return 1;
        }

        string markdown = File.ReadAllText(inputPath);
        WarnOnVersionMarker("render", markdown);
        WarnOnDuplicateQuestionIds("render", markdown);
        string html = CharterRenderer.Render(markdown);

        string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputPath, html);
        Console.WriteLine($"Rendered {inputPath} -> {outputPath}");
        return 0;
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        render,
    };
}

// Builds the root command hosting the `export` subcommand wired to Charter.Core.ArtifactExporter.
static RootCommand BuildExportRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.charter.md) to export.",
    };
    var outOption = new Option<string>("--out", "-o")
    {
        Description = "Path to write the self-contained, offline HTML artifact.",
        Required = true,
    };

    var export = new Command("export", "Export a Charter plan (.charter.md) to one self-contained, offline HTML file.")
    {
        inputArgument,
        outOption,
    };

    export.SetAction(parseResult => RunVerb("export", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        string outputPath = parseResult.GetValue(outOption)!;

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter export: input plan not found: {inputPath}");
            return 1;
        }

        string markdown = File.ReadAllText(inputPath);
        WarnOnVersionMarker("export", markdown);

        // The plan's own directory is the confinement root ArtifactExporter uses to resolve and inline the
        // plan's local asset references; reads never escape it.
        string planDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
        string html = ArtifactExporter.Export(markdown, planDirectory);

        string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputPath, html);
        Console.WriteLine($"Exported {inputPath} -> {outputPath}");
        return 0;
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        export,
    };
}

// Builds the root command hosting the `headless` subcommand wired to Charter.Cli.HeadlessCommand (which
// composes Charter.Core's ArtifactExporter and HeadlessRecord). No --out option: BOTH output names are derived
// from the plan's own file name, which is the point — a collecting harness computes them from the plan path
// with nothing passed and nothing configured. --out-dir only relocates the pair.
static RootCommand BuildHeadlessRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.charter.md) to render and record.",
    };
    var outDirOption = new Option<string?>("--out-dir")
    {
        Description =
            "Directory to write <plan-stem>.html and <plan-stem>.headless.json into. Defaults to the plan's "
            + "own directory. Created if missing; the file names never change.",
    };

    var headless = new Command(
        "headless",
        "Render a Charter plan for an UNATTENDED run: the self-contained artifact plus a forensic record "
            + "(anchor->line source map, open decisions, diagnostics) at paths derived from the plan's name. "
            + "Starts no server and never waits for a human. Exit 0 nothing outstanding, 2 a human must decide "
            + "or fix something (both files are still written).")
    {
        inputArgument,
        outDirOption,
    };

    headless.SetAction(parseResult => RunVerb("headless", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        string? outDirectory = parseResult.GetValue(outDirOption);

        return HeadlessCommand.Execute(inputPath, outDirectory);
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        headless,
    };
}

// Builds the root command hosting the `handoff` subcommand wired to Charter.Core.HandoffMarkdown.
static RootCommand BuildHandoffRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.charter.md) to hand off.",
    };
    var outOption = new Option<string>("--out", "-o")
    {
        Description = "Path to write the plain-CommonMark handoff markdown.",
        Required = true,
    };
    var answersOption = new Option<string?>("--answers")
    {
        Description = "Optional path to a JSON file mapping question id -> answer value(s), resolving open questions.",
    };

    var handoff = new Command("handoff", "Convert a reviewed Charter plan (.charter.md) to plain-CommonMark handoff markdown for Guardrails.")
    {
        inputArgument,
        outOption,
        answersOption,
    };

    handoff.SetAction(parseResult => RunVerb("handoff", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        string outputPath = parseResult.GetValue(outOption)!;
        string? answersPath = parseResult.GetValue(answersOption);

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter handoff: input plan not found: {inputPath}");
            return 1;
        }

        // --answers is OPTIONAL: when omitted, answers stays null and every :::question is handed off as an
        // open/unresolved question (a legitimate, common case). When supplied, the file is parsed into the
        // flat id -> value(s) shape HandoffMarkdown.Emit resolves against.
        IReadOnlyDictionary<string, IReadOnlyList<string>>? answers = null;
        if (!string.IsNullOrEmpty(answersPath))
        {
            if (!File.Exists(answersPath))
            {
                Console.Error.WriteLine($"charter handoff: answers file not found: {answersPath}");
                return 1;
            }

            try
            {
                answers = ReadAnswers(answersPath);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"charter handoff: could not parse answers JSON: {ex.Message}");
                return 1;
            }
        }

        string markdown = File.ReadAllText(inputPath);
        WarnOnVersionMarker("handoff", markdown);
        WarnOnDuplicateQuestionIds("handoff", markdown);
        string handoffMarkdown = HandoffMarkdown.Emit(markdown, answers);

        string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputPath, handoffMarkdown);
        Console.WriteLine($"Handed off {inputPath} -> {outputPath}");
        return 0;
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        handoff,
    };
}

// Builds the root command hosting the `convert` subcommand wired to Charter.Core.MarkdownConvert.
static RootCommand BuildConvertRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the plain Markdown document (.md) to convert into a .charter.md seed.",
    };
    var outOption = new Option<string>("--out", "-o")
    {
        Description = "Path to write the .charter.md seed.",
        Required = true,
    };

    var convert = new Command("convert", "Convert a plain Markdown document into a valid .charter.md seed (best-effort; an agent then enriches it).")
    {
        inputArgument,
        outOption,
    };

    convert.SetAction(parseResult => RunVerb("convert", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        string outputPath = parseResult.GetValue(outOption)!;

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter convert: input document not found: {inputPath}");
            return 1;
        }

        // Convert does the block pass-through + open-questions heuristic; EnsureVersionMarker (slice 3, the SSOT
        // for the frontmatter marker) stamps charter-format-version so the seed is a valid, round-trippable
        // .charter.md. No version-marker warning here: a plain .md is expected to be marker-less, and convert's
        // whole job is to add the marker.
        string markdown = File.ReadAllText(inputPath);
        ConvertResult conversion = MarkdownConvert.Convert(markdown);
        string seed = CharterFormat.EnsureVersionMarker(conversion.Markdown);

        string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputPath, seed);
        Console.WriteLine($"Converted {inputPath} -> {outputPath}");

        // Surface the promotion report on stderr (never stdout, never the exit code): one summary per promoted
        // section, plus a warning naming any complex/nested item left as prose so it is never silently dropped
        // (Charter #34).
        ReportConversion(conversion);
        return 0;
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        convert,
    };
}

// Print `charter convert`'s promotion report to STDERR (stdout keeps its single `Converted ...` line, exit
// stays 0). For each section that promoted anything: one summary line, then — when any items were left as prose
// (a complex/nested item convert emits verbatim rather than forcing into a :::question) — a warning naming each
// skipped item by its 1-based ordinal + a lead snippet, and the remedy. This is the whole point of Charter #34:
// convert never silently drops the item, it tells you exactly what it left for hand/agent enrichment. ASCII
// only, so the message is byte-stable across the Win/macOS/Linux console encodings CI runs on.
static void ReportConversion(ConvertResult conversion)
{
    foreach (ConvertedSection section in conversion.Sections)
    {
        int total = section.Promoted + section.Skipped.Count;
        Console.Error.WriteLine(
            $"charter convert: '{section.Heading}' -- promoted {section.Promoted} of {total} item(s).");

        if (section.Skipped.Count == 0)
        {
            continue;
        }

        Console.Error.WriteLine(
            $"charter convert: warning: left {section.Skipped.Count} item(s) as prose (complex/nested structure), not promoted to :::question:");
        foreach (SkippedItem skipped in section.Skipped)
        {
            Console.Error.WriteLine($"  - item {skipped.Ordinal}: \"{skipped.LeadSnippet}\"");
        }

        Console.Error.WriteLine(
            "  Promote these by hand, or let your authoring agent enrich them (see the charter skill).");
    }
}

// Parses a --answers JSON file — a flat object mapping question id -> an array of answer value strings, e.g.
// {"q1": ["A"], "q2": ["some free-text answer"]} — into the IReadOnlyDictionary shape HandoffMarkdown.Emit
// resolves each :::question against. This shape is DELIBERATELY minimal and distinct from
// Charter.Server.Answer: `charter handoff` is an offline, file-in/file-out command with no dependency on a
// running review server or a live session, so the file is hand-authored rather than drained from the API.
static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadAnswers(string answersPath)
{
    string json = File.ReadAllText(answersPath);
    var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json)
        ?? new Dictionary<string, string[]>();

    var answers = new Dictionary<string, IReadOnlyList<string>>(raw.Count);
    foreach (var pair in raw)
    {
        answers[pair.Key] = pair.Value ?? Array.Empty<string>();
    }

    return answers;
}

// Builds the root command hosting the `review` subcommand wired to Charter.Server.ReviewServer.
static RootCommand BuildReviewRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.charter.md) to serve for review.",
    };
    var noOpenOption = new Option<bool>("--no-open")
    {
        Description = "Serve the plan but do not open it in the default browser.",
    };
    var keepAnnotationsOption = new Option<bool>("--keep-annotations")
    {
        Description =
            "Restore queued annotations Charter judged to belong to a different document at this path "
            + "(none of their anchors resolve). Without it they are set aside and reported, never delivered.",
    };

    var review = new Command("review", "Serve a Charter plan (.charter.md) read-only over the loopback review server.")
    {
        inputArgument,
        noOpenOption,
        keepAnnotationsOption,
    };

    review.SetAction(parseResult => RunVerb("review", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        bool noOpen = parseResult.GetValue(noOpenOption);
        bool keepAnnotations = parseResult.GetValue(keepAnnotationsOption);

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter review: input plan not found: {inputPath}");
            return 1;
        }

        string planMarkdown = File.ReadAllText(inputPath);
        WarnOnVersionMarker("review", planMarkdown);
        WarnOnDuplicateQuestionIds("review", planMarkdown);

        // The session confines the served root to the plan's directory and mints a per-session capability
        // key; ReviewServer serves the rendered + SDK-injected plan on a loopback ephemeral port and gates
        // every request on that key. SidecarDirectory turns on durability (§1.6): the server persists its
        // queued annotations/answers to a server-owned sidecar under the per-user state dir and rehydrates
        // from it on start, so a review-process crash before drain loses nothing — and a solo reviewer's
        // answers survive to be applied later by `charter resolve`.
        var session = ReviewSession.Create(inputPath);
        using var server = ReviewServer.Start(
            session,
            new ReviewServerOptions
            {
                SidecarDirectory = StateDirectory.Sidecars(),
                ReviewLog = OpenReviewLog(session.SourcePath),
                KeepStaleAnnotations = keepAnnotations,
            });

        ReportStaleAnnotations(server.StaleAnnotations);

        // Register this running session in the per-user state dir so `charter poll` can discover it (address
        // + capability key + source path) WITHOUT the key ever crossing a command line. Written AFTER Start
        // (the address is now known) and — deliberately — before the ready line. Best-effort: a non-writable
        // state dir must not break `review`, so a write failure only warns and keeps serving.
        var sessionsDirectory = StateDirectory.Sessions();
        string? descriptorPath = null;
        try
        {
            descriptorPath = SessionRegistry.Write(
                sessionsDirectory, SessionDescriptor.ForSession(session, server.Address));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"charter review: could not register the session for `charter poll` ({ex.Message}); still serving.");
        }

        // Handle Ctrl+C gracefully so the listener is disposed (port freed) and the descriptor removed on the
        // way out. Subscribed BEFORE the ready line so an interrupt arriving the instant the server is up is
        // still caught (rather than defaulting to abrupt termination that would orphan the descriptor).
        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, keyPress) =>
        {
            keyPress.Cancel = true;
            stop.Set();
        };

        // The capability URL: the keyless loopback Address plus the session key on the ?key= query string
        // (the form the server authorizes and the browser opens). Address ends in '/', so this yields
        // exactly http://127.0.0.1:<port>/?key=<key>.
        string reviewUrl = $"{server.Address}?key={session.Key.Value}";
        Console.WriteLine($"Charter review server ready: {reviewUrl}");
        Console.Out.Flush();

        if (!noOpen)
        {
            TryOpenBrowser(reviewUrl);
        }

        // Keep serving until the process is stopped (Ctrl+C); remove the session descriptor on clean exit so a
        // subsequent `poll` does not have to prune it as stale.
        try
        {
            stop.Wait();
            return 0;
        }
        finally
        {
            if (descriptorPath is not null)
            {
                SessionRegistry.Delete(descriptorPath);
            }
        }
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        review,
    };
}

// Tell the reviewer, plainly, when a queued set of annotations was NOT rehydrated because it belongs to a
// different document that used to live at this path (Charter #67 — the sidecar is keyed by path, so deleting a
// plan and authoring a new one at the same name used to resurrect the dead document's notes, including ones
// Charter's own anchor resolution had already marked orphaned). Nothing was destroyed: the queue is named, and
// the reviewer chooses — restore it with --keep-annotations, or delete the file to be done with it.
static void ReportStaleAnnotations(StaleAnnotationQueue? stale)
{
    if (stale is null)
    {
        return;
    }

    Console.Error.WriteLine(
        $"charter review: {stale.Count} queued annotation(s) at this path were written against a different "
            + "document -- not one of their anchors resolves in this plan -- so they are NOT being served or "
            + "handed off.");
    Console.Error.WriteLine(
        stale.DurabilityDisabled
            ? $"charter review: they remain at {stale.PreservedAt}, which Charter could not copy aside, so this "
                + "session runs WITHOUT durability rather than overwriting them. Move or delete that file, or "
                + "re-run with --keep-annotations to restore them."
            : $"charter review: they are kept at {stale.PreservedAt}. Re-run with --keep-annotations to restore "
                + "them into this session, or delete that file to discard them.");
}

// Open (or decline to open) the per-author review log beside the plan — the git-mediated team review's write
// half. Charter READS git for the author identity (never writes git state: §5.1 permits the read and forbids
// the mutation) and falls back to a clearly-marked machine-local identity when git cannot name the reviewer.
//
// SOLO IS THE PRIMARY USE CASE (§5.0), and that is binding here in two ways. It creates NOTHING: the .review/
// directory comes into existence only if a record is actually written, so a reviewer who opens a plan and
// writes nothing leaves no trace beside their file. And it says NOTHING: both the §7 permanence notice and the
// identity warning fire only when the review directory is actually TRACKED by git — i.e. the reviewer has opted
// into sharing. Untracked, gitignored, or not a repo means silent, and `charter review` behaves exactly as it
// did before the review log existed. A review must NEVER fail for want of a log, so any failure only warns and
// returns null.
static ReviewLogWriter? OpenReviewLog(string planPath)
{
    try
    {
        var identity = GitIdentity.Resolve(Path.GetDirectoryName(planPath) ?? Directory.GetCurrentDirectory());
        var writer = new ReviewLogWriter(planPath, identity.Author);

        // A directory git already tracks means teammates' records are here and this reviewer is joining a
        // shared review — the only case in which "your identity means nothing to a teammate" is a real problem.
        if (!identity.FromGit && GitTracking.IsTracked(writer.ReviewDirectory))
        {
            Console.Error.WriteLine(
                $"charter review: warning: {identity.Reason}; your comments will be attributed to the local "
                    + $"identity {identity.Author.Email}, which means nothing on a teammate's machine. Set "
                    + "`git config user.email` (and user.name) before reviewing as part of a team.");
        }

        // §7, stated when it becomes true rather than on every session: at the moment the FIRST record lands,
        // and only if that record is landing somewhere git tracks. Before the first write there is nothing
        // permanent to warn about; for a solo reviewer there never is.
        writer.OnFirstRecordWritten = () =>
        {
            if (!GitTracking.IsTracked(writer.ReviewDirectory))
            {
                return;
            }

            Console.Error.WriteLine(
                $"charter review: review comments are written to {writer.LogPath}, which git tracks -- they "
                    + "travel to your teammates by git and are permanent in history. For local-only review, add "
                    + $"'*{ReviewLogPaths.DirectorySuffix}/' to .gitignore.");
        };

        return writer;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"charter review: could not open the review log beside the plan ({ex.Message}); reviewing "
                + "local-only, and your comments will not travel to teammates.");
        return null;
    }
}

// Builds the root command hosting the `poll` subcommand wired to Charter.Cli.PollCommand (which orchestrates
// Charter.Server's ReviewClient / SessionRegistry / PollEnvelope). The plan argument is OPTIONAL: omitted, poll
// auto-selects the single live session. With a plan named and NO live session, poll falls back to the committed
// review log beside it — the server-less read that lets an agent which is executing (not reviewing) pick up a
// teammate's comments.
static RootCommand BuildPollRoot()
{
    var inputArgument = new Argument<string?>("input")
    {
        Description = "Optional path to the Charter plan (.charter.md) to drain. Omitted: auto-select the single live session. Named: falls back to the plan's committed review log when no session is live.",
        Arity = ArgumentArity.ZeroOrOne,
    };
    var sessionOption = new Option<string?>("--session")
    {
        Description = "Path to an explicit session descriptor file to read instead of discovering by plan.",
    };
    var urlOption = new Option<string?>("--url")
    {
        Description = "Capability URL (http://127.0.0.1:PORT/?key=KEY) to drain directly, bypassing session discovery.",
    };
    var waitOption = new Option<bool>("--wait")
    {
        Description = "Run one native long-poll cycle for annotations before draining, instead of a non-blocking immediate drain. Live sessions only.",
    };
    var applyOption = new Option<bool>("--apply")
    {
        Description = "Write the drained answers INLINE into the plan's :::question blocks (atomic in-place write), resolving them.",
    };

    var poll = new Command(
        "poll",
        "Drain queued review feedback (annotations + answers) from a live review session, or -- with no live "
            + "session -- read the plan's committed review log. Exit 0 drained, 2 nothing queued, 3 neither a "
            + "session nor a readable log, 4 drain state unknown, 5 --apply refused.")
    {
        inputArgument,
        sessionOption,
        urlOption,
        waitOption,
        applyOption,
    };

    poll.SetAction(parseResult => RunVerb("poll", () =>
    {
        string? input = parseResult.GetValue(inputArgument);
        string? sessionPath = parseResult.GetValue(sessionOption);
        string? url = parseResult.GetValue(urlOption);
        bool wait = parseResult.GetValue(waitOption);
        bool apply = parseResult.GetValue(applyOption);

        return PollCommand.Execute(input, sessionPath, url, wait, apply);
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        poll,
    };
}

// Builds the root command hosting the `resolve` subcommand wired to Charter.Cli.ResolveCommand (which applies
// a solo reviewer's queued answers inline, via a live server or the durable sidecar).
// `charter reply` — the agent's reply into a review thread (#106). Deliberately a WRITE of one record to the
// agent's own author log and nothing else: it never edits the plan (single-writer), never contacts the review
// server, and never settles the comment (a reply is not a state op — deciding a comment is DONE stays a
// separate, deliberate `resolve`). That is what lets it be safe to call mid-review with a page open.
static RootCommand BuildReplyRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.charter.md) whose review thread to reply in.",
    };

    var toOption = new Option<string>("--to")
    {
        Description =
            "The id of the comment being replied to (the `id` on a review-log comment, as delivered on the "
            + "`charter poll` envelope).",
        Required = true,
    };

    var bodyOption = new Option<string>("--body")
    {
        Description =
            "The reply text. Say what you did about the comment — accepted it, disagreed and why, or what you "
            + "need clarified. A reviewer reads this in the panel next to their own note.",
        Required = true,
    };

    var humanOption = new Option<bool>("--as-human")
    {
        Description =
            "Attribute the reply to the human reviewer rather than to an agent. Off by default: this verb "
            + "exists for the AGENT's voice, and mislabelling it would put words in the reviewer's mouth.",
    };

    var reply = new Command(
        "reply",
        "Reply to a review comment in its thread — the agent's channel to accept, push back, or ask for clarification without touching the plan.")
    {
        inputArgument,
        toOption,
        bodyOption,
        humanOption,
    };

    reply.SetAction(parseResult => RunVerb("reply", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter reply: input plan not found: {inputPath}");
            return 1;
        }

        string target = parseResult.GetValue(toOption)!;
        string body = parseResult.GetValue(bodyOption)!;

        if (string.IsNullOrWhiteSpace(body))
        {
            Console.Error.WriteLine("charter reply: --body is empty; a reply with nothing in it tells the reviewer nothing.");
            return 1;
        }

        // A review must never fail for want of a log (same contract as `charter review`), but `reply` is a
        // verb whose ENTIRE purpose is to write one — so here a null writer is a real failure, not a silent
        // no-op. Reporting success while saying nothing to the reviewer is the exact defect #106 is about.
        ReviewLogWriter? writer = OpenReviewLog(inputPath);
        if (writer is null)
        {
            Console.Error.WriteLine(
                $"charter reply: could not open the review log beside {inputPath}; nothing was written.");
            return 1;
        }

        string actor = parseResult.GetValue(humanOption) ? ReviewActors.Human : ReviewActors.Agent;
        ReviewRecord written = writer.AppendReply(target, body, actor);

        Console.WriteLine($"Replied to {target} as {actor} ({written.Id}) -> {writer.LogPath}");
        return 0;
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        reply,
    };
}

static RootCommand BuildResolveRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.charter.md) whose queued answers to apply inline.",
    };

    var applyStaleOption = new Option<bool>("--apply-stale-answers")
    {
        Description =
            "Apply queued answers even when their :::question has CHANGED SHAPE since they were given (its "
            + "title, mode, target or options differ). Without it such answers are reported and left queued, "
            + "never written into the plan and never discarded.",
    };

    var resolve = new Command(
        "resolve",
        "Apply a solo reviewer's queued :::question answers INLINE into the plan (single-writer-safe; via a live review server or the durable sidecar).")
    {
        inputArgument,
        applyStaleOption,
    };

    resolve.SetAction(parseResult => RunVerb("resolve", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter resolve: input plan not found: {inputPath}");
            return 1;
        }

        return ResolveCommand.Execute(inputPath, parseResult.GetValue(applyStaleOption));
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        resolve,
    };
}

// Opens the served capability URL in the system default browser. Best-effort: a headless or
// browser-less environment just gets a stderr hint (stdout stays the single ready line).
static void TryOpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
        else
        {
            Process.Start("xdg-open", url);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"charter review: could not open a browser automatically ({ex.Message}). Open {url} manually.");
    }
}
