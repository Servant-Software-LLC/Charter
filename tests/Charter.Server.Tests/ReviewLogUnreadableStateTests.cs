using System;
using System.IO;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #221, second pass — a read that could read NOTHING must not say "there is nothing".
///
/// <para>
/// #252 gave the review log a third outcome for an ABSENT <c>.review/</c>, and the flaky browser test failed
/// again on master anyway, with the identical signature: <c>items=0</c>, focus dropped to <c>&lt;body&gt;</c>.
/// Its trace showed the log HAD loaded two comments and then emptied, which ruled out an absent directory.
/// </para>
/// <para>
/// The mechanism these pin: <see cref="ReviewLogWriter"/> appends under <see cref="FileShare.None"/> and holds
/// the file through an fsync. A concurrent read hits the sharing conflict, spends its ~30ms per-file retry, and
/// records the file as unreadable. One reviewer writes one log, so that file is EVERY log — the fold of nothing
/// was returned as <see cref="ReviewLogOutcome.Present"/> with zero comments, the panel trusted it, and the note
/// the reviewer was focused on disappeared.
/// </para>
/// <para>
/// Each test holds a REAL <see cref="FileShare.None"/> lock — the writer's own mechanism — and first proves that
/// lock actually blocks a reader on the platform running it. .NET emulates file sharing with advisory locks on
/// Unix; a test that silently failed to block would certify nothing.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","ReviewLogUnreadableState")].
/// </summary>
[Trait("Category", "ReviewLogUnreadableState")]
public class ReviewLogUnreadableStateTests : IDisposable
{
    private const string PlanMarkdown =
        "# A plan under team review\n" +
        "\n" +
        "The paragraph a teammate leaves a note against.\n";

    private static readonly ReviewAuthor Alice = new("Alice Ng", "alice@example.com");
    private static readonly ReviewAuthor Bob = new("Bob Chen", "bob@example.com");

    /// <summary>Far above any plausible short bound, so it can only fire on a retry that never settles.</summary>
    private const int RunawayWaitCeiling = 100;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-review-unreadable-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The recurrence itself. Before this fix the read returned PRESENT with zero comments — "nobody commented"
    /// over a log holding a comment. It must say it learned nothing, and it must still NAME the file: a
    /// permission error that never clears has to stay as visible as it was.
    /// </summary>
    [Fact]
    public void Every_log_unreadable_reads_as_unknown_and_still_names_them()
    {
        var plan = WritePlan();
        var writer = new ReviewLogWriter(plan, Alice);
        writer.AppendCreate(Anchor("the only note"), "a comment that exists, in a file that cannot be read right now");
        var directory = ReviewLogPaths.DirectoryForPlan(plan);

        var waits = 0;
        ReviewLogRead read;
        using (Lock(writer.LogPath))
        {
            AssertTheLockBlocksAReader(writer.LogPath);
            read = ReviewLogStore.Read(directory, waitBetweenAttempts: _ =>
            {
                if (++waits > RunawayWaitCeiling)
                {
                    throw new InvalidOperationException(
                        $"the read waited {waits} times against a lock that never clears: the retry is not bounded");
                }
            });
        }

        Assert.Equal(ReviewLogOutcome.Unknown, read.Outcome);
        Assert.True(read.IsUnknown);
        Assert.True(read.IsUnreadable, "logs that were there but unreadable must be told apart from a directory that was not");
        Assert.False(read.IsEmpty, "an unreadable log is not a finding that nobody commented");
        Assert.False(read.IsPresent, "zero comments from a read that read nothing is not the whole answer");
        Assert.Empty(read.State.Comments);

        var named = Assert.Single(read.Unreadable);
        Assert.Contains(Path.GetFileName(writer.LogPath), named, StringComparison.Ordinal);

        Assert.True(waits >= 1, "a look that could not read its logs must earn another look before settling");
    }

