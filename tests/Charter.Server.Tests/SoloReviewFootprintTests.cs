using System;
using System.IO;
using System.Linq;
using System.Net;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// §5.0 of <c>docs/plans/03-git-mediated-team-review.md</c> is binding on every slice: <i>"One person using
/// Charter alone remains the main use case. Team review is additive; it must never make solo use heavier… No
/// new required setup."</i>
///
/// The regression these tests exist to prevent: the review-log integration materialised the plan's
/// <c>.review/</c> directory at server start, so merely OPENING a plan dropped an empty directory beside it —
/// for every reviewer, whether or not they ever wrote a word. Creation must be lazy: the directory exists
/// because a record exists, never because a session started.
/// </summary>
[Trait("Category", "ReviewLog")]
public class SoloReviewFootprintTests
{
    private const string Plan =
        "# Solo plan\n\nA paragraph a reviewer might annotate, or might simply read.\n";

    [Fact]
    public void ReviewSession_WithAWriter_CreatesNothingBesideThePlanUntilARecordIsWritten()
    {
        var directory = NewTempDir();
        try
        {
            var planPath = Path.Combine(directory, "plan.charter.md");
            File.WriteAllText(planPath, Plan);
            var before = Snapshot(directory);

            var writer = new ReviewLogWriter(planPath, new ReviewAuthor("Solo", "solo@example.com"));
            using (var server = ReviewServer.Start(
                ReviewSession.Create(planPath),
                new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0, ReviewLog = writer }))
            {
                Assert.False(
                    Directory.Exists(writer.ReviewDirectory),
                    "serving a plan must not create its .review/ directory — only writing a record may (§5.0).");
                Assert.Equal(before, Snapshot(directory));
            }

            // The whole session left no trace beside the plan.
            Assert.Equal(before, Snapshot(directory));

            // ...and the directory still appears the moment there is something to put in it.
            writer.AppendCreate(new ReviewAnchor("some-block", "element", "a quote", null), "a real comment");
            Assert.True(Directory.Exists(writer.ReviewDirectory));
            Assert.True(File.Exists(writer.LogPath));
        }
        finally
        {
            TryDeleteDir(directory);
        }
    }

    [Fact]
    public void FirstRecordWritten_FiresExactlyOnce_AndOnlyAfterAnAppendLands()
    {
        // The hook the CLI hangs §7's permanence notice on. It must be a fact about a file that now exists —
        // fired once, on the first successful append — not a per-session banner.
        var directory = NewTempDir();
        try
        {
            var planPath = Path.Combine(directory, "plan.charter.md");
            File.WriteAllText(planPath, Plan);

            var fired = 0;
            var writer = new ReviewLogWriter(planPath, new ReviewAuthor("Solo", "solo@example.com"))
            {
                OnFirstRecordWritten = () => fired++,
            };

            Assert.Equal(0, fired);

            var anchor = new ReviewAnchor("some-block", "element", "a quote", null);
            writer.AppendCreate(anchor, "first");
            Assert.Equal(1, fired);

            writer.AppendCreate(anchor, "second");
            writer.AppendResolve("cmt_whatever", prev: null);
            Assert.Equal(1, fired);
        }
        finally
        {
            TryDeleteDir(directory);
        }
    }

    /// <summary>Every file and directory beside the plan, as a comparable ordinal-sorted list.</summary>
    private static string Snapshot(string directory)
        => string.Join(
            "\n",
            Directory
                .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .OrderBy(entry => entry, StringComparer.Ordinal));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-solo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
