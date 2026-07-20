using System.IO;
using Charter.Core;
using Xunit;

namespace Charter.Core.Tests;

/// <summary>
/// TDD-red tests for <see cref="ArtifactExporter.Export"/> — the true-offline export step layered on top of
/// <see cref="CharterRenderer.Render(string)"/>. These compile against the stub (all referenced types exist)
/// and FAIL at runtime because the stub throws <see cref="System.NotImplementedException"/>; task
/// <c>02-implement-artifact-exporter</c> makes them pass by implementing the real behavior.
///
/// Each test is fully self-contained: it materializes its own temp <c>planDirectory</c> (plus, where a test
/// needs an OUTSIDE-the-root fixture, a sibling/parent under the same scratch root), writes its fixtures,
/// and deletes the whole scratch tree in a <c>finally</c> — mirroring the temp-dir-with-cleanup style used by
/// the server tests (e.g. <c>LoopbackServeTests</c>/<c>PathConfinementTests</c>).
///
/// Export's contract, in one line: inline LOCAL image/media assets (resolved under, and confined to,
/// <c>planDirectory</c>) as <c>data:</c> URIs; omit (never leak the path of) assets that are oversized,
/// missing, or escape the root; redact any remaining non-<c>src</c> local <c>file://</c> reference to a bare
/// constant; leave remote assets alone; and NEVER emit the serve-time <c>data-charter-sdk</c> marker.
/// </summary>
[Trait("Category", "ArtifactExporter")]
public class ArtifactExporterTests
{
    // A real (byte-verified) fragment of the vendored Mermaid library: a JS string that LOOKS like an
    // <img src="..."> tag to a naive regex. The load-bearing regression test (12) proves the asset scan
    // leaves it untouched — a naive whole-HTML scan would rewrite it to about:blank and corrupt the runtime.
    private const string MermaidImgFalsePositive = "<img src=\"'+es(this.src)+'\"";

    // The distinctive interior marker of the minified Mermaid build (present in assets/mermaid.min.js and
    // inlined verbatim by CharterRenderer): its survival proves the runtime bytes rode through unmodified.
    private const string MermaidLibraryMarker = "__esbuild_esm_mermaid_nm";

