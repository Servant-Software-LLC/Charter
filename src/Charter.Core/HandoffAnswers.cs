using System.Text;
using System.Text.Json;

namespace Charter.Core;

/// <summary>
/// A parsed <c>--answers</c> file AND the hash of the exact text it was parsed from (Charter #187).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the two travel together.</b> The chain-of-custody manifest records <c>answersSha256</c> as the value
/// that reproduces every decision it marks <c>answerSource: "answers-file"</c>. If the dictionary and the hash
/// were passed to the manifest separately, two things could go wrong and neither is visible in the output: a
/// caller reads the file twice (parse, then hash) and the file changes in between — a TOCTOU whose only symptom
/// is a manifest that certifies a resolution nobody ran — or a caller simply hands over a hash of some OTHER
/// text. One type holding both, produced by one function from one string, makes both unspellable.
/// </para>
/// <para>
/// <b>The hash recipe is <see cref="PlanHash.Sha256Hex"/>, unchanged and deliberately.</b> All three hashes in
/// the manifest use it, because a manifest whose fields mean different things is worse than one with fewer
/// fields. Read <see cref="PlanHash"/>'s remarks before comparing any of them to <c>sha256sum</c>: they are
/// hashes of the DECODED text, not of the file's bytes.
/// </para>
/// </remarks>
/// <param name="Values">Question id → the supplied answer value(s), the shape the merge resolves against.</param>
/// <param name="Sha256">
/// The hash of the exact JSON text <see cref="Read"/> parsed. Never null: an answers file that was READ has a
/// hash. "No <c>--answers</c> was passed" is spelled by having no <see cref="HandoffAnswers"/> at all.
/// </param>
public sealed record HandoffAnswers(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Values,
    string Sha256)
{
    /// <summary>
    /// Parse an answers file's text — a flat object mapping question id to an array of answer value strings —
    /// and hash that same text.
    /// </summary>
    /// <remarks>
    /// A JSON <c>null</c> value becomes an EMPTY array rather than being dropped, so both spellings land on the
    /// one rule that judges them (<c>AnswerRules</c>: an empty value is an error, not an erasure — Charter
    /// #186). Dropping the key here would silently turn an explicit erasure attempt into an omission, which is
    /// the one thing omission is defined NOT to mean.
    /// </remarks>
    /// <exception cref="JsonException">The text is not a flat id → string-array object.</exception>
    public static HandoffAnswers Read(string json)
    {
        json ??= string.Empty;

        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]?>>(json)
            ?? new Dictionary<string, string[]?>(StringComparer.Ordinal);

        var values = new Dictionary<string, IReadOnlyList<string>>(raw.Count, StringComparer.Ordinal);
        foreach (var pair in raw)
        {
            values[pair.Key] = pair.Value ?? Array.Empty<string>();
        }

        return new HandoffAnswers(values, PlanHash.Sha256Hex(json));
    }

    /// <summary>
    /// Decode an answers file's bytes exactly as <c>File.ReadAllText</c> would — UTF-8 by default, with a byte
    /// order mark honoured and stripped.
    /// </summary>
    /// <remarks>
    /// It takes BYTES rather than a path so the encoding sniff (<see cref="EncodingWarning"/>), the decode and
    /// the hash all describe ONE snapshot of the file. Reading the file a second time to sniff it would
    /// reintroduce the very TOCTOU this type exists to close.
    /// </remarks>
    public static string Decode(byte[] fileBytes)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        using var stream = new MemoryStream(fileBytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The stderr sentence to print when <paramref name="fileBytes"/> is not BOM-less UTF-8, or null when it
    /// is. ASCII only, like every other line Charter writes to stderr.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because of one very specific, very unfixable-looking pipeline failure: a generator that
    /// writes <c>answers.json</c> from <b>Windows PowerShell 5.1</b> produces UTF-16LE, Charter decodes it
    /// perfectly, and every subsequent comparison of <c>answersSha256</c> against <c>sha256sum answers.json</c>
    /// mismatches forever, with nothing anywhere to explain why. The file is honest and the run is correct;
    /// only the hash's relationship to <c>sha256sum</c> is surprising — so this WARNS and never rejects.
    /// </para>
    /// <para>
    /// A BOM-less file that is not valid UTF-8 is warned about too, for the same reason: the decode replaces
    /// what it cannot read, so the hash covers text that is not what the bytes say.
    /// </para>
    /// </remarks>
    public static string? EncodingWarning(byte[] fileBytes)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);

        var bom = PlanHash.ByteOrderMarkName(fileBytes);
        if (bom is not null)
        {
            return $"the answers file begins with a {bom} byte order mark. Charter decodes it correctly, but "
                + "its answersSha256 hashes the DECODED text re-encoded as UTF-8, so it will NOT equal "
                + "`sha256sum` of this file's bytes. Write the file as BOM-less UTF-8 if you intend to compare "
                + "the two (Windows PowerShell 5.1's `>` and Out-File default to UTF-16LE).";
        }

        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(fileBytes);
        }
        catch (DecoderFallbackException)
        {
            return "the answers file is not valid UTF-8 and carries no byte order mark. Charter decoded it "
                + "with replacement characters, so its answersSha256 covers text that is not what the bytes "
                + "say, and will NOT equal `sha256sum` of this file. Write the file as BOM-less UTF-8.";
        }

        return null;
    }
}
