using System.Text;
using System.Text.RegularExpressions;

namespace Charter.Core;

/// <summary>
/// Exports Charter markdown to a TRULY offline, self-contained HTML artifact: it renders via
/// <see cref="CharterRenderer.Render(string)"/> and then (a) inlines every LOCAL image/media asset the plan
/// references (resolved under <paramref name="planDirectory"/>) as a <c>data:</c> URI, and (b) scrubs any
/// remaining local filesystem path from the shipped file. Unlike the review server, <c>export</c> NEVER
/// emits the serve-time annotation SDK (the <c>data-charter-sdk</c> marker is added only by
/// <c>Charter.Server.SdkInjector</c> at serve time) — the exported file is portable and SDK-free.
/// </summary>
/// <remarks>
/// The transform is deliberately a plain text post-process over the rendered HTML: it introduces no new JS,
/// makes no network call, and takes no dependency on <c>Charter.Server</c> (invariants 1 and 6). Asset reads
/// are confined to <paramref name="planDirectory"/> with the same separator-safe containment check as
/// <c>Charter.Server.PathConfinement.Resolve</c>, reimplemented locally so <c>Charter.Core</c> stays pure.
/// </remarks>
public static partial class ArtifactExporter
{
    /// <summary>
    /// Render and export <paramref name="markdown"/> to a self-contained HTML artifact, inlining local
    /// assets found under <paramref name="planDirectory"/>.
    /// </summary>
    /// <param name="markdown">The Charter plan markdown to render and export.</param>
    /// <param name="planDirectory">
    /// The plan's directory — the confinement root for resolving and reading local asset references. Asset
    /// reads never escape this directory.
    /// </param>
    /// <param name="maxAssetBytes">
    /// The per-asset inlining size cap (default 10 MiB, 10,485,760 bytes). A single asset larger than this is
    /// omitted rather than inlined.
    /// </param>
    /// <param name="maxTotalAssetBytes">
    /// The cumulative cap across every asset inlined into one export (default 50 MiB, 52,428,800 bytes).
    /// Assets are considered in document order; once the running total of already-inlined bytes would exceed
    /// this, every subsequent local asset is omitted even if individually under <paramref name="maxAssetBytes"/>.
    /// </param>
    public static string Export(
        string markdown,
        string planDirectory,
        long maxAssetBytes = 10_485_760,
        long maxTotalAssetBytes = 52_428_800)
    {
        // Step 1: render exactly as render/review do (including the already-inlined vendored Mermaid runtime).
        var html = CharterRenderer.Render(markdown);

        // The confinement root, canonicalized and trailing-separator-trimmed so the containment check below is
        // separator-safe (a sibling that merely shares the root as a raw string prefix is NOT accepted).
        var normalizedRoot = Path.GetFullPath(string.IsNullOrEmpty(planDirectory) ? "." : planDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Step 5: one running total of inlined bytes across the WHOLE call, spanning every non-script segment.
        var runningTotal = 0L;

        // Steps 3-7: decide the fate of one src="..." attribute value — inline, omit, or (remote) leave alone.
        string InlineOrOmit(Match match)
        {
            var reference = match.Groups["value"].Value;

            // Step 9: remote (and already-inlined data:) references are out of scope — leave them verbatim.
            if (IsRemoteOrData(reference))
            {
                return match.Value;
            }

            var (bytes, reason) = TryLoadAsset(
                reference, normalizedRoot, maxAssetBytes, maxTotalAssetBytes, runningTotal);

            if (bytes is not null)
            {
                // Step 6: inline as a data: URI, and count the bytes against the cumulative cap.
                runningTotal += bytes.Length;
                return $"src=\"data:{MimeFor(reference)};base64,{Convert.ToBase64String(bytes)}\"";
            }

            // Step 7: omit — rewrite to about:blank and record the reason + basename only; NEVER the path.
            return "src=\"about:blank\"" +
                   $" data-charter-export-omitted=\"{reason}\"" +
                   $" data-charter-export-filename=\"{EncodeAttribute(BaseName(reference))}\"";
        }

        // Steps 3-8 over a single NON-script region: inline/omit src= values first (steps 3-7), then redact
        // any surviving non-src file:// reference (step 8). Script regions are handled by the caller.
        string TransformNonScript(string segment)
        {
            var inlined = SrcAttributeRegex().Replace(segment, InlineOrOmit);
            var fileScrubbed = FileUriRegex().Replace(inlined, "file:///[redacted]");

            // Final pass: redact any UNAMBIGUOUS local filesystem path still riding a NON-src carrier — an
            // href, a CSS url(...), an srcset, an xlink:href — that the src-inlining and file:// passes above
            // never touch. Only drive-letter and UNC paths are redacted; bare POSIX /… paths (legit
            // root-relative URLs) are deliberately left alone. Script regions were already set aside by the
            // caller, so the vendored Mermaid runtime is untouched.
            return LocalPathRegex().Replace(fileScrubbed, "file:///[redacted]");
        }

        // Step 2: split SCRIPT and non-SCRIPT regions BEFORE scanning. The vendored Mermaid runtime rides
        // inside <script>…</script> and contains JS text that LOOKS like an <img src> tag; touching it would
        // corrupt the runtime. Set every script region ASIDE untouched, transform only the text around them,
        // then reassemble (step 10).
        var builder = new StringBuilder(html.Length);
        var cursor = 0;
        foreach (Match script in ScriptRegionRegex().Matches(html))
        {
            builder.Append(TransformNonScript(html.Substring(cursor, script.Index - cursor)));
            builder.Append(script.Value); // untouched — reassembled into its original position
            cursor = script.Index + script.Length;
        }

        builder.Append(TransformNonScript(html[cursor..]));
        return builder.ToString();
    }

    /// <summary>
    /// Resolve, confine, size-check and read one local asset. Returns its bytes on success, otherwise the
    /// omission reason (<c>not-found</c>, <c>too-large</c>, <c>total-cap-exceeded</c>, or <c>unreadable</c>).
    /// A path that escapes confinement is treated exactly like a missing file (<c>not-found</c>), so an
    /// escape is indistinguishable from a genuine absence to the artifact's consumer.
    /// </summary>
    private static (byte[]? Bytes, string? Reason) TryLoadAsset(
        string reference, string normalizedRoot, long maxAssetBytes, long maxTotalAssetBytes, long runningTotal)
    {
        var resolved = ResolveConfined(reference, normalizedRoot);
        if (resolved is null || !File.Exists(resolved))
        {
            return (null, "not-found");
        }

        long length;
        try
        {
            length = new FileInfo(resolved).Length;
        }
        catch (IOException)
        {
            return (null, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "unreadable");
        }

        if (length > maxAssetBytes)
        {
            return (null, "too-large");
        }

        if (runningTotal + length > maxTotalAssetBytes)
        {
            return (null, "total-cap-exceeded");
        }

        try
        {
            return (File.ReadAllBytes(resolved), null);
        }
        catch (IOException)
        {
            return (null, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "unreadable");
        }
    }

    /// <summary>
    /// Resolve <paramref name="reference"/> to an absolute path CONFINED to <paramref name="normalizedRoot"/>,
    /// returning <c>null</c> when it escapes. Strips a <c>file://</c> scheme first (via <see cref="Uri"/>, so a
    /// Windows <c>file:///C:/…</c> URI maps to a real OS path), then canonicalizes with
    /// <see cref="Path.GetFullPath(string)"/> relative to the root. The containment test mirrors
    /// <c>Charter.Server.PathConfinement.Resolve</c>: accept only when the resolved path EQUALS the root or
    /// starts with the root plus a directory separator — never a bare string-prefix match.
    /// </summary>
    private static string? ResolveConfined(string reference, string normalizedRoot)
    {
        try
        {
            var candidate = reference.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(reference).LocalPath
                : reference;

            var full = Path.GetFullPath(Path.Combine(normalizedRoot, candidate));

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (full.Equals(normalizedRoot, comparison))
            {
                return full;
            }

            var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
            return full.StartsWith(rootPrefix, comparison) ? full : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True when a src value is a remote (<c>http(s)://</c>) or already-inlined (<c>data:</c>) URI.</summary>
    private static bool IsRemoteOrData(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>data:</c> MIME type for a reference, chosen purely by file extension. Unknown extensions fall
    /// back to <c>application/octet-stream</c> — still self-contained and path-free, an honest degradation
    /// rather than a leak.
    /// </summary>
    private static string MimeFor(string reference)
    {
        var ext = Path.GetExtension(reference);
        if (SameExt(ext, ".png")) return "image/png";
        if (SameExt(ext, ".jpg") || SameExt(ext, ".jpeg")) return "image/jpeg";
        if (SameExt(ext, ".gif")) return "image/gif";
        if (SameExt(ext, ".svg")) return "image/svg+xml";
        if (SameExt(ext, ".webp")) return "image/webp";
        if (SameExt(ext, ".bmp")) return "image/bmp";
        if (SameExt(ext, ".mp4")) return "video/mp4";
        if (SameExt(ext, ".webm")) return "video/webm";
        if (SameExt(ext, ".pdf")) return "application/pdf";
        if (SameExt(ext, ".css")) return "text/css";
        return "application/octet-stream";

        static bool SameExt(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bare basename of an omitted reference — <see cref="Path.GetFileName(string)"/> only, never the
    /// directory portion. Safe to keep on an omitted element because its context already tells the reader it
    /// was an asset; the basename carries no extra information the surrounding markup did not.
    /// </summary>
    private static string BaseName(string reference)
    {
        try
        {
            if (reference.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(new Uri(reference).LocalPath);
            }
        }
        catch (UriFormatException)
        {
            // Fall through and treat the raw reference as a plain path.
        }

        return Path.GetFileName(reference);
    }

    /// <summary>Minimal HTML-attribute escaping for a value written inside double quotes.</summary>
    private static string EncodeAttribute(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
               .Replace("<", "&lt;", StringComparison.Ordinal)
               .Replace(">", "&gt;", StringComparison.Ordinal)
               .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>
    /// Captures every <c>&lt;script …&gt;…&lt;/script&gt;</c> region (non-greedy, spanning newlines) so the
    /// inlined Mermaid runtime and its bootstrap can be set aside UNTOUCHED before the asset scan runs.
    /// </summary>
    [GeneratedRegex("<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegionRegex();

    /// <summary>
    /// Matches a <c>src="…"</c> attribute on ANY element (tag-agnostic, so a <c>:::custom-html</c>
    /// <c>&lt;video src&gt;</c>/<c>&lt;iframe src&gt;</c> is covered exactly like an <c>&lt;img src&gt;</c>).
    /// The leading negative lookbehind keeps it from matching a longer attribute name that ends in
    /// <c>src</c> (e.g. <c>data-src</c>).
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])src\s*=\s*""(?<value>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex SrcAttributeRegex();

    /// <summary>
    /// Matches a whole <c>file://</c> reference up to the enclosing quote/whitespace/angle-bracket, used by
    /// the final redaction pass to replace it (basename included) with the bare constant <c>file:///[redacted]</c>.
    /// </summary>
    [GeneratedRegex(@"file://[^""'\s<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex FileUriRegex();

    /// <summary>
    /// Matches an UNAMBIGUOUS local filesystem path — a Windows drive-letter path (a lone drive letter,
    /// colon, then a separator: <c>C:/Users/…</c> or <c>C:\Users\…</c>) or a UNC path
    /// (<c>\\server\share\…</c>) — wherever it survives on a non-<c>src</c> carrier (an <c>href</c>,
    /// <c>srcset</c>, CSS <c>url(...)</c>, or <c>xlink:href</c>). The negative lookbehind on the drive letter
    /// keeps a URL scheme like <c>http://</c> (whose <c>p:/</c> would otherwise read as a drive) from
    /// matching, and a bare POSIX <c>/…</c> path is deliberately NOT matched — a root-relative URL is
    /// legitimate and must survive. The path body stops at the first quote, whitespace, angle bracket, or
    /// closing parenthesis, so an <c>srcset</c> descriptor (<c>… 2x</c>) or a CSS <c>url(…)</c> terminator is
    /// preserved after the redacted path.
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z])[A-Za-z]:[\\/][^""'\s<>)]*|\\\\[^""'\s<>)]+")]
    private static partial Regex LocalPathRegex();
}
