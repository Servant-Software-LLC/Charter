using System.Security.Cryptography;
using System.Text;

namespace Charter.Core;

/// <summary>
/// The ONE way Charter identifies a revision of a plan: the SHA-256 of the plan's UTF-8 text exactly as it
/// was read, lower-case hex.
/// </summary>
/// <remarks>
/// <para>
/// Two artifacts carry it, and the whole point is that they carry the SAME value for the same file:
/// <see cref="HeadlessRecord"/>'s <c>planSha256</c>, which lets a human in hindsight prove which revision the
/// artifact beside it was rendered from, and the trailing <c>&lt;!-- charter: plan-sha256=… --&gt;</c> stamp
/// <see cref="HandoffMarkdown.Emit"/> appends, which lets a downstream prove the plan Charter recorded is the
/// plan it consumed (Charter #172/#187). If these two hashed different text the join would be silently
/// meaningless, which is why the function is shared rather than copied.
/// </para>
/// <para>
/// <b>The text is hashed exactly as read — line endings included.</b> That is deliberate here and is the
/// opposite of <c>ReviewBaseStatus</c>, which hashes a plan in every newline form so a mixed Windows/Linux
/// team does not read "changed" on every comment at one revision. There the question is "did a human edit
/// this?"; here it is "are these two files byte-for-byte the same revision?", and a CRLF↔LF rewrite genuinely
/// produces a different file for a consumer diffing bytes.
/// </para>
/// </remarks>
public static class PlanHash
{
    /// <summary>The lower-case hex SHA-256 of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)))
            .ToLowerInvariant();
}
