using System.CommandLine;
using System.Diagnostics;
using Charter.Server;

namespace Charter.Cli;

/// <summary>
/// <c>charter sessions</c> — see, sweep and stop the review servers this machine is running (Charter #147).
/// </summary>
/// <remarks>
/// <para>
/// A review server is a long-lived foreground process a human starts and, frequently, walks away from. Nothing
/// listed them. The registry that knows about every one of them is a per-user directory of JSON descriptors
/// nobody was ever given a way to read, so the only ways to discover an abandoned server were a failed
/// <c>dotnet tool update</c> (the running binary holds its own file open) or a manual process-table hunt. On
/// one working machine that produced twelve descriptors, eleven of them naming processes that no longer
/// existed, the oldest nineteen days old.
/// </para>
/// <para>
/// This is deliberately a HUMAN verb, in a CLI that is otherwise mostly an agent IPC (Charter #144): it exists
/// to answer "what is running, and how do I stop it?", which is a question only a person asks.
/// </para>
/// </remarks>
internal static class SessionsCommand
{
    /// <summary>Root command hosting the <c>sessions</c> verb; <c>Program.cs</c> parses <c>sessions …</c> against it.</summary>
    public static RootCommand BuildRoot()
    {
        var pruneOption = new Option<bool>("--prune")
        {
            Description = "Delete descriptors whose review process is gone, then list what remains.",
        };
        var stopAllOption = new Option<bool>("--stop-all")
        {
            Description = "Stop every live review server listed, then prune. Annotations already saved are on disk.",
        };

        var sessions = new Command("sessions", "List the review servers running on this machine (and prune or stop them).")
        {
            pruneOption,
            stopAllOption,
        };

        sessions.SetAction(parseResult => RunVerb("sessions", () =>
        {
            var prune = parseResult.GetValue(pruneOption);
            var stopAll = parseResult.GetValue(stopAllOption);
            var directory = StateDirectory.Sessions();

            if (stopAll)
            {
                return StopAll(directory);
            }

            if (prune)
            {
                var removed = SessionRegistry.Prune(directory);
                Console.WriteLine(
                    removed == 0
                        ? "No stale sessions to prune."
                        : $"Pruned {removed} stale session descriptor(s).");
            }

            return List(directory);
        }));

        return new RootCommand("Charter — visual, reviewable plans your agent drafts, annotated in place.")
        {
            sessions,
        };
    }

    private static int List(string directory)
    {
        // Enumerate prunes as it reads, so anything listed is a session whose process still exists.
        var entries = SessionRegistry.Enumerate(directory);
        if (entries.Count == 0)
        {
            Console.WriteLine("No review servers are running.");
            return 0;
        }

        Console.WriteLine($"{entries.Count} review server(s) running:");
        foreach (var entry in entries)
        {
            var descriptor = entry.Descriptor;
            // The key is deliberately NOT printed: it is the session's capability, and a listing is the last
            // place it should leak. The address alone is useless without it, which is the point.
            Console.WriteLine(
                $"  pid {descriptor.Pid,-8} {descriptor.Address,-28} {descriptor.SourcePath}");
        }

        Console.WriteLine();
        Console.WriteLine("Stop one with your shell (kill/Stop-Process), or all of them with `charter sessions --stop-all`.");
        return 0;
    }

    private static int StopAll(string directory)
    {
        var entries = SessionRegistry.Enumerate(directory);
        if (entries.Count == 0)
        {
            SessionRegistry.Prune(directory);
            Console.WriteLine("No review servers are running.");
            return 0;
        }

        var stopped = 0;
        foreach (var entry in entries)
        {
            var descriptor = entry.Descriptor;
            try
            {
                using var process = Process.GetProcessById(descriptor.Pid);
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
                stopped++;
                Console.WriteLine($"Stopped pid {descriptor.Pid} ({descriptor.SourceFile}).");
            }
            catch (Exception ex)
            {
                // Never fail the whole sweep over one process we could not reach.
                Console.Error.WriteLine(
                    $"charter sessions: could not stop pid {descriptor.Pid} ({descriptor.SourceFile}): {ex.Message}");
            }
        }

        var removed = SessionRegistry.Prune(directory);
        Console.WriteLine(
            $"Stopped {stopped} of {entries.Count} review server(s); pruned {removed} descriptor(s).");
        Console.WriteLine(
            "Review notes are written to the plan's .review log as they are saved, so nothing unsaved was lost.");
        return 0;
    }

    // Mirrors the other verbs' top-level guard so an unexpected fault reports as a charter error rather than
    // an unhandled exception trace.
    private static int RunVerb(string verb, Func<int> body)
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
}
