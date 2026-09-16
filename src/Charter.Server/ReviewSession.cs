namespace Charter.Server;

/// <summary>
/// One review session: the source <c>.mdx</c> plan being reviewed, the confined root the server is allowed
/// to serve files from, and the per-session <see cref="CapabilityKey"/> that authorizes requests.
/// </summary>
public sealed class ReviewSession
{
    private ReviewSession(string sourcePath, string root, CapabilityKey key)
    {
        SourcePath = sourcePath;
        Root = root;
        Key = key;
    }

    /// <summary>The source Charter plan (<c>.mdx</c>) under review.</summary>
    public string SourcePath { get; }

    /// <summary>The confined root directory the server may serve files from — nothing outside it.</summary>
    public string Root { get; }

    /// <summary>The per-session capability key a request must present to be served.</summary>
    public CapabilityKey Key { get; }

    /// <summary>
    /// The plan's name as a reviewer would say it: the file name minus <c>.charter.md</c> (or <c>.md</c>), so
    /// <c>39-incremental-delivery.charter.md</c> is <c>39-incremental-delivery</c> (Charter #253). Unique per
    /// directory, which is enough — two servers for same-named plans in different directories already differ by
    /// port.
    /// </summary>
    public string PlanName
    {
        get
        {
            var name = Path.GetFileName(SourcePath);
            foreach (var suffix in new[] { ".charter.md", ".md" })
            {
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^suffix.Length];
                }
            }

            return name;
        }
    }

    /// <summary>
    /// The capability URL printed on the READY LINE: exactly <c>http://127.0.0.1:&lt;port&gt;/?key=&lt;key&gt;</c>,
    /// and nothing after the key — deliberately.
    /// </summary>
    /// <remarks>
    /// This line is a contract agents parse, and nothing specifies HOW they parse it. A parser taking everything
    /// after <c>?key=</c> — as one of Charter's own tests did — reads <c>&amp;plan=…</c> as part of the key and
    /// fails. So the plan name rides the URL a HUMAN opens (<see cref="BrowserUrl"/>) and the page title, never
    /// this line (Charter #253).
    /// </remarks>
    /// <param name="address">The server's keyless loopback address, ending in <c>/</c>.</param>
    public string CapabilityUrl(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return $"{address}?key={Key.Value}";
    }

    /// <summary>
    /// The URL <c>charter review</c> opens in the browser itself: the <see cref="CapabilityUrl"/> with
    /// <c>&amp;plan=&lt;name&gt;</c> after the key, so the address bar, history and a bookmark say which plan this
    /// is. The server authorizes on <c>key</c> alone and ignores <c>plan</c>, so both URLs serve the same page.
    /// </summary>
    /// <param name="address">The server's keyless loopback address, ending in <c>/</c>.</param>
    public string BrowserUrl(Uri address) =>
        CapabilityUrl(address) + "&plan=" + Uri.EscapeDataString(PlanName);

    /// <summary>The served page's tab title: the plan name first, so it survives a narrow tab (Charter #253).</summary>
    public string PageTitle => PlanName + " · Charter review";

    /// <summary>
    /// Create a review session bound to <paramref name="sourcePath"/>: confine the root to that plan's
    /// directory and mint a fresh <see cref="CapabilityKey"/>.
    /// </summary>
    public static ReviewSession Create(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        var fullSource = Path.GetFullPath(sourcePath);
        var root = Path.GetDirectoryName(fullSource);
        if (string.IsNullOrEmpty(root))
        {
            root = Directory.GetCurrentDirectory();
        }

        return new ReviewSession(fullSource, root, CapabilityKey.Create());
    }
}
