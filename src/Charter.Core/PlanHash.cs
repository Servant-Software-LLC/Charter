using System.Security.Cryptography;
using System.Text;

namespace Charter.Core;

/// <summary>
/// The ONE way Charter identifies a revision of any file in this pipeline: the SHA-256 of that file's DECODED
/// text, re-encoded as UTF-8, lower-case hex.
/// </summary>
/// <remarks>
/// <para>
/// It began as the plan's hash and is now the recipe for all of them, because a chain-of-custody artifact whose
/// fields mean different things is worse than one with fewer fields. Four values use it and the whole point is
/// that they agree wherever they describe the same bytes: <see cref="HeadlessRecord"/>'s <c>planSha256</c>, the
/// trailing <c>&lt;!-- charter: plan-sha256=… --&gt;</c> stamp <see cref="HandoffMarkdown.Emit"/> appends, and
/// <see cref="HandoffManifest"/>'s <c>planSha256</c> / <c>answersSha256</c> / <c>handoffSha256</c>
/// (Charter #172/#186/#187). If any two hashed different text, the joins between them would be silently
/// meaningless — which is worse than not having them.
/// </para>
/// <para>
/// <b>The recipe is not what a reader assumes, so it is written out literally.</b> Charter reads a file with
/// <c>File.ReadAllText</c>, which <b>strips a UTF-8 byte order mark</b> and <b>decodes UTF-16/UTF-32 per the
/// mark</b>; this function then hashes the <b>UTF-8 re-encoding of that decoded string</b>. Consequence, and it
/// is the one that costs a debugging session: <b>none of these hashes equals <c>sha256sum</c> of the file's
/// bytes unless the file is BOM-less UTF-8.</b> A pipeline generating <c>answers.json</c> from Windows
/// PowerShell 5.1 gets UTF-16LE and a permanent, unexplainable mismatch — which is why
/// <see cref="HandoffAnswers.EncodingWarning"/> exists and why <c>charter handoff</c> says so on stderr.
/// </para>
/// <para>
/// <b>The text is hashed exactly as read — line endings included.</b> That is deliberate and is the opposite of
/// <c>ReviewBaseStatus</c>, which hashes a plan in every newline form so a mixed Windows/Linux team does not
/// read "changed" on every comment at one revision. There the question is "did a human edit this?"; here it is
/// "are these two files byte-for-byte the same revision?", and a CRLF↔LF rewrite genuinely produces a different
/// file for a consumer diffing bytes. It is also why the manifest documents <c>handoffSha256</c> as ADVISORY:
/// a mismatch means tampering OR a line-ending rewrite in transit, and the hash alone cannot tell you which.
/// </para>
/// </remarks>
public static class PlanHash
{
    /// <summary>The lower-case hex SHA-256 of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)))
            .ToLowerInvariant();

    /// <summary>
    /// The name of the byte order mark <paramref name="bytes"/> begins with (<c>UTF-8</c>, <c>UTF-16LE</c>,
    /// <c>UTF-16BE</c>, <c>UTF-32LE</c>, <c>UTF-32BE</c>), or null when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It lives here rather than beside either caller because a byte order mark is exactly what makes the
    /// recipe above diverge from <c>sha256sum</c> — detecting one is part of explaining this hash, not part of
    /// reading any particular file. Two callers ask, and they draw OPPOSITE conclusions from the same answer,
    /// which is why only the DETECTION is shared and neither message is:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>--answers</c> (<see cref="HandoffAnswers.EncodingWarning"/>): a human chose that
    ///     encoding, the file decodes correctly and the run is honest — only the hash's relationship to
    ///     <c>sha256sum</c> is surprising. A <b>warning</b>, with "write it as BOM-less UTF-8" as the
    ///     remedy.</description></item>
    ///   <item><description>the <b>handoff</b> (<c>charter verify</c>): Charter WROTE that file, as BOM-less
    ///     UTF-8. A mark on it means somebody rewrote it, which is <b>evidence</b>, not an excuse — and telling
    ///     the user to rewrite the artifact would be exactly the wrong remedy.</description></item>
    /// </list>
    /// </remarks>
    public static string? ByteOrderMarkName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return "UTF-8";
        }

        // UTF-32 is tested BEFORE UTF-16: a UTF-32LE mark starts with the same two bytes as a UTF-16LE one.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return "UTF-32LE";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return "UTF-32BE";
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return "UTF-16LE";
        }

        return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? "UTF-16BE" : null;
    }
}
