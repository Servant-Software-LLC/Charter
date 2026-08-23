namespace Charter.Core;

/// <summary>
/// Everything ONE walk of a plan's blocks found: its <c>:::question</c> blocks, Charter's own diagnostics
/// about it, and the document-level facts a gate needs (how many question bodies would not parse, how many
/// unrecognized <c>:::</c> directives there are, which question ids are duplicated, and how the
/// <c>charter-format-version</c> marker read).
/// </summary>
/// <remarks>
/// <para>
/// It exists because TWO things now judge the same plan and must never see different question lists:
/// <see cref="HeadlessRecord"/>, which serializes the forensic record and computes
/// <see cref="HeadlessRecord.NeedsHuman"/>, and <see cref="HandoffGate"/>, which decides whether
/// <c>charter handoff --fail-if-needs-human</c> escalates (Charter #172). Those two predicates are
/// deliberately NOT the same boolean — the gate narrows the <c>target: agent</c> carve-out and counts things
/// the record files as warnings — so the safety comes from sharing the INVENTORY rather than from sharing a
/// verdict. A copied walk is how the two would start disagreeing about which questions a plan even has.
/// </para>
/// <para>
/// <b>Pure in the plan text.</b> No clock, no path, no I/O — the same contract
/// <see cref="HeadlessRecord.Build"/> promises, which is what lets its output be byte-identical across runs.
/// </para>
/// </remarks>
public sealed class PlanInventory
{
    private PlanInventory(
        IReadOnlyList<HeadlessQuestion> questions,
        IReadOnlyList<HeadlessNote> notes,
        int malformedQuestions,
        int unknownDirectives,
        int nestedQuestions,
        IReadOnlyList<string> duplicateQuestionIds,
        VersionMarkerResult versionMarker)
    {
        Questions = questions;
        Notes = notes;
        MalformedQuestions = malformedQuestions;
        UnknownDirectives = unknownDirectives;
        NestedQuestions = nestedQuestions;
        DuplicateQuestionIds = duplicateQuestionIds;
        VersionMarker = versionMarker;
    }

    /// <summary>Every readable <c>:::question</c>, in document order.</summary>
    public IReadOnlyList<HeadlessQuestion> Questions { get; }

    /// <summary>Every diagnostic Charter's own verbs raise about this plan.</summary>
    public IReadOnlyList<HeadlessNote> Notes { get; }

    /// <summary>How many <c>:::question</c> bodies could not be parsed — so their <c>target</c> is unknown.</summary>
    public int MalformedQuestions { get; }

    /// <summary>How many unrecognized <c>:::foo</c> directives the plan carries.</summary>
    public int UnknownDirectives { get; }

    /// <summary>
    /// How many <c>:::question</c> blocks render as live, answerable forms while being invisible to the block
    /// model (Charter #203) — so their decisions are absent from <see cref="Questions"/> entirely.
    /// </summary>
    public int NestedQuestions { get; }

    /// <summary>The distinct <c>:::question</c> ids carried by more than one block, in first-seen order.</summary>
    public IReadOnlyList<string> DuplicateQuestionIds { get; }

    /// <summary>How the plan's <c>charter-format-version</c> frontmatter marker read.</summary>
    public VersionMarkerResult VersionMarker { get; }

    /// <summary>
    /// <c>charter headless</c>'s escalation predicate. Exactly FOUR conditions raise it; see
    /// <see cref="HeadlessRecord.NeedsHuman"/> for why the line is drawn there and nowhere else.
    /// <see cref="HandoffGate"/> draws a DIFFERENT, stricter line, on purpose.
    /// </summary>
    /// <remarks>
    /// <b>The fourth term is <see cref="NestedQuestions"/> (Charter #203)</b>, and it mirrors
    /// <see cref="MalformedQuestions"/> exactly: both are questions the record cannot list, so both are decisions
    /// it cannot report on. A nested one is the worse of the two — a human is shown a live form and answers it,
    /// while the record certifies that nobody is needed. The record's standing reason for filing
    /// <c>UnknownDirective</c> as a warning ("widening the rule would make the flag almost always true") is a
    /// BASE-RATE argument and does not transfer: a correct plan has zero nested questions, so this term is false
    /// on every healthy document.
    /// </remarks>
    public bool NeedsHuman =>
        Questions.Any(question => question is { Answered: false, Target: "human" })
        || MalformedQuestions > 0
        || NestedQuestions > 0
        || DuplicateQuestionIds.Count > 0;

