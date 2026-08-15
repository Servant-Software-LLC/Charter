using System.IO;
using Xunit;

namespace Charter.Cli.Tests;

/// <summary>
/// Charter #144 — <c>charter poll</c> is the agent's IPC, and a human who runs it anyway must be told what
/// just happened to their review round.
///
/// <para>
/// The review page used to hand a human the raw command line under the label "Run this where your agent is".
/// Run in a terminal it worked exactly as designed, and the experience was bad: a wall of single-line JSON
/// envelopes, one every ~30 seconds forever, and the round CONSUMED — two annotations drained out of the
/// queue into a console nobody was reading, with nothing saying so. The page now hands over
/// <c>/charter-drain</c>, but the verb stays runnable, so it must stop being silent.
/// </para>
/// <para>
/// The terminal detection itself (<c>Console.IsOutputRedirected</c>) is deliberately NOT exercised here: a
/// test host captures stdout by construction, so that read is always true under xunit and the interactive
/// branch is unreachable from a test. The decision is therefore passed in, and everything downstream of it —
/// the message, the once-only guarantee, and the silence when a program is reading — is pinned.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","PollHumanNotice")].
/// </summary>
[Trait("Category", "PollHumanNotice")]
public class PollHumanNoticeTests
{
    [Fact]
    public void ADrainIsSilentWhenAProgramIsReadingTheEnvelopes()
    {
        PollCommand.ResetHumanNoticeForTests();
        var writer = new StringWriter();

        PollCommand.WriteHumanNotice(humanIsWatching: false, apply: true, writer);

        // An agent's stderr must not gain prose it never had — and stdout is untouched either way.
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void AHumanIsToldTheRoundIsBeingConsumed_AndPointedAtTheSkill()
    {
        PollCommand.ResetHumanNoticeForTests();
        var writer = new StringWriter();

        PollCommand.WriteHumanNotice(humanIsWatching: true, apply: false, writer);
        var notice = writer.ToString();

        // The fact that actually cost something: the notes are gone from the queue and this console is the
        // only copy. Saying "this is for agents" without that would be a style note, not a warning.
        Assert.Contains("DRAINS the review round", notice, StringComparison.Ordinal);
        Assert.Contains("only copy", notice, StringComparison.Ordinal);
        // ...and the way out, named, so the notice is actionable rather than merely alarming.
        Assert.Contains("/charter-drain", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Under <c>--apply</c> the question answers are written inline to the plan, so they are NOT lost. Saying
    /// so keeps the warning honest and proportionate — overstating it would train the reader to ignore it.
    /// </summary>
    [Fact]
    public void ApplyIsCalledOutAsTheHalfThatSurvives()
    {
        PollCommand.ResetHumanNoticeForTests();
        var applied = new StringWriter();
        PollCommand.WriteHumanNotice(humanIsWatching: true, apply: true, applied);

        PollCommand.ResetHumanNoticeForTests();
        var bare = new StringWriter();
        PollCommand.WriteHumanNotice(humanIsWatching: true, apply: false, bare);

        Assert.Contains("answers ARE applied inline", applied.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("answers ARE applied inline", bare.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--watch</c> loops <c>ExecuteAsync</c> once per cycle, indefinitely. A notice per cycle would be the
    /// same wall of noise this exists to apologise for.
    /// </summary>
    [Fact]
    public void TheNoticeAppearsOncePerProcess_NotOncePerWatchCycle()
    {
        PollCommand.ResetHumanNoticeForTests();
        var writer = new StringWriter();

        PollCommand.WriteHumanNotice(humanIsWatching: true, apply: true, writer);
        var afterFirst = writer.ToString();
        PollCommand.WriteHumanNotice(humanIsWatching: true, apply: true, writer);
        PollCommand.WriteHumanNotice(humanIsWatching: true, apply: true, writer);

        Assert.NotEqual(string.Empty, afterFirst);
        Assert.Equal(afterFirst, writer.ToString());
    }
}
