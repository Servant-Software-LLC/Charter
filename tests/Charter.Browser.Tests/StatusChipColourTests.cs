using System.Net;
using System.Text.Json;
using Charter.Core;
using Charter.Server;
using Microsoft.Playwright;
using Xunit;

namespace Charter.Browser.Tests;

/// <summary>
/// Charter #242 — a comment's status chip must say what state it is in at a glance, not only in its 11px word.
///
/// <para><b>The defect.</b> The chip is built as <c>charter-chip charter-chip-&lt;status&gt;</c>, but the injected
/// CSS styled only <c>contested</c>. <c>open</c> and <c>resolved</c> fell through to the neutral base chip, so
/// the two states a reviewer most needs to tell apart were visually identical.</para>
///
/// <para><b>What this pins, and why each is measured rather than eyeballed.</b></para>
/// <list type="bullet">
///   <item><description><c>resolved</c> uses the SAME green as the question block's ANSWERED pill — compared
///   against the pill's own computed colour, so "the same green" cannot drift into "a similar green".</description></item>
///   <item><description><c>open</c> is visibly tinted, yet QUIETER than <c>contested</c>: measured as distance from
///   the page background, which is the property that makes a chip compete for attention in either colour
///   scheme. A yellow as loud as <c>contested</c> would dress every note in the alert colour.</description></item>
///   <item><description><c>retracted</c> has a deliberate treatment rather than falling through.</description></item>
///   <item><description>Every chip still carries its status as TEXT — colour reinforces, it never replaces.</description></item>
///   <item><description>All of it holds in dark mode, and the test proves dark mode was genuinely active before
///   trusting anything it measured there.</description></item>
/// </list>
///
/// <para>The comments are written to the review log BEFORE the server starts, so the first fold already holds
/// every status. That avoids the watcher race the team-review test has to work around by touching files.</para>
/// </summary>
public sealed partial class ReviewLoopBrowserTests
{
    private const string ChipPlan =
        "# Status chip plan\n\n" +
        "The first paragraph.\n\n" +
        "The second paragraph.\n\n" +
        "The third paragraph.\n\n" +
        "The fourth paragraph.\n\n" +
        ":::question\n" +
        "{\"id\":\"q-store\",\"title\":\"Which datastore?\",\"mode\":\"single\",\"target\":\"human\"," +
        "\"options\":[\"Postgres\",\"SQLite\"],\"answer\":[\"Postgres\"]}\n" +
        ":::\n";

    // One synchronous read of every chip, so a re-render between reads cannot detach a handle. Colours are
    // normalized IN the browser: Chromium serializes a color-mix() result as `color(srgb r g b)` rather than
    // `rgb(...)`, and an unparseable serialization returns null so the test fails loudly instead of comparing
    // two strings that merely happen to differ.
    private const string ReadChipsScript = """
        (ids) => {
          const toRgba = (s) => {
            if (!s) return null;
            let m = s.match(/^rgba?\(([^)]+)\)$/);
            if (m) {
              const p = m[1].split(/[\s,\/]+/).filter(Boolean).map(Number);
              return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1];
            }
            m = s.match(/^color\(srgb\s+([-\d.e]+)\s+([-\d.e]+)\s+([-\d.e]+)(?:\s*\/\s*([-\d.e]+))?\)$/);
            if (m) return [m[1] * 255, m[2] * 255, m[3] * 255, m[4] === undefined ? 1 : Number(m[4])];
            return null;
          };
          const read = (el) => {
            if (!el) return null;
            const cs = getComputedStyle(el);
            return {
              text: el.textContent.trim(),
              background: cs.backgroundColor,
              backgroundRgba: toRgba(cs.backgroundColor),
              color: cs.color,
              borderRgba: toRgba(cs.borderTopColor),
              decoration: cs.textDecorationLine
            };
          };
          const chip = (id) =>
            document.querySelector('[data-annotation-id="' + id + '"] [data-charter-ui="item-status"]');
          const probe = document.createElement('div');
          probe.style.background = 'var(--charter-bg)';
          document.body.appendChild(probe);
          const page = toRgba(getComputedStyle(probe).backgroundColor);
          probe.remove();
          return {
            dark: matchMedia('(prefers-color-scheme: dark)').matches,
            page: page,
            pill: read(document.querySelector('form.question .question-status')),
            open: read(chip(ids.open)),
            resolved: read(chip(ids.resolved)),
            retracted: read(chip(ids.retracted)),
            contested: read(chip(ids.contested))
          };
        }
        """;

