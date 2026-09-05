using System;
using System.IO;
using System.Threading.Tasks;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #221 — the review log must be able to say <i>"I could not tell"</i> instead of
/// <i>"nobody commented"</i>.
///
/// <para>
/// The defect these pin: <c>ReviewLogStore.Read</c> answers <c>ReviewLogRead.Empty</c> whenever
/// <c>EnumerateLogs</c> finds nothing, and <c>EnumerateLogs</c> answers an empty array when the directory does
/// not exist. So a momentarily-absent <c>.review/</c> and a plan nobody has commented on produce the IDENTICAL
/// value — empty comments, empty <c>Unreadable</c>, no error — and every consumer downstream takes the
/// stronger reading. The panel empties and focus lands on <c>&lt;body&gt;</c>; <c>charter poll</c> reports a
/// clean, confident exit 2 and an agent is told the reviewer said nothing; <c>charter reply --to</c> refuses
/// against a comment that exists.
/// </para>
///
/// <para>
/// The shape of the fix is <see cref="ProbeResult"/>'s, which solved this same class of bug in this project
/// for #217: three outcomes, and a property per branch so no caller ever reads a branch as the negation of
/// another.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","ReviewLogUnknownState")].
/// </summary>
[Trait("Category", "ReviewLogUnknownState")]
public class ReviewLogUnknownStateTests : IDisposable
{
    private const string PlanMarkdown =
        "# A plan under team review\n" +
        "\n" +
        "The paragraph a teammate leaves a note against.\n";

    private static readonly ReviewAuthor Alice = new("Alice Ng", "alice@example.com");
    private static readonly ReviewAuthor Bob = new("Bob Chen", "bob@example.com");

    /// <summary>
    /// How many waits a retry against a permanently missing directory may take before this suite calls it
    /// unbounded. Far above any plausible SHORT bound, so it can only fire on a retry that never settles.
    /// </summary>
    private const int RunawayWaitCeiling = 100;

    /// <summary>
    /// How long a read against a permanently missing directory has to RETURN. Generous, because it is not
    /// measuring the bound — it exists so an unbounded retry fails this test instead of hanging the suite.
    /// </summary>
    private static readonly TimeSpan ReturnDeadline = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-review-unknown-" + Guid.NewGuid().ToString("N"));

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
    /// The common case, asserted first so the third state cannot be bought by degrading it: logs on disk read
    /// as PRESENT, and "present" is a claim about the read rather than a flag beside it — the folded comments
    /// still have to be the whole answer.
    /// </summary>
    [Fact]
    public void A_directory_with_logs_reads_as_present()
    {
        var plan = WritePlan();
        var written = new ReviewLogWriter(plan, Alice).AppendCreate(
            Anchor("the write path"), "The write path needs a retry budget.");

        var read = ReviewLogStore.Read(ReviewLogPaths.DirectoryForPlan(plan));

        Assert.Equal(ReviewLogOutcome.Present, read.Outcome);
        Assert.True(read.IsPresent);

        var folded = Assert.Single(read.State.Comments);
        Assert.Equal(written.Id, folded.Id);
        Assert.Equal("The write path needs a retry budget.", folded.Body);
        Assert.Equal(Alice.Email, folded.Author.Email);
        Assert.Empty(read.Unreadable);
    }

    /// <summary>
    /// The discriminator that stops the fix from answering Unknown to everything. A <c>.review/</c> that EXISTS
    /// and holds no logs is a plan nobody has commented on — a normal state, reachable by a checkout that
    /// removed the logs but left the directory, and it must stay a silent, cheap Empty.
    /// </summary>
    [Fact]
    public void An_existing_directory_with_no_logs_reads_as_empty()
    {
        var plan = WritePlan();
        var directory = ReviewLogPaths.DirectoryForPlan(plan);
        Directory.CreateDirectory(directory);

        var read = ReviewLogStore.Read(directory);

        Assert.Equal(ReviewLogOutcome.Empty, read.Outcome);
        Assert.True(read.IsEmpty);
        Assert.False(read.IsUnknown);

        // Empty is not an error and carries no complaint: nothing to report, nothing unreadable.
        Assert.Empty(read.State.Comments);
        Assert.Empty(read.State.Diagnostics);
        Assert.Empty(read.Unreadable);
    }

    /// <summary>
    /// The defect itself. A directory that is not there was never looked into, so the read learned nothing —
    /// and answering Empty is an assertion about what a teammate did or did not say, made without evidence.
    /// </summary>
    [Fact]
    public void A_missing_directory_reads_as_unknown_not_empty()
    {
        var plan = WritePlan();
        var directory = ReviewLogPaths.DirectoryForPlan(plan);
        Assert.False(Directory.Exists(directory), "the case under test is a directory that is NOT there");

        var read = ReviewLogStore.Read(directory);

        Assert.Equal(ReviewLogOutcome.Unknown, read.Outcome);
        Assert.True(read.IsUnknown);
        Assert.False(read.IsEmpty);
    }

