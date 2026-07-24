using System.CommandLine;

namespace Charter.Cli;

/// <summary>
/// The <c>charter skills</c> verb group. <c>charter skills install</c> extracts the skills bundled inside
/// the tool (<see cref="SkillsInstaller"/>) into Claude Code's skills directory so <c>charter</c> and
/// <c>charter-format</c> become discoverable — the mechanism Guardrails' plan-breakdown relies on to find
/// <c>charter-format</c>. Default destination is <c>~/.claude/skills</c> (every repo); <c>--project</c>
/// targets <c>./.claude/skills</c>; <c>--target</c> overrides with an explicit path; <c>--force</c>
/// overwrites folders that already exist. Mirrors Guardrails' <c>skills install</c>.
///
/// Built as its own root (parallel to <c>render</c> / <c>review</c> / <c>export</c> / <c>handoff</c> in
/// <c>Program.cs</c>) so the banner and <c>--version</c> paths there stay untouched.
/// </summary>
internal static class SkillsCommand
{
    /// <summary>Root command hosting the <c>skills</c> group; <c>Program.cs</c> parses <c>skills …</c> against it.</summary>
    public static RootCommand BuildRoot()
    {
        var targetOption = new Option<string?>("--target")
        {
            Description = "Explicit directory to install the skills into (overrides the default and --project).",
        };
        var projectOption = new Option<bool>("--project")
        {
            Description = "Install into ./.claude/skills in the current directory instead of the user home.",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite a skill folder that already exists in the target; otherwise it is skipped.",
        };

        var install = new Command("install", "Install the bundled Charter skills into Claude Code's skills directory.")
        {
            targetOption,
            projectOption,
            forceOption,
        };

        install.SetAction(parseResult => RunInstall(
            parseResult.GetValue(targetOption),
            parseResult.GetValue(projectOption),
            parseResult.GetValue(forceOption)));

        var skills = new Command("skills", "Manage the Charter Claude Code skills bundled with this tool.")
        {
            install,
        };

        // Bare `charter skills` (no subcommand): print usage and exit 0. An unknown subcommand like
        // `charter skills bogus` is an unmatched-token parse error handled by System.CommandLine (non-zero)
        // before this action ever runs.
        skills.SetAction(_ =>
        {
            Console.WriteLine("Usage: charter skills install [--project] [--target <dir>] [--force]");
            Console.WriteLine("Run 'charter skills install --help' for details.");
            return 0;
        });

        return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
        {
            skills,
        };
    }

    private static int RunInstall(string? target, bool project, bool force)
    {
        if (project && !string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("charter skills install: specify either --target or --project, not both.");
            return 1;
        }

        try
        {
            string targetDir = SkillsInstaller.ResolveTargetDir(target, project);
            string toolVersion = CharterVersion.Current;

            IReadOnlyList<SkillsInstaller.SkillResult> results =
                SkillsInstaller.InstallAll(targetDir, force, toolVersion);

            foreach (SkillsInstaller.SkillResult result in results)
            {
                string note = result.Outcome switch
                {
                    SkillsInstaller.SkillOutcome.Installed => "installed",
                    SkillsInstaller.SkillOutcome.Skipped => "skipped (already present; use --force to overwrite)",
                    _ => result.Outcome.ToString(),
                };
                Console.WriteLine($"  {result.Name,-20} {note}");
            }

            int installed = results.Count(result => result.Outcome == SkillsInstaller.SkillOutcome.Installed);
            int skipped = results.Count(result => result.Outcome == SkillsInstaller.SkillOutcome.Skipped);

            Console.WriteLine();
            Console.WriteLine($"{installed} skill(s) installed (v{toolVersion}), {skipped} skipped -> {targetDir}");
            Console.WriteLine("Restart Claude Code to pick up the installed skills.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or PathTooLongException or InvalidOperationException)
        {
            Console.Error.WriteLine($"charter skills install: {ex.Message}");
            return 1;
        }
    }
}
