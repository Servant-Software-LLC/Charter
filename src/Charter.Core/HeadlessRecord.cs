using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Charter.Core;

/// <summary>
/// What the interactive review would have put in front of a human, made durable — the forensic half of the
/// unattended (headless) path, Charter #7.
/// </summary>
/// <remarks>
/// <para>
/// <c>charter export</c> already produced the portable, SDK-free artifact and exited without a server, so the
/// artifact was never the gap. The gap was everything the review server holds only in MEMORY: the anchor →
/// markdown-line source map, the decisions a <c>:::question</c> would have elicited, and the diagnostics the
/// verbs print to stderr (which an agent-launched run may never show a human). Nothing wrote any of it to
/// disk. If an unattended run went bad, a human opening the artifact in hindsight could see WHAT the agent
/// produced but not trace a rendered element back to the markdown line it came from, nor tell which decisions
/// were never made.
/// </para>
/// <para>
/// <b>The record is a pure function of the plan text and the tool version.</b> No clock, no random, no local
/// path — so two runs over the same plan are byte-identical (a harness can diff them, a reviewer can
/// reproduce one) and the file is as safe to collect and pass on as the artifact beside it. The "when" is the
/// file's own mtime; embedding a timestamp would buy nothing and cost determinism.
/// </para>
/// <para>
/// <b><see cref="NeedsHuman"/> is the single escalation fact.</b> The CLI's exit code reads THIS property, and
/// the same property is serialized into the file, so the process exit code and the record on disk can never
/// disagree — the discipline that keeps <c>anchorStatus</c> and <c>sourceLine</c> in agreement on the poll
/// wire.
/// </para>
/// </remarks>
public sealed class HeadlessRecord
{
    /// <summary>
    /// The record's schema version, bumped when the on-disk shape changes incompatibly. A consumer that does
    /// not recognise it should refuse to interpret the file rather than guess.
    /// </summary>
    public const int Schema = 1;

    private readonly JsonObject _json;

    private HeadlessRecord(
        JsonObject json,
        bool needsHuman,
        IReadOnlyList<HeadlessQuestion> questions,
        IReadOnlyList<HeadlessNote> notes)
    {
        _json = json;
        NeedsHuman = needsHuman;
        Questions = questions;
        Notes = notes;
    }

    /// <summary>
    /// True when the plan carries something a human must decide or fix before an unattended crew should
    /// proceed. Exactly three conditions raise it, and nothing else does:
    /// <list type="number">
    ///   <item><description>an open <c>:::question</c> whose <c>target</c> is <c>human</c> — the decision
    ///     interactive review existed to elicit, and nobody was there to make it;</description></item>
    ///   <item><description>a <c>:::question</c> whose body Charter could not parse — its <c>target</c> is
    ///     UNKNOWN, and assuming "agent" would let a crew sail past a decision nobody can even read;</description></item>
    ///   <item><description>duplicate <c>:::question</c> ids — an answer would resolve into every block
    ///     sharing the id, and both <c>poll --apply</c> and <c>resolve</c> refuse the write, so the plan
    ///     cannot be settled unattended at all.</description></item>
    /// </list>
    /// A missing/unsupported format-version marker and an unknown <c>:::foo</c> directive are deliberately NOT
    /// among them: every other verb treats those as warnings that never change an exit code, and drawing the
    /// line anywhere else would make this flag almost always true and therefore worthless.
    /// </summary>
    public bool NeedsHuman { get; }

    /// <summary>Every <c>:::question</c> the plan carries, in document order.</summary>
    public IReadOnlyList<HeadlessQuestion> Questions { get; }

    /// <summary>Everything Charter itself noticed about the plan, in document order.</summary>
    public IReadOnlyList<HeadlessNote> Notes { get; }

    /// <summary>
    /// Build the record for <paramref name="markdown"/>.
    /// </summary>
    /// <param name="markdown">The plan's markdown, exactly as Charter read it.</param>
    /// <param name="planFileName">The plan's BARE file name — never a path (see <see cref="NoPathsAllowed"/>).</param>
    /// <param name="artifactFileName">The exported artifact's BARE file name — never a path.</param>
    /// <param name="charterVersion">
    /// The tool version that produced this. Recorded because a forensic file that does not name its producer
    /// is a trap: reading a page rendered by one binary against expectations taken from another is how a
    /// long-fixed defect gets re-filed as live (Charter #69).
    /// </param>
    public static HeadlessRecord Build(
        string markdown, string planFileName, string artifactFileName, string charterVersion)
    {
        markdown ??= string.Empty;
        RequireBareFileName(planFileName, nameof(planFileName));
        RequireBareFileName(artifactFileName, nameof(artifactFileName));
        ArgumentException.ThrowIfNullOrEmpty(charterVersion);

        var questions = new List<HeadlessQuestion>();
        var notes = new List<HeadlessNote>();

        // The version-marker lint every other verb already runs, recorded instead of merely printed.
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
        // to the id the artifact carries and to the key of this record's own sourceMap.
        var malformedQuestions = 0;
        foreach (var (kind, rawContent, startLine, anchorId) in PlanWalk.Blocks(markdown))
        {
            if (kind == BlockKind.Unknown)
            {
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
                startLine));
        }

