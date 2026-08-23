using System.Net;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #184 — a <c>:::diagram</c> nested inside a <c>::::note</c> must DRAW, in the served page and in
/// the saved artifact alike.
///
/// <para><c>RenderBody</c>'s <c>hasDiagram</c> was raised by the same top-level-only walk as the anchor pass,
/// so a nested diagram never inlined the Mermaid runtime and the block sat on the page as its own source
/// text. #166 had already made that block annotatable, which is the sharp edge: a reviewer could take a note
/// on a diagram that never became a picture.</para>
///
/// <para><b>The fixture carries no top-level diagram, deliberately.</b> With one present the runtime is
/// inlined for THAT block and the nested one draws by coincidence — the reason the defect reads as
/// intermittent, and the reason a careless fixture would pass without the fix.</para>
///
/// <para><b>The control is the EXPORTED artifact, not a second served page</b> (the #176 technique). The
/// runtime is what <c>export</c> and <c>review</c> share, so a served-page-only assertion could hold while
/// the saved file — the thing a reviewer mails to someone — still showed source text. The artifact carries no
/// SDK by construction (invariant 1), so it is also the honest place to prove the drawing is the RENDERER's
/// doing and not the SDK's.</para>
///
/// <para><b>And #177 is held from the other side in the same plan.</b> The escape hatch's forged
/// <c>&lt;pre class="mermaid"&gt;</c> must still read as the source text the author typed, on both surfaces —
/// so the fix that makes Charter's own nested diagram draw cannot have re-widened the node list back to a
/// document-wide <c>.mermaid</c> sweep.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    /// <summary>
    /// A <c>:::diagram</c> one level inside a <c>::::note</c>, an escape hatch whose body forges the same
    /// markup, and NO top-level diagram anywhere — so nothing but the nested block can inline the runtime.
    /// </summary>
    private const string NestedDiagramPlan =
        "# A note that explains itself with a picture\n\n" +
        "An ordinary paragraph, so the plan is not only callouts.\n\n" +
        "::::note\n" +
        "This design has one moving part, and here it is.\n\n" +
        ":::diagram\n" +
        "graph TD; Ingress-->Auth;\n" +
        ":::\n" +
        "::::\n\n" +
        ":::custom-html\n" +
        "<pre class=\"mermaid\" id=\"forged-nested\">graph TD; Forged--&gt;Node;</pre>\n" +
        ":::\n";

    /// <summary>The literal text the author wrote inside the forged <c>&lt;pre&gt;</c>, after HTML parsing.</summary>
    private const string ForgedNestedSource = "graph TD; Forged-->Node;";

    [SkippableFact]
    public async Task A_nested_diagram_draws_on_the_served_page_and_in_the_saved_artifact()
    {
        var planPath = NewPlanPath("nested-diagram");
        await File.WriteAllTextAsync(planPath, NestedDiagramPlan);
        var exportPath = Path.ChangeExtension(planPath, ".export.html");
        await File.WriteAllTextAsync(
            exportPath, ArtifactExporter.Export(NestedDiagramPlan, Path.GetDirectoryName(planPath)!));

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;

            // The plan really does contain NO top-level diagram: the coincidence that hides this defect is
            // asserted absent rather than assumed, because a fixture that grew one would pass either way.
            Assert.DoesNotContain(
                BlockDocument.Parse(NestedDiagramPlan).Blocks, block => block.Kind == BlockKind.Diagram);

            var served = await NewInstrumentedPageAsync(launched);
            await served.Page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(served.Page, "ready");
            await AssertTheNestedDiagramDrewAsync(served.Page);

            var artifact = await NewInstrumentedPageAsync(launched);
            await artifact.Page.GotoAsync(
                new Uri(exportPath).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            Assert.False(
                await artifact.Page.EvaluateAsync<bool>("() => typeof window.CharterAnnotate !== 'undefined'"),
                "the exported artifact must ship WITHOUT the annotation SDK");
            await AssertTheNestedDiagramDrewAsync(artifact.Page);

            AssertNoBrowserErrors(served);
            AssertNoBrowserErrors(artifact);
        }
        finally
        {
            Cleanup(planPath);
            Cleanup(exportPath);
        }
    }

    /// <summary>
    /// Mermaid rendered the block CHARTER authored — the one inside the callout, which carries no id of its
    /// own (#166, and deliberately still so) — and left the author's forged twin as the text they typed.
    /// </summary>
    private static async Task AssertTheNestedDiagramDrewAsync(IPage page)
    {
        // The nested diagram became a picture. Waiting on the <svg> is what stops this passing against a page
        // where the runtime was never inlined at all — which is precisely the defect.
        await page.WaitForSelectorAsync("div.note pre.mermaid svg");

        // It is still ID-LESS: giving it a real rendering does not give it a real anchor (that question is
        // settled — a note on it resolves outward to the enclosing div.note).
        Assert.Equal(
            0, await page.Locator("div.note pre.mermaid[id]").CountAsync());

        // #177: the escape hatch's forged twin is untouched, on this surface too.
        Assert.Equal(0, await page.Locator("#forged-nested svg").CountAsync());
        Assert.Equal(
            ForgedNestedSource, (await page.Locator("#forged-nested").InnerTextAsync()).Trim());
        Assert.Equal(
            0, await page.Locator(".custom-html [data-processed], .custom-html[data-processed]").CountAsync());
    }
}
