using Charter.Cli;
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

// Verb dispatch, driven by the ONE catalog in CharterCommands.Commands rather than by a chain of hand-written
// `if (args[0] == "<verb>")` blocks. Each entry's root command hosts a subcommand of the same name, so the FULL
// argument list (verb included) is what gets parsed — exactly as each `if` block used to do. Every per-verb
// rationale that used to sit on those blocks now sits on its catalog entry, verbatim.
//
// This loop and the two command lists printed below all read that one catalog, which is the point of Charter
// #138: dispatch and help cannot disagree, because there is nothing left to keep in step by hand. A verb added
// to the catalog is dispatchable AND listed; there is no second place to forget.
if (args.Length >= 1)
{
    foreach (var command in CharterCommands.Commands)
    {
        if (args[0] == command.Name)
        {
            return command.Build().Parse(args).Invoke();
        }
    }
}

// Unknown-verb guard: any non-empty first token that reaches here is neither a known verb/flag (those all
// returned above) nor a help flag — so it is a typo'd or unknown command. Emit a clean error plus the command
// list to stderr and exit NON-ZERO instead of silently falling through to the help banner + exit 0. That
// fall-through was a footgun: `charter renderr plan.charter.md -o out.html && guardrails …` would exit 0 and hand
// Guardrails a stale/missing artifact while every step reported success.
if (args.Length >= 1 && !string.IsNullOrEmpty(args[0]) && args[0] is not ("--help" or "-h" or "-?" or "help"))
{
    Console.Error.WriteLine($"charter: unknown command '{args[0]}'");
    Console.Error.WriteLine(CharterCommands.CommandListLine);
    return 1;
}

// Genuine no-argument (or explicit --help) help path — the ONLY route to `return 0` from here.
AnsiConsole.Write(new FigletText("Charter").Color(Color.Teal));
AnsiConsole.MarkupLine("[grey]Visual, reviewable plans your agent drafts — and you annotate in place.[/]");
AnsiConsole.WriteLine();
AnsiConsole.MarkupLine(CharterCommands.BannerCommandsLine);
AnsiConsole.MarkupLine("Try:    [green]charter review <plan.charter.md>[/]  or  [green]charter --version[/]");

// A POINTER, never a table (Charter #173). Charter's verbs do not share one exit-code vocabulary — the
// unattended path's 2 and the review drain's 2 mean different things — so a banner table would have to be
// either wrong or long enough to bury the command list. Each verb's own --help carries its codes and the
// warning; this line only makes sure a pipeline author knows to go and look.
AnsiConsole.MarkupLine(
    "[grey]Exit codes differ per verb and are documented in each one's [/][green]--help[/][grey]; "
        + "Charter's 2 is not one meaning.[/]");
return 0;

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
