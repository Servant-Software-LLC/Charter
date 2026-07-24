namespace Charter.Core;

/// <summary>
/// Thrown by <see cref="QuestionResolution.ApplyToFile"/> when the plan carries two or more
/// <c>:::question</c> blocks that share an <c>id</c>. Applying an answer would splice it into EVERY block
/// carrying that id (a silent double-write), so the apply is refused instead. The offending ids are exposed
/// on <see cref="Ids"/> and named in <see cref="System.Exception.Message"/> so the caller can report a clear,
/// actionable error and the reviewer can give each question a unique id.
/// </summary>
public sealed class DuplicateQuestionIdException : InvalidOperationException
{
    /// <summary>Create the exception for the duplicated question <paramref name="ids"/>.</summary>
    public DuplicateQuestionIdException(IReadOnlyList<string> ids)
        : base(BuildMessage(ids))
    {
        Ids = ids;
    }

    /// <summary>The distinct question ids shared by more than one <c>:::question</c> block, in first-seen order.</summary>
    public IReadOnlyList<string> Ids { get; }

    private static string BuildMessage(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return "refusing to apply answers: the plan has duplicate :::question id(s): "
            + string.Join(", ", ids)
            + ". Give each question a unique id so an answer resolves exactly one block.";
    }
}
