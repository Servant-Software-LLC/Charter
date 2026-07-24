using System.Reflection;

namespace Charter.Cli;

/// <summary>
/// The single source of truth for the tool's version string. Reads the executing assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/>, normalised by stripping any <c>+build</c>
/// metadata (everything from the first <c>'+'</c>), falling back to the assembly version when the
/// attribute is absent. This is the value <c>charter skills install</c> stamps into each installed
/// <c>SKILL.md</c> frontmatter (<c>metadata.charter-version</c>) so a later staleness check can tell an
/// installed skill apart from the running tool. Mirrors Guardrails' <c>GuardrailsVersion</c>.
/// </summary>
internal static class CharterVersion
{
    /// <summary>The normalised tool version (no <c>+build</c> metadata).</summary>
    public static string Current { get; } = Resolve(typeof(CharterVersion).Assembly);

    /// <summary>
    /// The version string for <paramref name="assembly"/>: its informational version with any
    /// <c>+build</c> metadata stripped, or its assembly version if that attribute is absent.
    /// </summary>
    public static string Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return Normalize(informational);
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>Strip <c>+build</c> metadata (everything from the first <c>'+'</c>) and surrounding whitespace.</summary>
    private static string Normalize(string version)
    {
        string trimmed = version.Trim();
        int plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }
}
