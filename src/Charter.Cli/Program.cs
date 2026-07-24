using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
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
    Version? v = Assembly.GetExecutingAssembly().GetName().Version;
    Console.WriteLine($"charter {v?.ToString(3) ?? "0.0.0"}");
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

// `charter handoff <plan.charter.md> -o <out.md> [--answers <answers.json>]`: read the markdown and, via
// Charter.Core.HandoffMarkdown, convert every ::: directive block into plain CommonMark Guardrails can
// consume — resolving each :::question against the optional --answers file (or flagging it open when no
// answer is supplied) — then write the handoff markdown. Parsed with System.CommandLine, parallel to
// `render`; only entered for the `handoff` verb so the banner / --version behavior above stays as-is.
if (args.Length >= 1 && args[0] == "handoff")
{
    return BuildHandoffRoot().Parse(args).Invoke();
}

// `charter skills install [--project] [--target <dir>] [--force]`: extract the skills bundled inside this
// binary (skills/charter + skills/charter-format, embedded as resources) into Claude Code's skills directory
// so `charter-format` becomes discoverable to Guardrails plan-breakdown. Parsed with System.CommandLine,
// parallel to `render`; only entered for the `skills` verb so the banner / --version behavior stays as-is.
if (args.Length >= 1 && args[0] == "skills")
{
    return SkillsCommand.BuildRoot().Parse(args).Invoke();
}

// Unknown-verb guard: any non-empty first token that reaches here is neither a known verb/flag (those all
// returned above) nor a help flag — so it is a typo'd or unknown command. Emit a clean error plus the command
// list to stderr and exit NON-ZERO instead of silently falling through to the help banner + exit 0. That
// fall-through was a footgun: `charter renderr plan.charter.md -o out.html && guardrails …` would exit 0 and hand
// Guardrails a stale/missing artifact while every step reported success.
if (args.Length >= 1 && !string.IsNullOrEmpty(args[0]) && args[0] is not ("--help" or "-h" or "-?" or "help"))
{
    Console.Error.WriteLine($"charter: unknown command '{args[0]}'");
    Console.Error.WriteLine("Commands: render, review, export, handoff, skills. Flags: --version, --help.");
    return 1;
}

// Genuine no-argument (or explicit --help) help path — the ONLY route to `return 0` from here.
AnsiConsole.Write(new FigletText("Charter").Color(Color.Teal));
AnsiConsole.MarkupLine("[grey]Visual, reviewable plans your agent drafts — and you annotate in place.[/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("Status: the local review server is live. Commands: [green]render[/], [green]review[/], [green]export[/], [green]handoff[/], [green]skills[/].");
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

    var review = new Command("review", "Serve a Charter plan (.charter.md) read-only over the loopback review server.")
    {
        inputArgument,
        noOpenOption,
    };

    review.SetAction(parseResult => RunVerb("review", () =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        bool noOpen = parseResult.GetValue(noOpenOption);

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter review: input plan not found: {inputPath}");
            return 1;
        }

        WarnOnVersionMarker("review", File.ReadAllText(inputPath));

        // The session confines the served root to the plan's directory and mints a per-session capability
        // key; ReviewServer serves the rendered + SDK-injected plan on a loopback ephemeral port and gates
        // every request on that key.
        var session = ReviewSession.Create(inputPath);
        using var server = ReviewServer.Start(session);

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

        // Keep serving until the process is stopped (Ctrl+C). Handle Ctrl+C gracefully so the listener is
        // disposed (and its port freed) on the way out rather than left to the OS.
        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, keyPress) =>
        {
            keyPress.Cancel = true;
            stop.Set();
        };
        stop.Wait();
        return 0;
    }));

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        review,
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
