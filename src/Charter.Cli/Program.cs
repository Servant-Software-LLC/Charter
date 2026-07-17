using System.CommandLine;
using System.Reflection;
using Charter.Core;
using Spectre.Console;

// Scaffold entry point. The real CLI — `charter <plan.mdx>` opening a local review server that
// renders the block plan in the browser for in-place annotation, plus `export` and a `poll` feedback
// loop — lands in later milestones (see README.md). Today the surface is a banner + --version and the
// first working verb, `render`, which turns a Charter plan (.mdx) into one portable HTML artifact via
// the Charter.Core renderer.

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
