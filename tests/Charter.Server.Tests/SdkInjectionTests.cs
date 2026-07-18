using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// SDK-injection tests: <see cref="SdkInjector.Inject"/> must weave the serve-time annotation SDK into a
/// rendered plan's HTML — preserving the original body, carrying the stable <c>data-charter-sdk</c> marker,
/// landing before <c>&lt;/body&gt;</c> when present, and never mutating the caller's input.
/// </summary>
[Trait("Category", "ReviewServer")]
public class SdkInjectionTests
{
    private const string Marker = "data-charter-sdk";
    private const string SdkScript = "<script data-charter-sdk>window.__charterReview=true;</script>";

    [Fact]
    public void Inject_KeepsOriginalBody_AndInjectsMarkedSdkScript()
    {
        const string html = "<html><body><h1 id=\"b0\">Rendered Plan</h1></body></html>";

        var result = SdkInjector.Inject(html, SdkScript);

        Assert.Contains("<h1 id=\"b0\">Rendered Plan</h1>", result); // the original body content survives
        Assert.Contains(SdkScript, result);                          // the SDK script is injected verbatim
        Assert.Contains(Marker, result);                             // and it carries the stable marker
    }

    [Fact]
    public void Inject_PlacesScriptBeforeClosingBodyTag()
    {
        const string html = "<html><body><p id=\"b1\">content</p></body></html>";

        var result = SdkInjector.Inject(html, SdkScript);

        var scriptIndex = result.IndexOf(Marker, StringComparison.Ordinal);
        var closingBodyIndex = result.LastIndexOf("</body>", StringComparison.Ordinal);
        Assert.True(scriptIndex >= 0, "the injected SDK marker must be present in the output");
        Assert.True(closingBodyIndex >= 0, "the original </body> must remain in the output");
        Assert.True(scriptIndex < closingBodyIndex, "the SDK script must be injected before </body>");
    }

    [Fact]
    public void Inject_AppendsScript_WhenNoBodyTagPresent()
    {
        const string html = "<h1 id=\"b2\">A fragment with no body element</h1>";

        var result = SdkInjector.Inject(html, SdkScript);

        Assert.Contains("<h1 id=\"b2\">A fragment with no body element</h1>", result);
        Assert.Contains(Marker, result); // still injected (appended) even without a </body> anchor
    }

    [Fact]
    public void Inject_DoesNotMutateTheInputHtml()
    {
        const string original = "<html><body><p id=\"b3\">untouched</p></body></html>";
        var input = original;

        _ = SdkInjector.Inject(input, SdkScript);

        Assert.Equal(original, input);            // the caller's input is left exactly as passed in
        Assert.DoesNotContain(Marker, input);     // injection produced a new string, not an edit in place
    }
}
