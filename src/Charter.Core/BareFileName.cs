namespace Charter.Core;

/// <summary>
/// The ONE rule behind Charter's no-local-path guarantee: a machine-readable artifact names the files it is
/// about by their BARE file names, never by a path, so it is as safe to collect and hand on as the exported
/// artifact beside it.
/// </summary>
/// <remarks>
/// <para>
/// It was <see cref="HeadlessRecord"/>'s private <c>RequireBareFileName</c> until <see cref="HandoffManifest"/>
/// needed exactly the same guarantee for three more names (Charter #187). Two copies of a security-shaped rule
/// is one copy too many: the interesting half is the Windows/Unix asymmetry below, and a second implementation
/// is a second chance to get that wrong in only one of them.
/// </para>
/// </remarks>
public static class BareFileName
{
    /// <summary>The explanation appended to every rejection, so the caller learns the rule and not just the fact.</summary>
    public const string NoPathsAllowed =
        "must be a bare file name, not a path — a Charter machine artifact deliberately carries no local path.";

    /// <summary>
    /// Throw unless <paramref name="value"/> is a bare file name.
    /// </summary>
    /// <remarks>
    /// Checked against BOTH separators explicitly rather than via <c>Path.GetFileName</c>, which is
    /// platform-dependent: on Unix a backslash is an ordinary filename character, so a Windows path would sail
    /// through unnoticed there. Accepting a path and silently trimming it would make the guarantee depend on
    /// every caller remembering to trim.
    /// </remarks>
    public static void Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);

        if (value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value is "." or "..")
        {
            throw new ArgumentException($"'{parameterName}' {NoPathsAllowed}", parameterName);
        }
    }
}
