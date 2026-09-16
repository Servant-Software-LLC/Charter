using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #253 — with several charters served at once, the tabs were indistinguishable: the URL named the session
/// (a port and a key) and the page had no title at all, so every tab read <c>127.0.0.1:57006/?key=…</c>.
///
/// <para>
/// The load-bearing constraint is the READY LINE. It is a contract agents parse, and nothing specifies how — one of
/// Charter's own tests took everything after <c>?key=</c> as the key. Appending <c>&amp;plan=…</c> there would have
/// broken any parser shaped like that. So these pin that the ready-line URL is EXACTLY its old shape, while the plan's
/// name reaches the two surfaces a human actually identifies a tab by: the page title, and the URL Charter opens in
/// the browser itself.
/// </para>
///
/// Class trait (exact literal for the coverage guardrail): [Trait("Category","ReviewUrlAndTitle")].
/// </summary>
[Trait("Category", "ReviewUrlAndTitle")]
public class ReviewUrlAndTitleTests : IDisposable
{
    private static readonly Uri Address = new("http://127.0.0.1:57006/");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-review-title-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("39-incremental-delivery.charter.md", "39-incremental-delivery")]
    [InlineData("notes.md", "notes")]
    [InlineData("Plan.CHARTER.MD", "Plan")]
    [InlineData("v1.2-rollout.charter.md", "v1.2-rollout")]
    [InlineData("README", "README")]
    public void The_plan_name_is_the_file_name_without_its_charter_suffix(string fileName, string expected)
    {
        Assert.Equal(expected, SessionFor(fileName).PlanName);
    }

    [Fact]
    public void The_ready_line_url_is_exactly_its_old_shape_with_nothing_after_the_key()
    {
        var session = SessionFor("39-incremental-delivery.charter.md");

        var url = session.CapabilityUrl(Address);

        Assert.Matches(new Regex(@"^http://127\.0\.0\.1:\d+/\?key=[0-9a-f]+$"), url);
        Assert.DoesNotContain("plan", url, StringComparison.Ordinal);

        // The parser that motivated the constraint — everything after `?key=` — must still recover the key exactly.
        var greedy = url[(url.IndexOf("?key=", StringComparison.Ordinal) + "?key=".Length)..];
        Assert.Equal(session.Key.Value, greedy);
    }

    [Fact]
    public void The_browser_url_carries_the_plan_name_after_the_key()
    {
        var session = SessionFor("39-incremental-delivery.charter.md");

        var url = session.BrowserUrl(Address);

        Assert.StartsWith(session.CapabilityUrl(Address) + "&plan=", url, StringComparison.Ordinal);
        Assert.EndsWith("&plan=39-incremental-delivery", url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_name_that_needs_escaping_is_escaped_in_the_browser_url()
    {
        var session = SessionFor("r&d plan.charter.md");

        var url = session.BrowserUrl(Address);

        Assert.EndsWith("&plan=r%26d%20plan", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_served_page_is_titled_with_the_plan_name_on_both_urls()
    {
        var planPath = WritePlan("39-incremental-delivery.charter.md");
        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        using var client = new HttpClient();

        // The server authorizes on `key` alone, so the URL an agent parses and the URL a human opens serve the same
        // titled page.
        foreach (var url in new[] { session.CapabilityUrl(server.Address), session.BrowserUrl(server.Address) })
        {
            var html = await client.GetStringAsync(url);
            Assert.Equal("39-incremental-delivery · Charter review", Title(html));
        }
    }

    [Fact]
    public async Task The_title_is_html_encoded()
    {
        var planPath = WritePlan("r&d.charter.md");
        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(
            session, new ReviewServerOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        using var client = new HttpClient();

        var html = await client.GetStringAsync(session.CapabilityUrl(server.Address));

        Assert.Contains("<title>r&amp;d", html, StringComparison.Ordinal);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private ReviewSession SessionFor(string fileName) => ReviewSession.Create(Path.Combine(_root, fileName));

    private string WritePlan(string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "---\ncharter-format-version: 1\n---\n\n# A plan\n\nA paragraph.\n");
        return path;
    }

    private static string Title(string html)
    {
        var match = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline);
        Assert.True(match.Success, "the served page carries no <title>");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}
