using System.Reflection;
using Spectre.Console;

// Scaffold entry point. The real CLI — `charter <plan.mdx>` opening a local review server that
// renders the block plan in the browser for in-place annotation, plus `render`, `export`, and a
// `poll` feedback loop — lands in later milestones (see README.md). For now: a banner + --version,
// enough to build, pack as a dotnet tool, and ship as a native binary through the release pipeline.

if (args.Length >= 1 && args[0] is "--version" or "-v")
{
    Version? v = Assembly.GetExecutingAssembly().GetName().Version;
    Console.WriteLine($"charter {v?.ToString(3) ?? "0.0.0"}");
    return 0;
}

AnsiConsole.Write(new FigletText("Charter").Color(Color.Teal));
AnsiConsole.MarkupLine("[grey]Visual, reviewable plans your agent drafts — and you annotate in place.[/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine("Status: [yellow]scaffold[/]. The MDX renderer and local review server land next.");
AnsiConsole.MarkupLine("Try:    [green]charter --version[/]");
return 0;
