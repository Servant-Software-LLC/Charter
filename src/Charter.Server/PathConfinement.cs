namespace Charter.Server;

/// <summary>
/// The load-bearing confinement primitive: resolve a request path against a served root and return the
/// full on-disk path ONLY when it stays inside that root, rejecting <c>..</c> traversal and absolute-path
/// escapes. This is transport-independent, so it can be proven directly by a unit test rather than only
/// over HTTP.
/// </summary>
public static class PathConfinement
{
    /// <summary>
    /// Resolve <paramref name="requestPath"/> under <paramref name="root"/>. Returns the full path when it
    /// stays inside <paramref name="root"/>; returns <c>null</c> when the path escapes via <c>..</c>
    /// traversal, is an absolute path outside the root, or traverses a symlink/junction whose real target
    /// escapes the root.
    /// </summary>
    /// <remarks>
    /// Both the root and the combined candidate are canonicalized with <see cref="Path.GetFullPath(string)"/>
    /// so <c>..</c> segments collapse before the containment check. <see cref="Path.Combine(string, string)"/>
    /// returns an absolute <paramref name="requestPath"/> verbatim, so an absolute escape canonicalizes to a
    /// path outside the root and is rejected the same way a relative traversal is. Because
    /// <see cref="Path.GetFullPath(string)"/> is LEXICAL and never follows reparse points, a symlink/junction
    /// textually inside the root but resolving OUTSIDE it would pass the string check; so after the lexical
    /// check the final target is resolved (<see cref="EscapesViaReparsePoint"/>) and containment re-run. This
    /// mirrors the same fix in <c>Charter.Core.ArtifactExporter.ResolveConfined</c> — reimplemented locally so
    /// each assembly stays self-contained.
    /// </remarks>
    public static string? Resolve(string root, string requestPath)
    {
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        requestPath ??= string.Empty;

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, requestPath));

        var normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Lexical containment: accept only when the resolved path EQUALS the root or starts with the root plus
        // a directory separator — never a bare string-prefix match (a sibling `plan-evil` vs `plan`).
        if (!IsContained(fullPath, normalizedRoot))
        {
            return null;
        }

        // Physical containment: a reparse point below the root whose final target escapes the root is refused.
        return EscapesViaReparsePoint(fullPath, normalizedRoot) ? null : fullPath;
    }

    /// <summary>
    /// True when <paramref name="path"/> is the root itself or lies beneath it — separator-safe, so a sibling
    /// that merely shares the root as a raw string prefix (<c>plan-evil</c> vs <c>plan</c>) is NOT contained.
    /// </summary>
    private static bool IsContained(string path, string normalizedRoot)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return path.Equals(normalizedRoot, comparison)
            || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Walk every path component strictly BELOW <paramref name="normalizedRoot"/> and return true when one is a
    /// reparse point (symlink or junction) whose resolved final target ESCAPES the root. Only components inside
    /// the root are examined: the root and its ancestors are trusted and never resolved (on macOS the temp
    /// root's ancestors — e.g. <c>/tmp</c> — are themselves symlinks, and the root was canonicalized lexically
    /// to match). A missing or inaccessible component is not an escape here; the caller's existence checks
    /// handle absence.
    /// </summary>
    private static bool EscapesViaReparsePoint(string full, string normalizedRoot)
    {
        var relative = Path.GetRelativePath(normalizedRoot, full);
        if (relative.Length == 0 || relative == "." || Path.IsPathRooted(relative)
            || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var current = normalizedRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, segment);

            var attributes = SafeGetAttributes(current);
            if (attributes is not { } attrs || (attrs & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            var target = ResolveFinalTarget(current, (attrs & FileAttributes.Directory) != 0);
            if (target is null || !IsContained(target, normalizedRoot))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The attributes of the entry AT <paramref name="path"/> (a link's OWN attributes, reparse flag included —
    /// <see cref="File.GetAttributes(string)"/> does not follow the link), or <c>null</c> when the path is
    /// missing or inaccessible.
    /// </summary>
    private static FileAttributes? SafeGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The canonical final target of a reparse-point <paramref name="path"/> (following the whole link chain),
    /// as an absolute path, or <c>null</c> when it cannot be resolved.
    /// </summary>
    private static string? ResolveFinalTarget(string path, bool isDirectory)
    {
        try
        {
            var target = isDirectory
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.ResolveLinkTarget(path, returnFinalTarget: true);

            return target is null ? null : Path.GetFullPath(target.FullName);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