    /// <summary>
    /// 1. A relative local image inlines as a <c>data:image/png;base64,&lt;exact bytes&gt;</c> URI, replacing
    /// the original relative path.
    /// </summary>
    [Fact]
    public void Export_RelativeLocalImage_InlinesAsDataUri()
        => WithScratch((_, planDirectory) =>
        {
            var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };
            File.WriteAllBytes(Path.Combine(planDirectory, "diagram.png"), bytes);
            const string markdown = "![Diagram](./diagram.png)";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.Contains("data:image/png;base64,", html);
            Assert.Contains("data:image/png;base64," + System.Convert.ToBase64String(bytes), html);
            Assert.DoesNotContain("./diagram.png", html);
        });

    /// <summary>
    /// 2. A <c>file:///</c>-referenced local image inlines identically to case 1 — and no literal
    /// <c>file://</c> path text survives anywhere in the output.
    /// </summary>
    [Fact]
    public void Export_FileUriLocalImage_InlinesAndDropsFileScheme()
        => WithScratch((_, planDirectory) =>
        {
            var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x11, 0x22, 0x33, 0x44 };
            var imagePath = Path.Combine(planDirectory, "shot.png");
            File.WriteAllBytes(imagePath, bytes);
            var markdown = $"![Shot]({FileUri(imagePath)})";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.Contains("data:image/png;base64," + System.Convert.ToBase64String(bytes), html);
            Assert.DoesNotContain("file://", html);
        });

    /// <summary>
    /// 3. MIME is chosen by extension: a non-PNG asset (<c>.svg</c>) inlines with the matching
    /// <c>data:image/svg+xml;base64,</c> prefix.
    /// </summary>
    [Fact]
    public void Export_SvgLocalImage_InlinesWithSvgMime()
        => WithScratch((_, planDirectory) =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
            File.WriteAllBytes(Path.Combine(planDirectory, "vector.svg"), bytes);
            const string markdown = "![Vector](./vector.svg)";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.Contains("data:image/svg+xml;base64,", html);
            Assert.Contains("data:image/svg+xml;base64," + System.Convert.ToBase64String(bytes), html);
        });

    /// <summary>
    /// 4. An asset larger than <c>maxAssetBytes</c> is omitted (src <c>about:blank</c>,
    /// <c>data-charter-export-omitted="too-large"</c>, <c>data-charter-export-filename</c> = basename only),
    /// never inlined, and its full local path never leaks anywhere in the output.
    /// </summary>
    [Fact]
    public void Export_OversizedAsset_IsOmittedAndPathNeverLeaks()
        => WithScratch((_, planDirectory) =>
        {
            var bytes = Filled(0x2A, 200); // 200 bytes, over the 100-byte override below
            File.WriteAllBytes(Path.Combine(planDirectory, "big.png"), bytes);
            const string markdown = "![Big](./big.png)";

            var html = ArtifactExporter.Export(markdown, planDirectory, maxAssetBytes: 100);

            Assert.DoesNotContain("data:image/png;base64,", html);
            Assert.DoesNotContain(System.Convert.ToBase64String(bytes), html);
            Assert.Contains("about:blank", html);
            Assert.Contains("data-charter-export-omitted=\"too-large\"", html);
            Assert.Contains("data-charter-export-filename=\"big.png\"", html);
            // The full local path (temp-dir + basename) must be absent EVERYWHERE, not merely out of `src`.
            Assert.DoesNotContain(planDirectory, html);
        });

    /// <summary>
    /// 5. A referenced asset that does not exist is omitted the same way (<c>not-found</c>,
    /// <c>about:blank</c>), with no leaked path.
    /// </summary>
    [Fact]
    public void Export_MissingAsset_IsOmittedAsNotFound()
        => WithScratch((_, planDirectory) =>
        {
            // No file written — the reference dangles.
            const string markdown = "![Gone](./missing.png)";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.DoesNotContain("data:image/png;base64,", html);
            Assert.Contains("about:blank", html);
            Assert.Contains("data-charter-export-omitted=\"not-found\"", html);
            Assert.DoesNotContain(planDirectory, html);
        });

    /// <summary>
    /// 6. A <c>../outside.png</c> traversal to a file that genuinely exists ONE LEVEL UP is refused — treated
    /// exactly like a missing asset (omitted, never read, path never leaked). Asset reads are confined to
    /// <c>planDirectory</c>.
    /// </summary>
    [Fact]
    public void Export_PathTraversalOutsideRoot_IsRefusedLikeMissing()
        => WithScratch((root, planDirectory) =>
        {
            // A real file just outside planDirectory (in the scratch root, one level up).
            var outsidePath = Path.Combine(root, "outside.png");
            File.WriteAllBytes(outsidePath, Filled(0x55, 32));
            const string markdown = "![Escape](../outside.png)";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.DoesNotContain("data:image/png;base64,", html);
            Assert.Contains("about:blank", html);
            Assert.Contains("data-charter-export-omitted=\"not-found\"", html);
            // Neither the escaping resolved path nor the scratch root may leak.
            Assert.DoesNotContain(outsidePath, html);
            Assert.DoesNotContain(root, html);
        });

    /// <summary>
    /// 7. A non-image local reference (a <c>file://</c> link) is REDACTED — its entire URI is rewritten to the
    /// bare constant <c>file:///[redacted]</c> with NO basename kept (unlike the image-omission cases, which
    /// keep a basename). No fragment of the original URI survives.
    /// </summary>
    [Fact]
    public void Export_NonImageFileUriLink_IsRedactedToBareConstant()
        => WithScratch((_, planDirectory) =>
        {
            const string markdown = "See [Notes](file:///C:/some/local/notes.txt) for details.";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.Contains("file:///[redacted]", html);
            // The ENTIRE original URI is gone — no directory fragment and (deliberately) no basename.
            Assert.DoesNotContain("C:/some/local", html);
            Assert.DoesNotContain("notes.txt", html);
        });

    /// <summary>
    /// 8. The local-reference scan is TAG-AGNOSTIC: a <c>:::custom-html</c> block whose raw
    /// <c>&lt;video src="file://…mp4"&gt;</c> points at a local file inlines exactly like an <c>&lt;img src&gt;</c>
    /// (<c>data:video/mp4;base64,</c>), with no path or <c>file://</c> left behind. The scan matches any
    /// <c>src="…"</c> value regardless of the element it sits on.
    /// </summary>
    [Fact]
    public void Export_CustomHtmlVideoSrc_InlinesLikeImage()
        => WithScratch((_, planDirectory) =>
        {
            var bytes = Filled(0x77, 24);
            var clipPath = Path.Combine(planDirectory, "clip.mp4");
            File.WriteAllBytes(clipPath, bytes);
            var markdown =
                ":::custom-html\n" +
                $"<video src=\"{FileUri(clipPath)}\"></video>\n" +
                ":::";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.Contains("data:video/mp4;base64,", html);
            Assert.Contains("data:video/mp4;base64," + System.Convert.ToBase64String(bytes), html);
            Assert.DoesNotContain("file://", html);
            Assert.DoesNotContain(planDirectory, html);
        });

    /// <summary>
    /// 9. A remote (<c>http(s)://</c>) asset is out of scope for inlining — its exact URL survives verbatim.
    /// </summary>
    [Fact]
    public void Export_RemoteImage_IsLeftUntouched()
        => WithScratch((_, planDirectory) =>
        {
            const string remoteUrl = "https://example.com/remote-diagram.png";
            var markdown = $"![Remote]({remoteUrl})";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.Contains(remoteUrl, html);
        });

    /// <summary>
    /// 10. The exported artifact NEVER carries the serve-time SDK marker (<c>data-charter-sdk</c>) — the SDK is
    /// injected only at serve time by the server, never by <c>export</c> (invariant 1).
    /// </summary>
    [Fact]
    public void Export_Output_NeverContainsSdkMarker()
        => WithScratch((_, planDirectory) =>
        {
            const string markdown = "# A plan\n\nSome ordinary prose with no assets.";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.DoesNotContain("data-charter-sdk", html);
        });

    /// <summary>
    /// 11. A document with no local references round-trips unchanged in substance: the exact HTML
    /// <see cref="CharterRenderer.Render(string)"/> alone produces survives inside the export output (export
    /// must not corrupt ordinary content when there is nothing to inline or redact).
    /// </summary>
    [Fact]
    public void Export_DocumentWithNoLocalReferences_PreservesRenderedContent()
        => WithScratch((_, planDirectory) =>
        {
            const string markdown =
                "# Title\n\n" +
                "A plain prose paragraph.\n\n" +
                "![Remote](https://example.com/pic.png)";

            var rendered = CharterRenderer.Render(markdown);
            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.Contains(rendered, html);
        });

    /// <summary>
    /// 12. THE load-bearing regression test. A document containing BOTH a <c>:::diagram</c> (which inlines the
    /// vendored Mermaid runtime) AND a local image must (a) inline the local image as a real
    /// <c>data:image/png;base64,</c> URI, AND (b) leave the Mermaid runtime byte-identical — proven by the
    /// library marker surviving unmodified and by the naive JS-string false-positive
    /// (<c>&lt;img src="'+es(this.src)+'"</c>) NOT being rewritten to <c>about:blank</c>/omitted. A whole-HTML
    /// asset scan that reaches into <c>&lt;script&gt;</c> would corrupt the diagram runtime.
    /// </summary>
    [Fact]
    public void Export_DiagramPlusImage_InlinesImageButNeverTouchesMermaidRuntime()
        => WithScratch((_, planDirectory) =>
        {
            var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xAA, 0xBB, 0xCC, 0xDD };
            File.WriteAllBytes(Path.Combine(planDirectory, "pic.png"), bytes);
            const string markdown =
                ":::diagram\n" +
                "graph TD\n" +
                "A-->B\n" +
                ":::\n\n" +
                "![Pic](./pic.png)";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            // (a) the local image is inlined, exactly as in test 1.
            Assert.Contains("data:image/png;base64," + System.Convert.ToBase64String(bytes), html);

            // (b) the inlined Mermaid runtime is untouched: its interior marker survives verbatim, and the
            // JS-string fragment that LOOKS like an <img src> tag was NOT rewritten. (Both are byte-verified
            // substrings of the real vendored assets/mermaid.min.js.)
            Assert.Contains(MermaidLibraryMarker, html);
            Assert.Contains(MermaidImgFalsePositive, html);
            Assert.DoesNotContain("data-charter-export-omitted=\"not-found\"", html);
        });

    /// <summary>
    /// 13. Confinement is directory-separator-safe: a SIBLING directory that shares <c>planDirectory</c> as a
    /// raw STRING prefix (<c>&lt;root&gt;/plan-evil</c> vs <c>&lt;root&gt;/plan</c>) is rejected. A naive
    /// "resolved path starts with the planDirectory string" check would wrongly accept
    /// <c>../plan-evil/secret.png</c>; this test catches that.
    /// </summary>
    [Fact]
    public void Export_SiblingDirectorySharingStringPrefix_IsRefused()
        => WithScratch((root, planDirectory) =>
        {
            // planDirectory is <root>/plan; the sibling <root>/plan-evil textually starts with the same prefix.
            var evilDir = planDirectory + "-evil";
            Directory.CreateDirectory(evilDir);
            var secretPath = Path.Combine(evilDir, "secret.png");
            File.WriteAllBytes(secretPath, Filled(0x66, 48));
            const string markdown = "![Sneaky](../plan-evil/secret.png)";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.DoesNotContain("data:image/png;base64,", html);
            Assert.Contains("data-charter-export-omitted=\"not-found\"", html);
            Assert.DoesNotContain(evilDir, html);
            Assert.DoesNotContain(secretPath, html);
        });

    /// <summary>
    /// 14. The cumulative <c>maxTotalAssetBytes</c> cap stops inlining once spent — even for a later asset that
    /// alone fits under <c>maxAssetBytes</c>. Two under-cap images in document order: the first inlines, the
    /// second is omitted with <c>data-charter-export-omitted="total-cap-exceeded"</c> because the first already
    /// spent the budget.
    /// </summary>
    [Fact]
    public void Export_TotalAssetCapExceeded_OmitsLaterAssetUnderPerAssetCap()
        => WithScratch((_, planDirectory) =>
        {
            var first = Filled(0x01, 100);
            var second = Filled(0x02, 100);
            File.WriteAllBytes(Path.Combine(planDirectory, "one.png"), first);
            File.WriteAllBytes(Path.Combine(planDirectory, "two.png"), second);
            const string markdown = "![One](./one.png)\n\n![Two](./two.png)";

            // Per-asset cap left at default (both 100-byte files fit); total cap only fits the FIRST.
            var html = ArtifactExporter.Export(markdown, planDirectory, maxTotalAssetBytes: 150);

            // First asset inlines...
            Assert.Contains("data:image/png;base64," + System.Convert.ToBase64String(first), html);
            // ...the second is omitted because the running total already exceeded the budget.
            Assert.DoesNotContain(System.Convert.ToBase64String(second), html);
            Assert.Contains("data-charter-export-omitted=\"total-cap-exceeded\"", html);
        });

    /// <summary>
    /// 15. Absolute local paths that ride NON-<c>src</c> carriers — an <c>&lt;a href&gt;</c>, a CSS
    /// <c>url(...)</c> inside a <c>style</c>, and an <c>&lt;img srcset&gt;</c> — are redacted to the bare
    /// <c>file:///[redacted]</c> sentinel. The asset-inlining pass only handles <c>src=</c>, so an unambiguous
    /// drive-letter path anywhere else would otherwise ship in the artifact and leak the author's username and
    /// directory layout. Neither the username (<c>Alice</c>) nor any absolute path may survive.
    /// </summary>
    [Fact]
    public void Export_LocalPathsInNonSrcCarriers_AreRedacted()
        => WithScratch((_, planDirectory) =>
        {
            const string markdown =
                "See [notes](C:/Users/Alice/secret/notes.md).\n\n" +
                ":::custom-html\n" +
                "<div style=\"background:url('C:/Users/Alice/private/bg.png')\"></div>\n" +
                "<img srcset=\"C:/Users/Alice/pics/hi-2x.png 2x\">\n" +
                ":::";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.DoesNotContain("Alice", html);
            Assert.DoesNotContain("C:/Users/Alice/secret/notes.md", html);
            Assert.DoesNotContain("C:/Users/Alice/private/bg.png", html);
            Assert.DoesNotContain("C:/Users/Alice/pics/hi-2x.png", html);
            Assert.Contains("file:///[redacted]", html);
        });

    /// <summary>
    /// 16. The redaction is targeted, not a blanket path scrub: a legitimate remote URL (whose scheme
    /// <c>http://</c> superficially contains a <c>p:/</c> that looks like a drive letter) and a root-relative
    /// URL both survive untouched. Only UNAMBIGUOUS local paths are redacted.
    /// </summary>
    [Fact]
    public void Export_RemoteAndRootRelativeUrls_AreNotRedacted()
        => WithScratch((_, planDirectory) =>
        {
            const string markdown =
                "A [remote](https://example.com/docs/page) link and a [root](/docs/local-guide) link.";

            var html = ArtifactExporter.Export(markdown, planDirectory);

            Assert.Contains("https://example.com/docs/page", html);
            Assert.Contains("/docs/local-guide", html);
        });

    // --- helpers ---------------------------------------------------------------------------------------

    /// <summary>
    /// Create a fresh scratch root plus a <c>plan</c> sub-directory used as <c>planDirectory</c> (so a
    /// <c>..</c> reference has a real level to escape to), run <paramref name="test"/> with
    /// <c>(scratchRoot, planDirectory)</c>, then delete the whole scratch tree in a <c>finally</c>.
    /// </summary>
    private static void WithScratch(System.Action<string, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "charter-export-" + System.Guid.NewGuid().ToString("N"));
        var planDirectory = Path.Combine(root, "plan");
        Directory.CreateDirectory(planDirectory);
        try
        {
            test(root, planDirectory);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>The absolute <c>file:///…</c> URI for a local path (e.g. <c>file:///C:/…/clip.mp4</c>).</summary>
    private static string FileUri(string absolutePath) => new System.Uri(absolutePath).AbsoluteUri;

    /// <summary>A byte array of <paramref name="count"/> copies of <paramref name="value"/>.</summary>
    private static byte[] Filled(byte value, int count)
    {
        var bytes = new byte[count];
        System.Array.Fill(bytes, value);
        return bytes;
    }
}