    /// <summary>
    /// What actually happens on a CI runner most of the time: the append finishes inside the budget. The comment
    /// must survive — which is the reason the incomplete look retries instead of settling on its first answer.
    /// </summary>
    [Fact]
    public void A_lock_that_clears_within_the_budget_reads_present()
    {
        var plan = WritePlan();
        var writer = new ReviewLogWriter(plan, Alice);
        var written = writer.AppendCreate(Anchor("the only note"), "arriving while the panel refreshes");
        var directory = ReviewLogPaths.DirectoryForPlan(plan);

        var waits = 0;
        var hold = Lock(writer.LogPath);
        ReviewLogRead read;
        try
        {
            AssertTheLockBlocksAReader(writer.LogPath);

            // The append "finishes" at the first wait: the first look genuinely cannot read the file, and the next
            // genuinely can. Only the timing belongs to the test.
            read = ReviewLogStore.Read(directory, waitBetweenAttempts: _ =>
            {
                waits++;
                hold.Dispose();
            });
        }
        finally
        {
            hold.Dispose();
        }

        Assert.True(waits >= 1, "the read must look again rather than settle on the look that hit the lock");
        Assert.Equal(ReviewLogOutcome.Present, read.Outcome);
        Assert.Equal(written.Id, Assert.Single(read.State.Comments).Id);
        Assert.Empty(read.Unreadable);
    }

    /// <summary>
    /// The CONTROL, and it passes before this fix as well as after — deliberately. A read that folded SOME logs
    /// has learned something real, so it keeps today's semantics: PRESENT, the readable comments, the unreadable
    /// log named beside them. This proves the fix did not over-reach by turning every conflict into Unknown.
    /// </summary>
    [Fact]
    public void A_partially_unreadable_read_keeps_its_fold_and_names_the_rest()
    {
        var plan = WritePlan();
        var alice = new ReviewLogWriter(plan, Alice);
        var readable = alice.AppendCreate(Anchor("alice's note"), "in a log nobody is writing to");
        var bob = new ReviewLogWriter(plan, Bob);
        bob.AppendCreate(Anchor("bob's note"), "in a log that is mid-append");
        var directory = ReviewLogPaths.DirectoryForPlan(plan);

        ReviewLogRead read;
        using (Lock(bob.LogPath))
        {
            AssertTheLockBlocksAReader(bob.LogPath);
            read = ReviewLogStore.Read(directory, waitBetweenAttempts: _ => { });
        }

        Assert.Equal(ReviewLogOutcome.Present, read.Outcome);
        Assert.Equal(readable.Id, Assert.Single(read.State.Comments).Id);
        Assert.Contains(Path.GetFileName(bob.LogPath), Assert.Single(read.Unreadable), StringComparison.Ordinal);
    }

    /// <summary>
    /// The discriminator for <see cref="ReviewLogRead.IsUnreadable"/>: an ABSENT directory is Unknown too, but it is
    /// NOT unreadable. <c>charter poll</c> treats the first as the ordinary state of a solo review (exit 3) and the
    /// second as a failed read (exit 4), so collapsing them regresses one or the other.
    /// </summary>
    [Fact]
    public void An_absent_directory_is_unknown_but_not_unreadable()
    {
        var plan = WritePlan();
        var directory = ReviewLogPaths.DirectoryForPlan(plan);
        Assert.False(Directory.Exists(directory), "nothing in this test ever creates the directory");

        var read = ReviewLogStore.Read(directory, waitBetweenAttempts: _ => { });

        Assert.True(read.IsUnknown);
        Assert.False(read.IsUnreadable);
        Assert.Empty(read.Unreadable);
    }
    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>Hold <paramref name="path"/> exactly as <see cref="ReviewLogWriter"/> does during an append.</summary>
    private static FileStream Lock(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    /// <summary>
    /// Prove the precondition every test here rests on, rather than trusting it: while the lock is held, a reader
    /// opening the file the way <see cref="ReviewLogStore"/> does must be refused.
    /// </summary>
    private static void AssertTheLockBlocksAReader(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return;
        }

        Assert.Fail(
            "FileShare.None did not stop a reader on this platform, so this test cannot reach the unreadable path it "
                + "exists to pin — it would certify nothing.");
    }

    private string WritePlan(string fileName = "team.charter.md")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, PlanMarkdown);
        return path;
    }

    private static ReviewAnchor Anchor(string quote) => new("b-a-block-in-the-plan", "element", quote, null);
}
