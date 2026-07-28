using Charter.Core;

namespace Charter.Server;

/// <summary>
/// The one kernel that binds a queued <see cref="Annotation"/> to a markdown source line — always against the
/// plan AS IT IS NOW, never against a snapshot taken when the reviewer wrote the note (Charter #49).
/// </summary>
/// <remarks>
/// <para>
/// An annotation's line is resolved at SUBMIT time and stored, but that value goes stale the instant anything
/// above the block changes line count — the drafting agent's own edit, or <c>charter poll --apply</c> folding an
/// answer into a <c>:::question</c> above it. Handing the agent a stale line is worse than handing it none: it
/// edits the wrong block, confidently. So every path that REPORTS an annotation re-resolves through here first —
/// the server's <c>/api/poll</c> drain, <c>charter poll --apply</c> again after its own write, and
/// <c>GET /api/annotations</c>, the panel's list.
/// </para>
/// <para>
/// That last one is Charter #78. The list route used to emit the stored submit-time pair, so the same annotation
/// at the same moment read <c>sourceLine: 1, anchorStatus: "resolved"</c> from the panel route and
/// <c>null, "orphaned"</c> from the drain. One field cannot mean two things depending on who asked; routing both
/// through this kernel is what makes them structurally incapable of disagreeing.
/// </para>
/// <para>
/// An anchor that no longer resolves yields <c>SourceLine = null</c>, which surfaces as
/// <see cref="AnchorStatus.Orphaned"/> on the wire: an explicit "the block you commented on has changed",
/// with the annotation's own quote/nodeId/note as the recovery hints. That is a detectable, recoverable
/// outcome — unlike a confidently wrong line number.
/// </para>
/// </remarks>
public static class AnchorResolution
{
    /// <summary>
    /// Re-resolve every annotation's <see cref="Annotation.AnchorId"/> against <paramref name="markdown"/>,
    /// returning copies whose <see cref="Annotation.SourceLine"/> is the anchor's CURRENT 1-based line (or
    /// <c>null</c> when it no longer resolves). Everything else about each annotation is untouched. One
    /// <see cref="SourceMap"/> is built for the whole batch, so a drain costs one parse regardless of size.
    /// </summary>
    public static IReadOnlyList<Annotation> Resolve(IReadOnlyList<Annotation> annotations, string markdown)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        if (annotations.Count == 0)
        {
            return annotations;
        }

        var map = SourceMap.Build(markdown ?? string.Empty);
        var resolved = new Annotation[annotations.Count];
        for (var i = 0; i < annotations.Count; i++)
        {
            resolved[i] = annotations[i] with { SourceLine = map.LineForAnchor(annotations[i].AnchorId) };
        }

        return resolved;
    }

    /// <summary>
    /// <see cref="Resolve"/> against the plan file at <paramref name="planPath"/>. When the plan cannot be read
    /// the batch resolves against an EMPTY document, so every annotation drains as an explicit orphan rather
    /// than carrying an unverifiable line: an unreadable plan means the lines cannot be trusted, and saying so
    /// is the honest answer. Nothing is ever dropped — the notes and their recovery hints travel either way.
    /// </summary>
    /// <remarks>
    /// Deliberately takes NO cancellation token. It sits between the destructive <c>Drain()</c> and the write
    /// that delivers (or requeues) the batch, so an exception here would strand the drained annotations with
    /// neither. Reading one local plan file is cheap and bounded; every failure mode it has is caught and
    /// answered with "orphaned", which keeps that window exception-free.
    /// </remarks>
    public static async Task<IReadOnlyList<Annotation>> ResolveAgainstFileAsync(
        IReadOnlyList<Annotation> annotations, string planPath)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        if (annotations.Count == 0)
        {
            return annotations;
        }

        return Resolve(annotations, await ReadOrEmptyAsync(planPath).ConfigureAwait(false));
    }

    private static async Task<string> ReadOrEmptyAsync(string planPath)
    {
        try
        {
            return await File.ReadAllTextAsync(planPath).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
        catch (ArgumentException)
        {
            // A missing/blank path — the session's source is unusable, so the lines are unverifiable.
            return string.Empty;
        }
    }
}
