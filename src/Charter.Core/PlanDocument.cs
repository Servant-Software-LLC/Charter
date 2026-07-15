namespace Charter.Core;

/// <summary>
/// A parsed Charter plan: the block-structured, reviewable deliverable an agent authors and a
/// human annotates in place, before Guardrails breaks it down into an executable task DAG.
///
/// This is a scaffold placeholder. The real block model, MDX parsing, annotation anchoring, and
/// the local review server land in later milestones — see the roadmap in README.md.
/// </summary>
public sealed record PlanDocument(string Title, IReadOnlyList<string> Blocks)
{
    /// <summary>An empty plan with no blocks — the starting point before any authoring.</summary>
    public static PlanDocument Empty { get; } = new("Untitled plan", Array.Empty<string>());
}
