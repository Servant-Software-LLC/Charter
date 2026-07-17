namespace Charter.Core;

/// <summary>
/// Renders Charter markdown to one portable HTML artifact, wrapping each block's element with its
/// content-derived stable <see cref="Block.Id"/> so a human annotation on the rendered HTML can be
/// round-tripped back to the markdown source via the <see cref="SourceMap"/>.
/// </summary>
/// <remarks>STUB (TDD red). The real Markdig-based rendering lands in a later task.</remarks>
public static class CharterRenderer
{
    /// <summary>
    /// Render <paramref name="markdown"/> to portable HTML. Each top-level block's element carries an
    /// <c>id</c> attribute equal to that block's stable <see cref="Block.Id"/>. NOT YET IMPLEMENTED.
    /// </summary>
    public static string Render(string markdown) => throw new NotImplementedException();
}
