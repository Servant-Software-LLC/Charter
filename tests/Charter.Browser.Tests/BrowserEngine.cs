using System;
using Microsoft.Playwright;

namespace Charter.Browser.Tests;

/// <summary>
/// Which browser engine the browser tests drive, selected by the <c>CHARTER_BROWSER</c> environment variable
/// and defaulting to Chromium.
/// <para>
/// <b>Why this exists (#110).</b> Every browser test was Chromium-only — 40 references to it, none to WebKit —
/// while <c>charter review</c> hands the URL to the platform's DEFAULT browser
/// (<c>Process.Start("open", url)</c> on macOS). On a stock Mac that is Safari, so on a first-class target the
/// engine a reviewer actually gets was the one engine never exercised. The SDK leans on precisely the APIs
/// where WebKit diverges most — <c>getSelection</c>/<c>createRange</c> for text-range anchoring,
/// <c>getBoundingClientRect</c> for hit-testing (including inside rendered Mermaid SVG), and
/// <c>EventSource</c> for live reload and the review-log re-hydrate that agent replies ride. Every one of
/// those fails SILENTLY: a comment lands on the wrong text, or the page stops updating, and nothing throws.
/// </para>
/// <para>
/// An environment variable rather than a <c>[Theory]</c> on purpose: it leaves all twelve review-loop test
/// bodies untouched and asserting behaviour rather than parameterisation, and CI gets full coverage by running
/// the suite twice. The cost is that a local run exercises one engine at a time, which is the right trade for
/// tests whose value is in what they assert, not in how they are enumerated.
/// </para>
/// </summary>
public static class BrowserEngine
{
    /// <summary>The engine name in force, lowercased. <c>chromium</c> unless <c>CHARTER_BROWSER</c> says otherwise.</summary>
    public static string Name =>
        (Environment.GetEnvironmentVariable("CHARTER_BROWSER") ?? "chromium").Trim().ToLowerInvariant();

    /// <summary>
    /// The Playwright browser type for <see cref="Name"/>. An unrecognised value falls back to Chromium rather
    /// than throwing — a typo in CI configuration should degrade to the covered engine, never turn the whole
    /// browser suite red for a reason unrelated to the product.
    /// </summary>
    public static IBrowserType For(IPlaywright playwright)
    {
        ArgumentNullException.ThrowIfNull(playwright);

        return Name switch
        {
            "webkit" => playwright.Webkit,
            "firefox" => playwright.Firefox,
            _ => playwright.Chromium,
        };
    }

    /// <summary>
    /// True when the engine in force is Chromium-family, i.e. when Chromium-specific launch arguments such as
    /// <c>--hide-scrollbars</c> mean anything. WebKit and Firefox reject unknown Chromium flags, so a caller
    /// must not pass them blindly.
    /// </summary>
    public static bool IsChromium => Name is not ("webkit" or "firefox");
}