    [SkippableFact]
    [Trait("Feature", "StatusChipColour")]
    public async Task Each_comment_status_chip_is_distinguishable_at_a_glance_in_light_and_dark()
    {
        var directory = Path.Combine(Path.GetTempPath(), "charter-chips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var planPath = Path.Combine(directory, "chips.charter.md");
        await File.WriteAllTextAsync(planPath, ChipPlan);

        var alice = new ReviewLogWriter(planPath, new ReviewAuthor("Alice Ng", "alice@example.com"));
        var bob = new ReviewLogWriter(planPath, new ReviewAuthor("Bob Chen", "bob@example.com"));
        // Any four distinct live anchors will do; the order is by id, not by position in the document.
        var anchors = SourceMap.Build(ChipPlan).Anchors.OrderBy(a => a, StringComparer.Ordinal).ToList();

        var open = alice.AppendCreate(new ReviewAnchor(anchors[0], "element", "first", null), "still open");

        var resolved = alice.AppendCreate(new ReviewAnchor(anchors[1], "element", "second", null), "settled");
        alice.AppendResolve(resolved.Id, prev: null);

        var retracted = alice.AppendCreate(new ReviewAnchor(anchors[2], "element", "third", null), "withdrawn");
        alice.AppendRetract(retracted.Id, prev: null);

        // Concurrent, disagreeing settlements — neither saw the other (prev is null on both) — fold to CONTESTED.
        var contested = bob.AppendCreate(new ReviewAnchor(anchors[3], "element", "fourth", null), "disputed");
        bob.AppendResolve(contested.Id, prev: null);
        alice.Append(new ReviewRecord
        {
            Version = ReviewRecord.CurrentVersion,
            Id = ReviewLogWriter.NewId(ReviewOpKind.Reopen),
            Op = ReviewOps.Token(ReviewOpKind.Reopen),
            Author = alice.Author,
            Target = contested.Id,
        });

        var session = ReviewSession.Create(planPath);
        using var server = ReviewServer.Start(session, new ReviewServerOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ReviewLog = alice,
        });

        try
        {
            var launched = await TryLaunchAsync();
            Skip.If(launched is null, $"{BrowserEngine.Name}/Playwright unavailable on this host.");

            await using var browser = launched!.Browser;
            var instrumented = await NewInstrumentedPageAsync(launched);
            var page = instrumented.Page;

            await page.GotoAsync(
                CapabilityUrl(server, session), new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await WaitForEventAsync(page, "ready");
            await WaitForEventAsync(page, "review-log-loaded");

            // Measure the chips as a reviewer sees them: in an OPEN panel. These notes were written straight to
            // the log rather than through the composer, so nothing has opened the panel yet, and every chip is
            // present but hidden.
            await page.ClickAsync(Ui("panel-toggle"));
            await WaitForEventAsync(page, "panel-opened");

            foreach (var (id, status) in new[]
                     {
                         (open.Id, "open"), (resolved.Id, "resolved"),
                         (retracted.Id, "retracted"), (contested.Id, "contested"),
                     })
            {
                await page.WaitForSelectorAsync(
                    "[data-annotation-id=\"" + id + "\"][data-charter-status=\"" + status + "\"] " + Ui("item-status"));
            }

            var ids = new { open = open.Id, resolved = resolved.Id, retracted = retracted.Id, contested = contested.Id };

            var light = await page.EvaluateAsync<JsonElement>(ReadChipsScript, ids);
            Assert.False(light.GetProperty("dark").GetBoolean(), "the light pass is running with a dark colour scheme");
            AssertChipHierarchy(light, "light");

            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
            var dark = await page.EvaluateAsync<JsonElement>(ReadChipsScript, ids);

            // Without this, a context that silently ignored the emulation would re-measure the light theme and
            // report dark mode as working. Prove the precondition before trusting a single value.
            Assert.True(dark.GetProperty("dark").GetBoolean(),
                "prefers-color-scheme: dark is not actually active, so the dark pass would prove nothing");
            AssertChipHierarchy(dark, "dark");

            // And dark mode must use the DARK tokens, not stay on the light green.
            Assert.NotEqual(
                light.GetProperty("resolved").GetProperty("background").GetString(),
                dark.GetProperty("resolved").GetProperty("background").GetString());
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is harmless.
            }
        }
    }

