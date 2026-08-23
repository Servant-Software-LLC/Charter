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

        var overwriteTrackedOption = new Option<bool>("--overwrite-tracked")
        {
            Description =
                "Allow --force to overwrite git-tracked skill source that has uncommitted changes. "
                + "Without it, that install is refused (Charter #154).",
        };

        var install = new Command("install", "Install the bundled Charter skills into Claude Code's skills directory.")
        {
            targetOption,
            projectOption,
            forceOption,
            overwriteTrackedOption,
        };

        install.SetAction(parseResult => RunInstall(
            parseResult.GetValue(targetOption),
            parseResult.GetValue(projectOption),
            parseResult.GetValue(forceOption),
            parseResult.GetValue(overwriteTrackedOption)));

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

    /// <summary>
    /// Refuse a <c>--force</c> install that would delete git-tracked skill source carrying uncommitted work
    /// (Charter #154). Returns false when the install must not proceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--force</c> deletes its destination and re-extracts the bundled copy. Harmless for
    /// <c>~/.claude/skills</c>, which holds installed copies and nothing else; destructive for a repository
    /// that tracks its skills as SOURCE — and the path there is not obscure, because <c>charter --version</c>
    /// PRINTS this command as the remedy for stale skills, so the natural response is to run it wherever you
    /// are standing.
    /// </para>
    /// <para>
    /// Keyed on <b>tracked AND dirty</b>, never tracked alone: overwriting a CLEAN tracked folder loses
    /// nothing git cannot give back, so refusing there would be noise — and a guard that fires when nothing
    /// is at stake is one people learn to pass <c>--overwrite-tracked</c> to reflexively.
    /// </para>
    /// </remarks>
    private static bool RefuseToClobberAuthoredSource(
        string targetDir, bool force, bool overwriteTracked, string toolVersion)
    {
        if (!force || overwriteTracked || !Directory.Exists(targetDir))
        {
            return true;   // nothing is being deleted, or the operator has said to proceed anyway
        }

        var dirty = new List<string>();
        foreach (string skillDir in Directory.GetDirectories(targetDir))
        {
            if (GitWorkingTree.TracksAnyFileUnder(skillDir) && GitWorkingTree.HasLocalChangesUnder(skillDir))
            {
                dirty.Add(skillDir);
            }
        }

        if (dirty.Count == 0)
        {
            return true;
        }

        Console.Error.WriteLine(
            "charter skills install: refusing to overwrite git-tracked skill source(s) with uncommitted changes:");
        foreach (string dir in dirty)
        {
            Console.Error.WriteLine($"  - {dir}");
        }

        // Say what --force would DO, not merely that it is declined: the operator has to be able to weigh it.
        Console.Error.WriteLine(
            $"  --force would delete each of those folders and replace them with the copy bundled in charter "
                + $"v{toolVersion}, destroying work git cannot restore.");
        Console.Error.WriteLine(
            "  Commit or stash those changes first, or pass --overwrite-tracked to proceed anyway.");
        return false;
    }

    /// <summary>
    /// Say so when the resolved target landed INSIDE a git working tree (Charter #194), naming the repository
    /// and the exact rules to paste. Prints nothing otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The install itself is not wrong — it did what it was told. What was wrong was the SILENCE: skills
    /// written through a symlinked <c>~/.claude/skills</c> sat as untracked folders in a personal repository
    /// until they surfaced in <c>git status</c> much later with no memory of where they came from. Committing
    /// them is the outcome to avoid, because <see cref="SkillFrontmatterStamper"/> stamps
    /// <c>metadata.charter-version</c> into each <c>SKILL.md</c> and <c>SkillDriftCheck</c> diffs against it:
    /// a committed copy is a vendored snapshot that goes stale, diffs on every reinstall, and draws drift
    /// warnings. So the advisory carries the REASON, not just the observation.
    /// </para>
    /// <para>
    /// Silent under <c>--project</c>, whose entire meaning is "put this in the repository so teammates get
    /// it". There, landing in a work tree is the intent, and explaining how to ignore it would be noise.
    /// </para>
    /// <para>
    /// Advisory ONLY: nothing here writes an ignore rule anywhere. Two reasons, and they are independent.
    /// A tool that silently edits ignore rules in a repository the user never named — reached only through a
    /// symlink from a home directory — is worse than one that says nothing; and the obvious shortcut of
    /// dropping a self-ignoring <c>.gitignore</c> (<c>*</c>) into each skill folder would silently break
    /// <c>--project</c>, because a <c>.gitignore</c> in a subdirectory overrides a negation in the repository
    /// root. Against the common <c>.claude/*</c> + <c>!.claude/skills/</c> convention that stops the skill
    /// being delivered at all, and the symptom of an over-broad ignore rule is that nothing appears.
    /// (<c>SkillsInstallGitAdvisoryTests</c> pins that experiment.) Whether to commit these stays the
    /// operator's decision; this only makes sure it is a decision rather than an accident.
    /// </para>
    /// </remarks>
    private static void ReportInstallIntoAWorkingTree(
        string targetDir, bool project, IReadOnlyList<SkillsInstaller.SkillResult> results, string toolVersion)
    {
        if (project)
        {
            return;
        }

        // Asked AFTER the install, so the directory exists to be asked about and the answer describes where
        // the skills really are rather than where they were nominally headed.
        GitWorkingTree.WorkTreeLocation? location = GitWorkingTree.LocateWorkTree(targetDir);
        if (location is null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Note: {results.Count} Charter skill(s) now sit inside a git working tree ({location.Root}).");
        Console.WriteLine(
            $"  They are tool-managed and version-stamped (v{toolVersion}); 'charter skills install --force' "
                + "re-creates them, so a copy committed here goes stale and draws drift warnings.");
        Console.WriteLine("  To keep them out of source control, add to that repository's .gitignore:");
        foreach (SkillsInstaller.SkillResult result in results)
        {
            Console.WriteLine($"    /{location.PrefixWithinRoot}{result.Name}/");
        }
    }

    private static int RunInstall(string? target, bool project, bool force, bool overwriteTracked)
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

            if (!RefuseToClobberAuthoredSource(targetDir, force, overwriteTracked, toolVersion))
            {
                return 1;
            }

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

            ReportInstallIntoAWorkingTree(targetDir, project, results, toolVersion);
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
