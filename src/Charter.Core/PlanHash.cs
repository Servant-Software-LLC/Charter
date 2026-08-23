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
}
