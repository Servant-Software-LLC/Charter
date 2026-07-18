using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Charter.Core;
using Charter.Server;
using Spectre.Console;

// Scaffold entry point. The real CLI — `charter <plan.mdx>` opening a local review server that
// renders the block plan in the browser for in-place annotation, plus `export` and a `poll` feedback
// loop — lands in later milestones (see README.md). Today the surface is a banner + --version, the
// `render` verb (a Charter plan (.mdx) -> one portable HTML artifact via the Charter.Core renderer),
// and the `review` verb, which renders the plan and serves it read-only over the loopback review
// server for in-browser preview.

if (args.Length >= 1 && args[0] is "--version" or "-v")
{
    Version? v = Assembly.GetExecutingAssembly().GetName().Version;
    Console.WriteLine($"charter {v?.ToString(3) ?? "0.0.0"}");
    return 0;
}

// `charter render <plan.mdx> -o <out.html>`: read the markdown, render it through Charter.Core, and
// write the HTML artifact. Parsed with System.CommandLine; only entered for the `render` verb so the
// banner / --version behavior above stays exactly as-is.
if (args.Length >= 1 && args[0] == "render")
{
    return BuildRenderRoot().Parse(args).Invoke();
}

// `charter review <plan.mdx> [--no-open]`: render the plan and serve it read-only over the loopback
// review server for in-browser preview. Parsed with System.CommandLine, parallel to `render`; only
// entered for the `review` verb so the banner / --version behavior above stays exactly as-is.
if (args.Length >= 1 && args[0] == "review")
{
    return BuildReviewRoot().Parse(args).Invoke();
}

AnsiConsole.Write(new FigletText("Charter").Color(Color.Teal));
AnsiConsole.MarkupLine("[grey]Visual, reviewable plans your agent drafts — and you annotate in place.[/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("Status: [yellow]scaffold[/]. The local review server lands next; try [green]charter render[/].");
AnsiConsole.MarkupLine("Try:    [green]charter --version[/]");
return 0;

// Builds the root command hosting the `render` subcommand wired to Charter.Core.CharterRenderer.
static RootCommand BuildRenderRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.mdx) to render.",
    };
    var outOption = new Option<string>("--out", "-o")
    {
        Description = "Path to write the rendered HTML artifact.",
        Required = true,
    };

    var render = new Command("render", "Render a Charter plan (.mdx) to one portable HTML file.")
    {
        inputArgument,
        outOption,
    };

    render.SetAction(parseResult =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        string outputPath = parseResult.GetValue(outOption)!;

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter render: input plan not found: {inputPath}");
            return 1;
        }

        string markdown = File.ReadAllText(inputPath);
        string html = CharterRenderer.Render(markdown);

        string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputPath, html);
        Console.WriteLine($"Rendered {inputPath} -> {outputPath}");
        return 0;
    });

    return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
    {
        render,
    };
}

// Builds the root command hosting the `review` subcommand wired to Charter.Server.ReviewServer.
static RootCommand BuildReviewRoot()
{
    var inputArgument = new Argument<string>("input")
    {
        Description = "Path to the Charter plan (.mdx) to serve for review.",
    };
    var noOpenOption = new Option<bool>("--no-open")
    {
        Description = "Serve the plan but do not open it in the default browser.",
    };

    var review = new Command("review", "Serve a Charter plan (.mdx) read-only over the loopback review server.")
    {
        inputArgument,
        noOpenOption,
    };

    review.SetAction(parseResult =>
    {
        string inputPath = parseResult.GetValue(inputArgument)!;
        bool noOpen = parseResult.GetValue(noOpenOption);

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"charter review: input plan not found: {inputPath}");
            return 1;
        }

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
    });

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