    private static void AssertChipHierarchy(JsonElement read, string scheme)
    {
        var pill = Chip(read, "pill", scheme);
        var open = Chip(read, "open", scheme);
        var resolved = Chip(read, "resolved", scheme);
        var retracted = Chip(read, "retracted", scheme);
        var contested = Chip(read, "contested", scheme);
        var page = Rgba(read.GetProperty("page"), "page background", scheme);

        // Colour reinforces; the status is still written on the chip.
        Assert.Equal("open", open.GetProperty("text").GetString());
        Assert.Equal("resolved", resolved.GetProperty("text").GetString());
        Assert.Equal("retracted", retracted.GetProperty("text").GetString());
        Assert.Equal("contested", contested.GetProperty("text").GetString());

        // resolved IS the ANSWERED green — the pill's own computed value, not a hex copied into the test.
        Assert.Equal(pill.GetProperty("background").GetString(), resolved.GetProperty("background").GetString());
        Assert.Equal(pill.GetProperty("color").GetString(), resolved.GetProperty("color").GetString());

        // open has a fill of its own. Before #242 it fell through to the base chip, which has none.
        var openBg = Rgba(open.GetProperty("backgroundRgba"), "open background", scheme);
        Assert.True(openBg[3] > 0, $"[{scheme}] the open chip has no background fill — it still looks like the base chip");

        // ...and it is QUIETER than contested: nearer the page it sits on. This is the alert hierarchy itself,
        // and it is stated as a distance so it means the same thing on a white page and on a near-black one.
        var contestedBg = Rgba(contested.GetProperty("backgroundRgba"), "contested background", scheme);
        var openLoudness = Distance(openBg, page);
        var contestedLoudness = Distance(contestedBg, page);
        Assert.True(
            openLoudness > 0,
            $"[{scheme}] the open chip's fill is identical to the page, so it carries no colour at all");
        Assert.True(
            openLoudness < contestedLoudness,
            $"[{scheme}] open ({openLoudness:F1} from the page) is at least as loud as contested " +
            $"({contestedLoudness:F1}) — every note would compete with the chip that means stop");

        // contested keeps a border open does not share, so the two differ by more than a shade of fill.
        Assert.NotEqual(
            FormatRgba(Rgba(contested.GetProperty("borderRgba"), "contested border", scheme)),
            FormatRgba(Rgba(open.GetProperty("borderRgba"), "open border", scheme)));

        // retracted is decided, not left to fall through.
        Assert.Contains("line-through", retracted.GetProperty("decoration").GetString(), StringComparison.Ordinal);
    }

    private static JsonElement Chip(JsonElement read, string name, string scheme)
    {
        var chip = read.GetProperty(name);
        Assert.True(chip.ValueKind == JsonValueKind.Object, $"[{scheme}] no '{name}' element was found on the page");
        return chip;
    }

    private static double[] Rgba(JsonElement value, string what, string scheme)
    {
        // null means the browser serialized a colour in a form the script does not parse — fail by name rather
        // than silently comparing strings.
        Assert.True(value.ValueKind == JsonValueKind.Array, $"[{scheme}] could not parse the {what} colour");
        return value.EnumerateArray().Select(v => v.GetDouble()).ToArray();
    }

    private static double Distance(double[] a, double[] b) =>
        Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2));

    private static string FormatRgba(double[] c) =>
        string.Join(",", c.Take(3).Select(v => Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
