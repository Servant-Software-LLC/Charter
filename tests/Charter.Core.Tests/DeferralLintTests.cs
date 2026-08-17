using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// Charter #156 — a deferral that names nothing has no owner and no expiry.
///
/// <para>
/// A reviewer asked, of a scope note reading <i>"probes, the ladder and steering stay v2 and are explicitly
/// out of scope"</i>, whether anything actually tracked those. The artifact could not tell them: on the page
/// <i>"deferred to #228"</i> and <i>"deferred into the void"</i> look identical, so the absence of tracking
/// is a silence a reviewer has to think to interrogate.
/// </para>
/// <para>
/// Charter's half is the CONVENTION and the PROMPT only — that something is named. Whether that issue
/// exists, is open, or is about the right thing is a network lookup against one vendor's tracker, and it
/// stays with the agent (maintainer ruling on the issue: the binary stays dependency-free). These tests pin
/// the textual half, including the calibration decisions, which were made against real charters rather than
/// invented.
/// </para>
/// </summary>
public class DeferralLintTests
{
    [Fact]
    public void Finds_a_deferral_that_names_nothing()
    {
        const string markdown =
            "# Stage 1\n\n## Scope\n\n- **Out:** probes, the ladder and steering stay v2 and are out of scope.\n";

        var found = Assert.Single(DeferralLint.Find(markdown));

        Assert.Equal(5, found.SourceLine);
        Assert.Contains("out of scope", found.Excerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Silent_when_the_deferral_names_its_issue()
    {
        const string markdown =
            "# Stage 1\n\n## Scope\n\n- **Out:** the resolver and anything that reads a tier (Stage 2, #226).\n";

        Assert.Empty(DeferralLint.Find(markdown));
    }

    /// <summary>
    /// Vendor-neutral by construction. The convention is that SOMETHING is named — a Jira browse URL tracks a
    /// deferral exactly as well as a GitHub number, and a lint that only understood one would quietly push
    /// every other project toward the tracker Charter happened to know about.
    /// </summary>
    [Theory]
    [InlineData("Tracked at https://example.atlassian.net/browse/PROJ-42.")]
    [InlineData("Tracked at https://gitlab.com/acme/app/-/issues/17.")]
    [InlineData("Tracked as #9.")]
    public void Any_named_tracker_counts_not_just_a_github_number(string tracking)
    {
        var markdown = "# S\n\n## Scope\n\nThe ladder is deferred to v2. " + tracking + "\n";

        Assert.Empty(DeferralLint.Find(markdown));
    }

    /// <summary>
    /// CALIBRATION, from running this over three real charters: the tracking reference routinely sits in a
    /// neighbouring paragraph of the same section, not in the sentence that defers. Judging per paragraph
    /// called that untracked; a human reading the section plainly sees it. So tracked-ness is judged per
    /// SECTION while the report still points at the deferring paragraph.
    /// </summary>
    [Fact]
    public void Tracking_counts_when_it_sits_elsewhere_in_the_same_section()
    {
        const string markdown =
            "# Stage 1\n\n## Failing early\n\nJIT judge resolution is deferred to #223.\n\n"
            + "1. The hook lands in this stage.\n"
            + "2. A judge resolved just-in-time is explicitly out of scope here.\n";

        Assert.Empty(DeferralLint.Find(markdown));
    }

    /// <summary>
    /// ...and the section boundary is real: tracking in a DIFFERENT section does not cover this one, or the
    /// lint would fall silent on any document that mentioned an issue anywhere.
    /// </summary>
    [Fact]
    public void Tracking_in_another_section_does_not_cover_this_one()
    {
        const string markdown =
            "# Plan\n\n## Related\n\nSee #226 for the resolver.\n\n"
            + "## Scope\n\nProbes and steering are out of scope.\n";

        var found = Assert.Single(DeferralLint.Find(markdown));
        Assert.Contains("Probes and steering", found.Excerpt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A section LABEL is not a statement that defers anything. <c>## Scope / non-goals</c> matches the
    /// deferral vocabulary by name alone, and warning about a heading would spend the reader's attention on
    /// nothing — which is how a lint teaches people to skip it.
    /// </summary>
    [Fact]
    public void A_heading_is_never_itself_a_deferral()
    {
        const string markdown = "# Plan\n\n## Scope / non-goals\n\nOrdinary prose with nothing deferred.\n";

        Assert.Empty(DeferralLint.Find(markdown));
    }

    /// <summary>
    /// The bug real data caught and reasoning did not: a wrapped line beginning <c>#223, where a judge…</c>
    /// is an ISSUE REFERENCE, not a heading. Reading it as one split the section in two and hid the very
    /// reference that proved the deferral was tracked — so the lint reported a false positive on a document
    /// that was doing exactly the right thing.
    /// </summary>
    [Fact]
    public void A_line_starting_with_an_issue_number_is_not_a_heading()
    {
        const string markdown =
            "# Plan\n\n## Failing early\n\nJIT resolution is out of scope here, and is deferred to\n"
            + "#223, where a judge can actually resolve to a non-Claude model.\n";

        Assert.Empty(DeferralLint.Find(markdown));
    }

    [Fact]
    public void Empty_input_is_not_an_error()
    {
        Assert.Empty(DeferralLint.Find(string.Empty));
        Assert.Empty(DeferralLint.Find(null!));
    }
}