    /// <summary>Walk <paramref name="markdown"/> once and report everything a judge of it needs.</summary>
    public static PlanInventory Build(string markdown)
    {
        markdown ??= string.Empty;

        var questions = new List<HeadlessQuestion>();
        var notes = new List<HeadlessNote>();

        // The version-marker lint every verb already runs, recorded instead of merely printed.
        var marker = CharterFormat.ValidateVersionMarker(markdown);
        if (marker.Status == VersionMarkerStatus.Missing)
        {
            notes.Add(new HeadlessNote(HeadlessNoteKind.MissingVersionMarker, marker.Message, null));
        }
        else if (marker.Status == VersionMarkerStatus.Unsupported)
        {
            notes.Add(new HeadlessNote(HeadlessNoteKind.UnsupportedVersionMarker, marker.Message, null));
        }

        // The duplicate-id lint, from its one kernel — never re-implemented here.
        var duplicates = QuestionResolution.FindDuplicateQuestionIds(markdown);
        foreach (var duplicateId in duplicates)
        {
            notes.Add(new HeadlessNote(
                HeadlessNoteKind.DuplicateQuestionId,
                $"Two or more :::question blocks share the id '{duplicateId}'. An answer would resolve into "
                    + "every block sharing it, and `charter poll --apply` / `charter resolve` refuse the write.",
                null));
        }

        // One walk of the document, reading its blocks, their start lines and their ASSIGNED anchor ids from
        // the same kernels the renderer and the SourceMap read — so a question's anchorId here is byte-identical
        // to the id the artifact carries and to the key of the record's own sourceMap.
        var walk = PlanWalk.Walk(markdown);
        var malformedQuestions = 0;
        var unknownDirectives = 0;
        foreach (var (kind, rawContent, startLine, anchorId) in walk.Blocks)
        {
            if (kind == BlockKind.Unknown)
            {
                unknownDirectives++;
                notes.Add(new HeadlessNote(
                    HeadlessNoteKind.UnknownDirective,
                    "An unrecognized ::: directive was rendered as a visible unknown-directive block; its body "
                        + "is preserved but nothing interprets it.",
                    startLine));
                continue;
            }

            if (kind != BlockKind.Question)
            {
                continue;
            }

            // A body Charter cannot even locate (a malformed container) and a body that fails the schema are
            // the same fact to a consumer: the question's target is unknown, so it escalates either way.
            var (spec, reason) = ReadQuestion(rawContent);
            if (spec is null)
            {
                malformedQuestions++;
                notes.Add(new HeadlessNote(
                    HeadlessNoteKind.MalformedQuestion,
                    "A :::question body could not be parsed, so its target is unknown: "
                        + (reason ?? "the body is not a valid question."),
                    startLine));
                continue;
            }

            questions.Add(new HeadlessQuestion(
                spec.Id,
                spec.Title,
                QuestionSpec.Token(spec.Mode),
                QuestionSpec.Token(spec.Target),
                spec.Options,
                spec.Answer,
                anchorId,
                startLine,
                spec.Recommended));
        }

        // The nested-directive lint, from the SAME walk — never a second parse (Charter #203).
        var nestedQuestions = AddNestedDirectiveNotes(walk.NestedDirectives, notes);

        // The two lints `handoff` prints that the record had no note kind for (Charter #173). Until they were
        // added, `notes: []` did NOT mean "Charter noticed nothing" — which is precisely the query a post-mortem
        // harness asks of this file, and it was silently wrong.
        AddMissingRecommendationNotes(markdown, questions, notes);
        AddUntrackedDeferralNotes(markdown, notes);

        return new PlanInventory(
            questions, notes, malformedQuestions, unknownDirectives, nestedQuestions, duplicates, marker);
    }

