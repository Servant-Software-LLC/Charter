using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// The <c>charter-format-version</c> marker's validation lint and stamp helper (Architecture B §2.4, Charter
/// #24). <see cref="CharterFormat.ValidateVersionMarker(string)"/> reads the plain-YAML frontmatter marker and
/// judges it Missing / Unsupported / Ok WITHOUT loading any skill; <see cref="CharterFormat.EnsureVersionMarker(string)"/>
/// stamps the marker surgically (prepending a fresh block or adding the key to an existing one) while preserving
/// every other byte. The version constants are single-sourced from <see cref="CharterFormat.Version"/> /
/// <see cref="CharterFormat.MinVersion"/> — the same numbers the drift test binds the skill frontmatter to.
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","CharterFormat")].
/// </summary>
[Trait("Category", "CharterFormat")]
public class CharterFormatTests
{
    // Body authored WITHOUT a marker — carries a following ::: block and prose the stamp helper must preserve.
    private const string Body =
        "# Living plan\n\nSome prose introducing the plan.\n\n:::note\nAn aside.\n:::\n";

    // ---- ValidateVersionMarker -----------------------------------------------------------------------------

    [Fact]
    public void Validate_AbsentMarker_IsMissing()
    {
        var result = CharterFormat.ValidateVersionMarker(Body);

        Assert.Equal(VersionMarkerStatus.Missing, result.Status);
        Assert.Null(result.Version);
        Assert.Contains(CharterFormat.MarkerKey, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_CurrentVersion_IsOk()
    {
        var markdown = $"---\n{CharterFormat.MarkerKey}: {CharterFormat.Version}\n---\n" + Body;

        var result = CharterFormat.ValidateVersionMarker(markdown);

        Assert.Equal(VersionMarkerStatus.Ok, result.Status);
        Assert.Equal(CharterFormat.Version, result.Version);
    }

    [Fact]
    public void Validate_VersionAboveRange_IsUnsupported()
    {
        var markdown = $"---\n{CharterFormat.MarkerKey}: 99\n---\n" + Body;

        var result = CharterFormat.ValidateVersionMarker(markdown);

        Assert.Equal(VersionMarkerStatus.Unsupported, result.Status);
        Assert.Equal(99, result.Version);
    }

    [Fact]
    public void Validate_NonIntegerMarker_IsUnsupported_WithNoParsedVersion()
    {
        var markdown = $"---\n{CharterFormat.MarkerKey}: not-a-number\n---\n" + Body;

        var result = CharterFormat.ValidateVersionMarker(markdown);

        Assert.Equal(VersionMarkerStatus.Unsupported, result.Status);
        Assert.Null(result.Version);
    }

    // ---- EnsureVersionMarker -------------------------------------------------------------------------------

    [Fact]
    public void Ensure_Absent_AddsAMarkerBlock_AndPreservesTheFollowingBlockAndProse()
    {
        var stamped = CharterFormat.EnsureVersionMarker(Body);

        // A fresh frontmatter block is prepended carrying exactly the marker...
        Assert.StartsWith($"---\n{CharterFormat.MarkerKey}: {CharterFormat.Version}\n---\n", stamped, StringComparison.Ordinal);
        // ...the stamp now validates Ok...
        Assert.Equal(VersionMarkerStatus.Ok, CharterFormat.ValidateVersionMarker(stamped).Status);
        // ...and every byte of the original body survives (the following ::: block and the prose).
        Assert.Contains(Body, stamped, StringComparison.Ordinal);
        Assert.Contains(":::note", stamped, StringComparison.Ordinal);
        Assert.Contains("Some prose introducing the plan.", stamped, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_Present_IsANoOp()
    {
        var already = $"---\n{CharterFormat.MarkerKey}: {CharterFormat.Version}\n---\n" + Body;

        Assert.Equal(already, CharterFormat.EnsureVersionMarker(already));
    }

    [Fact]
    public void Ensure_ExistingFrontMatterWithoutMarker_AddsTheKeyAndKeepsOtherKeys()
    {
        var existing = "---\ntitle: My Plan\n---\n" + Body;

        var stamped = CharterFormat.EnsureVersionMarker(existing);

        // The other frontmatter key is untouched and the marker key was added inside the same block.
        Assert.Contains("title: My Plan", stamped, StringComparison.Ordinal);
        Assert.Contains($"{CharterFormat.MarkerKey}: {CharterFormat.Version}", stamped, StringComparison.Ordinal);
        Assert.Equal(VersionMarkerStatus.Ok, CharterFormat.ValidateVersionMarker(stamped).Status);
        Assert.Contains(":::note", stamped, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_IsIdempotent()
    {
        var once = CharterFormat.EnsureVersionMarker(Body);
        var twice = CharterFormat.EnsureVersionMarker(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Ensure_StampedMarker_IsStrippedByThePipeline_RendersIdenticallyToTheBareBody()
    {
        // The stamp helper writes a frontmatter block the Markdig pipeline strips, so the rendered content is
        // byte-identical to the bare body and the marker never leaks into the artifact — proof that the reader,
        // the writer, and the pipeline's stripping all agree on the marker's shape.
        var stamped = CharterFormat.EnsureVersionMarker(Body);

        Assert.Equal(CharterRenderer.Render(Body), CharterRenderer.Render(stamped));
        Assert.DoesNotContain(CharterFormat.MarkerKey, CharterRenderer.Render(stamped), StringComparison.Ordinal);
    }
}
