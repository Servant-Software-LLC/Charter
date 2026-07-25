using System.Text;

namespace Charter.Core;

/// <summary>
/// The single shared HTML document shell for every Charter render surface — <c>render</c>, <c>review</c>, and
/// <c>export</c> all wrap their rendered block body here, so all three emit a COMPLETE, STYLED document
/// (<c>&lt;!doctype&gt;</c> / <c>&lt;html&gt;</c> / <c>&lt;head&gt;</c> / <c>&lt;body&gt;</c>) with the bundled
/// stylesheet inlined once — never a bare, unstyled fragment (Charter #38). The head carries a UTF-8 charset,
/// a responsive viewport, an OPTIONAL Content-Security-Policy meta, and the bundled CSS
/// (<see cref="CharterStyles.Css"/>) as a single inline <c>&lt;style&gt;</c>.
/// </summary>
/// <remarks>
/// The CSP is path-specific and passed in by the caller, NOT baked here: <c>render</c> and <c>review</c> pass
/// <c>null</c> (the review server supplies the served-page CSP as an HTTP header; a plain <c>render</c> file is
/// a local trusted artifact), while <c>export</c> passes its strict offline CSP as a meta so the shipped file
/// carries it inline. Keeping the shell common but the CSP a parameter is what reconciles the two policies
/// without forking the shell. The stylesheet is inline (no external <c>&lt;link&gt;</c>) so the artifact stays
/// offline/portable (invariant 1) and satisfies <c>style-src 'unsafe-inline'</c>.
/// </remarks>
public static class CharterDocument
{
    /// <summary>
    /// Wrap <paramref name="body"/> (rendered block HTML, plus any inlined Mermaid runtime) in the complete
    /// shell. When <paramref name="cspMeta"/> is non-empty it is emitted as a
    /// <c>&lt;meta http-equiv="Content-Security-Policy"&gt;</c> inside the head, before the stylesheet.
    /// </summary>
    public static string Wrap(string body, string? cspMeta)
    {
        body ??= string.Empty;

        var sb = new StringBuilder(body.Length + CharterStyles.Css.Length + 512);
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\" />\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n");
        if (!string.IsNullOrEmpty(cspMeta))
        {
            sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(cspMeta).Append("\" />\n");
        }

        sb.Append("<style>\n").Append(CharterStyles.Css).Append("\n</style>\n");
        sb.Append("</head>\n<body>\n");
        sb.Append(body);
        sb.Append("\n</body>\n</html>\n");
        return sb.ToString();
    }
}