        var needsHuman =
            questions.Any(q => q is { Answered: false, Target: "human" })
            || malformedQuestions > 0
            || duplicates.Count > 0;

        return new HeadlessRecord(
            Serialize(markdown, planFileName, artifactFileName, charterVersion, needsHuman, questions, notes),
            needsHuman,
            questions,
            notes);
    }

    /// <summary>
    /// Parse one <c>:::question</c> container: its <see cref="QuestionSpec"/>, or a null spec plus the reason
    /// it could not be read. Both failure modes — an unlocatable body and a schema-invalid one — collapse here
    /// because they mean the same thing downstream: the question's <c>target</c> is unknown.
    /// </summary>
    private static (QuestionSpec? Spec, string? Reason) ReadQuestion(string rawContent)
    {
        var body = QuestionResolution.QuestionBody(rawContent);
        if (body is null)
        {
            return (null, "the container is not a well-formed :::question.");
        }

        return QuestionSpec.TryParse(body, out var spec, out var error) && spec is not null
            ? (spec, null)
            : (null, error);
    }

    /// <summary>
    /// The record as indented JSON — deterministic in the plan text and the tool version, so repeated builds
    /// of the same plan are byte-identical.
    /// </summary>
    /// <remarks>
    /// Written through a <see cref="Utf8JsonWriter"/> rather than
    /// <c>JsonNode.ToJsonString(JsonSerializerOptions)</c>: that overload needs a <c>TypeInfoResolver</c> on
    /// any custom options and throws without one. <see cref="Utf8JsonWriter"/> also indents with a bare LF on
    /// every platform, so the file's bytes do not depend on the OS that produced it.
    /// </remarks>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            _json.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static JsonObject Serialize(
        string markdown,
        string planFileName,
        string artifactFileName,
        string charterVersion,
        bool needsHuman,
        IReadOnlyList<HeadlessQuestion> questions,
        IReadOnlyList<HeadlessNote> notes)
    {
        var questionArray = new JsonArray();
        foreach (var question in questions)
        {
            questionArray.Add(new JsonObject
            {
                ["id"] = question.Id,
                ["title"] = question.Title,
                ["mode"] = question.Mode,
                ["target"] = question.Target,
                ["options"] = ToJsonArray(question.Options),
                ["answered"] = question.Answered,
                ["answer"] = ToJsonArray(question.Answer),
                ["anchorId"] = question.AnchorId,
                ["sourceLine"] = question.SourceLine,
            });
        }

        var noteArray = new JsonArray();
        foreach (var note in notes)
        {
            noteArray.Add(new JsonObject
            {
                ["kind"] = HeadlessNote.Token(note.Kind),
                ["message"] = note.Message,
                ["sourceLine"] = note.SourceLine is { } line ? JsonValue.Create(line) : null,
            });
        }

        // The anchor → markdown-line map the live server resolves annotations through, from the SAME
        // SourceMap the server uses. Emitted in ascending source-line order so the file reads top-to-bottom
        // like the plan; every slot has its own line, so the order is total and deterministic.
        var map = SourceMap.Build(markdown);
        var sourceMap = new JsonObject();
        foreach (var anchor in map.Anchors
            .Select(a => (Anchor: a, Line: map.LineForAnchor(a) ?? 0))
            .OrderBy(entry => entry.Line)
            .ThenBy(entry => entry.Anchor, StringComparer.Ordinal))
        {
            sourceMap[anchor.Anchor] = anchor.Line;
        }

        return new JsonObject
        {
            ["schema"] = Schema,
            ["charterVersion"] = charterVersion,
            ["plan"] = planFileName,
            ["planSha256"] = Sha256Hex(markdown),
            ["artifact"] = artifactFileName,
            ["needsHuman"] = needsHuman,
            ["questions"] = questionArray,
            ["notes"] = noteArray,
            ["sourceMap"] = sourceMap,
        };
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    /// <summary>
    /// The SHA-256 of the plan's UTF-8 text as Charter read it, lower-case hex. This is what lets a human in
    /// hindsight prove WHICH revision of the plan the artifact beside it was rendered from — the artifact
    /// alone cannot say.
    /// </summary>
    private static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Why a path is refused where a name is required: the record's contract is that it carries no local
    /// filesystem path (exactly like the exported artifact), so it is safe to collect and hand on. Accepting a
    /// path and silently trimming it would make the guarantee depend on the caller remembering to trim.
    /// </summary>
    private const string NoPathsAllowed =
        "must be a bare file name, not a path — the headless record deliberately carries no local path.";

    private static void RequireBareFileName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);

        // Checked against BOTH separators explicitly rather than via Path.GetFileName, which is
        // platform-dependent: on Unix a backslash is an ordinary filename character, so a Windows path would
        // sail through unnoticed there.
        if (value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value is "." or "..")
        {
            throw new ArgumentException($"'{parameterName}' {NoPathsAllowed}", parameterName);
        }
    }
}