    /// <summary>
    /// The two no-comment outcomes reaching a caller as distinguishable values — which is the entire point,
    /// because today they are the same value and the distinction is destroyed at the read.
    /// </summary>
    [Fact]
    public void Unknown_and_empty_are_distinguishable_by_a_caller()
    {
        var neverCommented = WritePlan("never-commented.charter.md");
        var unknownDirectory = ReviewLogPaths.DirectoryForPlan(neverCommented);

        var emptied = WritePlan("emptied.charter.md");
        var emptyDirectory = ReviewLogPaths.DirectoryForPlan(emptied);
        Directory.CreateDirectory(emptyDirectory);

        var unknown = ReviewLogStore.Read(unknownDirectory);
        var empty = ReviewLogStore.Read(emptyDirectory);

        // Everything a consumer used to have to go on is identical across the two — this is the conflation,
        // stated as an assertion so the fix cannot be mistaken for "the comments differ now".
        Assert.Empty(unknown.State.Comments);
        Assert.Empty(empty.State.Comments);
        Assert.Empty(unknown.Unreadable);
        Assert.Empty(empty.Unreadable);

        Assert.NotEqual(empty.Outcome, unknown.Outcome);

        // Each branch has a POSITIVE spelling, so a consumer never has to negate another to reach it.
        Assert.True(empty.IsEmpty);
        Assert.False(empty.IsUnknown);
        Assert.True(unknown.IsUnknown);
        Assert.False(unknown.IsEmpty);

        // ...and that matters because the negation CANNOT tell them apart: `!IsPresent` holds for both. It is
        // the spelling ProbeResult.IsAbsent was introduced to make unnecessary (#217), one layer down.
        Assert.False(empty.IsPresent);
        Assert.False(unknown.IsPresent);
    }

    /// <summary>
    /// The retry half of the decision: a directory that is absent for a moment and then there must read as the
    /// real answer, not as Unknown. That is the case the retry exists for — the logs arrive by git, so a pull
    /// or a merge can be replacing <c>.review/</c> at the exact moment the panel refreshes.
    /// </summary>
    /// <remarks>
    /// The log lands DURING the read, in the gap the retry already waits in. Injecting the wait rather than
    /// racing a background thread against the bound is what makes this deterministic on every runner: the
    /// directory is genuinely absent on the first attempt and genuinely there on the next, every byte read is
    /// the writer's own, and only the timing belongs to the test.
    /// </remarks>
    [Fact]
    public void A_transient_failure_inside_the_retry_bound_still_reads_present()
    {
        var plan = WritePlan();
        var directory = ReviewLogPaths.DirectoryForPlan(plan);
        Assert.False(Directory.Exists(directory), "the read must begin against an absent directory");

        var waits = 0;
        ReviewRecord? arrived = null;

        var read = ReviewLogStore.Read(directory, waitBetweenAttempts: _ =>
        {
            waits++;
            arrived ??= new ReviewLogWriter(plan, Bob).AppendCreate(
                Anchor("the read path"), "arriving mid-read, exactly as a pull would deliver it");
        });

        Assert.True(waits >= 1, "the read must re-consult the directory rather than settle on its first look");

        Assert.Equal(ReviewLogOutcome.Present, read.Outcome);
        Assert.NotNull(arrived);
        Assert.Equal(arrived!.Id, Assert.Single(read.State.Comments).Id);
    }

    /// <summary>
    /// The other half of the same decision, and the reason the bound is part of the deliverable: a directory
    /// that never appears has to SETTLE. An unbounded retry hangs the panel instead of emptying it, which is
    /// worse than the bug being fixed — so this asserts the read returns, and fails rather than hanging when
    /// it does not.
    /// </summary>
    [Fact]
    public async Task A_permanently_missing_directory_settles_as_unknown_within_the_bound()
    {
        var plan = WritePlan();
        var directory = ReviewLogPaths.DirectoryForPlan(plan);
        Assert.False(Directory.Exists(directory), "nothing in this test ever creates the directory");

        var waits = 0;

        // The wait does not sleep, so the loop runs at full speed and the ceiling names an unbounded retry
        // instead of leaving a thread spinning for the rest of the run.
        var call = Task.Run(() => ReviewLogStore.Read(directory, waitBetweenAttempts: _ =>
        {
            if (++waits > RunawayWaitCeiling)
            {
                throw new InvalidOperationException(
                    $"the read waited {waits} times against a permanently missing directory: the retry is not bounded");
            }
        }));

        var finished = await Task.WhenAny(call, Task.Delay(ReturnDeadline));
        Assert.True(
            ReferenceEquals(finished, call),
            $"the read did not RETURN within {ReturnDeadline.TotalSeconds:0}s against a permanently missing "
                + "directory — an unbounded retry hangs the panel rather than emptying it");

        var read = await call;
        Assert.Equal(ReviewLogOutcome.Unknown, read.Outcome);
        Assert.True(read.IsUnknown);
        Assert.False(read.IsEmpty);

        Assert.True(
            waits <= RunawayWaitCeiling,
            $"the retry must be SHORT as well as finite; it waited {waits} times");
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private string WritePlan(string fileName = "team.charter.md")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, PlanMarkdown);
        return path;
    }

    /// <summary>
    /// An anchor carrying only its quote. Nothing here resolves an anchor against the plan — the read folds
    /// logs, and the projection that resolves anchors is a separate consumer.
    /// </summary>
    private static ReviewAnchor Anchor(string quote) => new("b-a-block-in-the-plan", "element", quote, null);
}