    /// <summary>
    /// Record every live-rendered nested <c>:::</c> directive under its own note kind, and return how many of
    /// them were <c>:::question</c>s — the count that feeds <see cref="NeedsHuman"/> (Charter #203).
    /// </summary>
    /// <remarks>
    /// The note KIND is what carries the tier, which is why there are four of them and why the mapping lives in
    /// <see cref="NestedDirectiveLint.NoteKindFor"/> rather than being re-derived here: <see cref="HandoffGate"/>
    /// switches on <c>note.Kind</c>, so a record and a gate reading different mappings would disagree about the
    /// same plan.
    /// </remarks>
    private static int AddNestedDirectiveNotes(
        IReadOnlyList<NestedDirective> nested, List<HeadlessNote> notes)
    {
        var nestedQuestions = 0;
        foreach (var directive in nested)
        {
            if (directive.Kind == BlockKind.Question)
            {
                nestedQuestions++;
            }

            notes.Add(new HeadlessNote(
                NestedDirectiveLint.NoteKindFor(directive.Kind),
                $"A :::{directive.Directive} directive is nested inside a container that renders its children, "
                    + "so it is DRAWN on the page but is not a block: it has no anchor, no source-map entry and "
                    + "no entry in this record, the flatten emits its body as blockquoted prose, and an answer "
                    + "to it can never be folded back. A ::: directive must be a top-level block.",
                directive.SourceLine));
        }

        return nestedQuestions;
    }

    /// <summary>
    /// The missing-lean lint (Charter #142), from its one kernel. A WARNING, never an escalation: the
    /// deliberate <c>"recommended": null</c> opt-out is already excluded by the lint itself.
    /// </summary>
    private static void AddMissingRecommendationNotes(
        string markdown, IReadOnlyList<HeadlessQuestion> questions, List<HeadlessNote> notes)
    {
        foreach (var id in QuestionResolution.FindQuestionsMissingRecommendation(markdown))
        {
            var line = questions.FirstOrDefault(question => string.Equals(question.Id, id, StringComparison.Ordinal))
                ?.SourceLine;

            notes.Add(new HeadlessNote(
                HeadlessNoteKind.MissingRecommendation,
                $"The open question '{id}' carries no `recommended` key at all, so an escalation on it can say "
                    + "a human must decide while offering nothing to decide with. Add `recommended` (verbatim "
                    + "one of the options), or write `\"recommended\": null` to record a declined lean.",
                line));
        }
    }

    /// <summary>The untracked-deferral lint (Charter #156), from its one kernel. Also a warning.</summary>
    private static void AddUntrackedDeferralNotes(string markdown, List<HeadlessNote> notes)
    {
        foreach (var deferral in DeferralLint.Find(markdown))
        {
            notes.Add(new HeadlessNote(
                HeadlessNoteKind.UntrackedDeferral,
                "Work is deferred here without naming anything that tracks it, so it survives only as a "
                    + "sentence in a document nobody re-reads. Name an issue, ticket or URL.",
                deferral.SourceLine));
        }
    }

    /// <summary>
    /// Parse one <c>:::question</c> container: its <see cref="QuestionSpec"/>, or a null spec plus the reason
    /// it could not be read. Both failure modes — an unlocatable body and a schema-invalid one — collapse here
    /// because they mean the same thing downstream: the question's <c>target</c> is unknown.
    /// </summary>
    private static (QuestionSpec? Spec, string? Reason) ReadQuestion(string rawContent)
    {
        // The ONE question-body parse, shared with the flatten (Charter #172).
        var body = QuestionResolution.QuestionBody(rawContent);
        if (body is null)
        {
            return (null, "the container is not a well-formed :::question.");
        }

        return QuestionSpec.TryParse(body, out var spec, out var error) && spec is not null
            ? (spec, null)
            : (null, error);
    }
}