/// <summary>One <c>:::question</c> as the record preserves it — the form a human would have filled in.</summary>
/// <param name="Id">The question's document-unique id.</param>
/// <param name="Title">The question put to the reviewer.</param>
/// <param name="Mode">The authoring-format mode token (<c>single</c>/<c>multi</c>/<c>free-text</c>/<c>bool</c>/<c>number</c>).</param>
/// <param name="Target">The authoring-format target token (<c>human</c>/<c>agent</c>).</param>
/// <param name="Options">
/// The declared options. Kept even for an answered question because the REJECTED option is the rationale a
/// downstream guardrail can be written against — the same reason the flattened handoff emits them.
/// </param>
/// <param name="Answer">The value(s) that settled it, or empty when the question is still open.</param>
/// <param name="AnchorId">
/// The block's assigned anchor — the id the rendered artifact carries and a key of this record's
/// <c>sourceMap</c>, so a reader can go artifact element → anchor → markdown line.
/// </param>
/// <param name="SourceLine">The 1-based markdown line the block starts at.</param>
public sealed record HeadlessQuestion(
    string Id,
    string Title,
    string Mode,
    string Target,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> Answer,
    string AnchorId,
    int SourceLine)
{
    /// <summary>True when the question carries a non-empty answer — the on-disk resolved marker.</summary>
    public bool Answered => Answer.Count > 0;
}

/// <summary>What Charter itself noticed about the plan — the "machine-generated review notes" of Charter #7.</summary>
/// <remarks>
/// Deliberately NOT auto-generated human-style comments: the issue puts those out of scope, and synthesising
/// review prose is an agent's job, not Charter's. These are the diagnostics Charter's own verbs already emit
/// to stderr, which an agent-launched run may never show a human — recorded so they survive the run.
/// </remarks>
public enum HeadlessNoteKind
{
    /// <summary>The plan carries no <c>charter-format-version</c> marker.</summary>
    MissingVersionMarker,

    /// <summary>The marker is present but its value is outside the supported range.</summary>
    UnsupportedVersionMarker,

    /// <summary>Two or more <c>:::question</c> blocks share an id.</summary>
    DuplicateQuestionId,

    /// <summary>A <c>:::question</c> body could not be parsed, so its target is unknown.</summary>
    MalformedQuestion,

    /// <summary>An unrecognized <c>:::foo</c> directive: rendered visibly, but nothing interprets it.</summary>
    UnknownDirective,
}

/// <summary>One recorded diagnostic.</summary>
/// <param name="Kind">What was noticed.</param>
/// <param name="Message">A human-readable explanation. Deterministic — it embeds ids, never paths or clocks.</param>
/// <param name="SourceLine">The 1-based markdown line it is about, or null for a document-wide note.</param>
public sealed record HeadlessNote(HeadlessNoteKind Kind, string Message, int? SourceLine)
{
    /// <summary>
    /// The wire token for <paramref name="kind"/>. Hyphenated, matching the token style the annotation wire
    /// already uses (<c>text-range</c>, <c>diagram-node</c>) rather than camelCase.
    /// </summary>
    public static string Token(HeadlessNoteKind kind) => kind switch
    {
        HeadlessNoteKind.MissingVersionMarker => "missing-version-marker",
        HeadlessNoteKind.UnsupportedVersionMarker => "unsupported-version-marker",
        HeadlessNoteKind.DuplicateQuestionId => "duplicate-question-id",
        HeadlessNoteKind.MalformedQuestion => "malformed-question",
        HeadlessNoteKind.UnknownDirective => "unknown-directive",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown headless note kind."),
    };
}
