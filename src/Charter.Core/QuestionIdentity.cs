using System.Security.Cryptography;
using System.Text;

namespace Charter.Core;

/// <summary>
/// The fingerprint of a <c>:::question</c>'s <b>declared shape</b> — what the reviewer was actually asked. It is
/// to an answer what an anchor is to an annotation: the evidence that lets a later write ask "is this still the
/// same question?" instead of assuming it.
/// </summary>
/// <remarks>
/// <para>
/// Charter #75 item 3. An answer is keyed by the question's <c>id</c>, not by an anchor, so the replaced-plan
/// evidence that quarantines annotations (<c>ReviewSidecar.IsStale</c>) says nothing about it. A plan deleted and
/// re-authored at the same path that happens to reuse an id — <c>db-choice</c>, <c>rollout-scope</c>, ids a
/// <c>charter convert</c> seed will happily regenerate — could therefore fold the OLD document's decision into
/// the new one. Unlike an orphaned annotation that write lands in the plan file: silent, durable, and possibly
/// already shaping a Guardrails DAG.
/// </para>
/// <para>
/// <b>What is fingerprinted, and what deliberately is not.</b> The five declared fields of
/// <see cref="QuestionSpec"/> — id, title, mode, target, and the option list in order — and NOT
/// <see cref="QuestionSpec.Answer"/>. Excluding the answer is load-bearing: applying one answer rewrites its
/// question's body, and if that changed the fingerprint then a second apply of the same answer, or an apply of a
/// sibling answer in the same pass, would look stale. Everything included is something the reviewer read before
/// they chose; if any of it changed, the decision on record was made against a question that no longer exists as
/// asked.
/// </para>
/// </remarks>
public static class QuestionIdentity
{
    // Bumped if the composition below ever changes, so an old fingerprint can never accidentally equal a new one
    // computed a different way. A mismatch is the safe direction anyway (surface, don't apply), but a version
    // line makes the intent explicit rather than incidental.
    private const string FingerprintVersion = "1";

    // Separates the option list's members. U+001F (UNIT SEPARATOR) cannot occur in a JSON string's decoded value
    // by accident, so two different option lists can never render to the same joined text.
    private const char OptionSeparator = '\u001f';

    /// <summary>
    /// The fingerprint of <paramref name="spec"/>'s declared shape: lowercase hex SHA-256, stable across
    /// processes and machines (no runtime hash seed).
    /// </summary>
    public static string Fingerprint(QuestionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var composed = string.Join(
            '\n',
            FingerprintVersion,
            spec.Id,
            spec.Title,
            QuestionSpec.Token(spec.Mode),
            QuestionSpec.Token(spec.Target),
            string.Join(OptionSeparator, spec.Options));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composed))).ToLowerInvariant();
    }

    /// <summary>
    /// The fingerprint of the <c>:::question</c> in <paramref name="markdown"/> carrying
    /// <paramref name="questionId"/>, or <see langword="null"/> when there is no such question or its body is not
    /// a valid question (a malformed block renders as a degraded placeholder and is not something a reviewer
    /// answered through Charter).
    /// </summary>
    /// <remarks>
    /// A <c>null</c> is deliberately "no evidence", never "stale". Every caller treats an absent fingerprint —
    /// here or on the queued answer — as permission to proceed exactly as it did before this existed, so a
    /// pre-upgrade queue, a hand-authored plan, or a question that has simply been deleted can never be the
    /// reason a reviewer's decision is withheld.
    /// </remarks>
    public static string? FingerprintOf(string markdown, string questionId)
    {
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrEmpty(questionId))
        {
            return null;
        }

        foreach (var block in BlockDocument.Parse(markdown).Blocks)
        {
            if (block.Kind != BlockKind.Question)
            {
                continue;
            }

            var body = QuestionResolution.QuestionBody(block.RawContent);
            if (body is null || !QuestionSpec.TryParse(body, out var spec, out _) || spec is null)
            {
                continue;
            }

            if (string.Equals(spec.Id, questionId, StringComparison.Ordinal))
            {
                return Fingerprint(spec);
            }
        }

        return null;
    }
}
