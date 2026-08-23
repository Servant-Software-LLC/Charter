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
    /// The record's schema version, bumped when the on-disk shape changes INCOMPATIBLY — a field removed,
    /// retyped, or given a new meaning. A consumer that does not recognise it should refuse to interpret the
    /// file rather than guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Adding a field, or a new <c>notes[].kind</c> token, is NOT a bump</b> — it is the compatible change
    /// the contract is written to absorb, which is why a consumer is told to ignore an unrecognised key and an
    /// unrecognised note kind rather than reject the file.
    /// </para>
    /// <para>
    /// <b>Why 2.</b> Schema 1 shipped with the prose promise above and the promise failed twice: <c>recommended</c>
    /// was added in Charter #142 with the number left at 1, and — the one that actually breaks a harness —
    /// <c>notes: []</c> did not mean "Charter noticed nothing", because <c>handoff</c> printed two lints
    /// (missing <c>recommended</c>, untracked deferrals) the record had no kind for. Fixing that changes what an
    /// EXISTING field means, which is a bump by this constant's own rule. It is also the version from which the
    /// shape is bound by a test rather than by a sentence: <c>HeadlessRecordContractTests</c> holds the emitted
    /// field set against <c>skills/charter/references/unattended.md</c>, so an undocumented addition now fails
    /// the build. That is the mechanism a prose promise could not be (Charter #173).
    /// </para>
    /// </remarks>
    public const int Schema = 2;

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
    /// <param name="planFileName">The plan's BARE file name — never a path (see <see cref="BareFileName"/>).</param>
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

        // ONE walk, shared with the strict-handoff gate (Charter #172) so the two can never disagree about
        // which questions a plan has. The escalation predicate stays this record's own — see
        // PlanInventory.NeedsHuman and HandoffGate for why they are deliberately not the same boolean.
        var inventory = PlanInventory.Build(markdown);

        return new HeadlessRecord(
            Serialize(markdown, planFileName, artifactFileName, charterVersion, inventory),
            inventory.NeedsHuman,
            inventory.Questions,
            inventory.Notes);
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
        PlanInventory inventory)
    {
        var questionArray = new JsonArray();
        foreach (var question in inventory.Questions)
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
                // The authoring agent's lean, or null (Charter #142). An UNANSWERED human question is a
                // blocking escalation, and this is the field that decides whether the record can say what
                // the agent would have chosen or merely that someone must choose. Emitted always, including
                // as an explicit null, so a triage reading the record never has to distinguish "no lean"
                // from "this Charter is too old to report one".
                ["recommended"] = question.Recommended is null ? null : JsonValue.Create(question.Recommended),
                ["anchorId"] = question.AnchorId,
                ["sourceLine"] = question.SourceLine,
            });
        }

        var noteArray = new JsonArray();
        foreach (var note in inventory.Notes)
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
            ["planSha256"] = PlanHash.Sha256Hex(markdown),
            ["artifact"] = artifactFileName,

            // The plan's own format marker, as a PAIR (Charter #173). A bare integer could not carry the
            // distinction a consumer needs: CharterFormat reports no version for a MISSING marker and for a
            // present-but-non-integer one alike, so `charter-format-version: 1.0` would read identically to an
            // unstamped plan. The status token says which case it is; `marker` is the raw declared value,
            // verbatim, or null when there is no marker at all.
            ["planFormatVersion"] = new JsonObject
            {
                ["status"] = VersionMarkerToken(inventory.VersionMarker.Status),
                ["marker"] = inventory.VersionMarker.RawValue is { } raw ? JsonValue.Create(raw) : null,
                ["version"] = inventory.VersionMarker.Version is { } parsed ? JsonValue.Create(parsed) : null,
            },
            ["needsHuman"] = inventory.NeedsHuman,
            ["questions"] = questionArray,
            ["notes"] = noteArray,
            ["sourceMap"] = sourceMap,
        };
    }

    /// <summary>The wire token for a version-marker status. Hyphen-free single words, matching the token style
    /// the rest of the record uses.</summary>
    private static string VersionMarkerToken(VersionMarkerStatus status) => status switch
    {
        VersionMarkerStatus.Ok => "ok",
        VersionMarkerStatus.Missing => "missing",
        VersionMarkerStatus.Unsupported => "unsupported",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown version-marker status."),
    };

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
    /// The no-local-path guarantee, from its ONE kernel (<see cref="BareFileName"/>). It used to live here as
    /// a private method; <see cref="HandoffManifest"/> needs exactly the same rule for three more names
    /// (Charter #187), and a security-shaped rule with two implementations is one that can hold on Windows and
    /// not on Unix. Nothing about the record's behaviour changed when it moved.
    /// </summary>
    private static void RequireBareFileName(string value, string parameterName)
        => BareFileName.Require(value, parameterName);
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
    int SourceLine,
    string? Recommended = null)
{
    /// <summary>
    /// True when the question carries an answer that records a DECISION — at least one value, none of them
    /// blank. The predicate is <see cref="AnswerRules.IsDecision"/>, shared with the renderer's
    /// resolved-question display and the flatten's Answered/Open branch, because a field read in three places
    /// that means three things is the defect one level up.
    /// </summary>
    /// <remarks>
    /// It used to be <c>Answer.Count &gt; 0</c> — counting elements, not content — so <c>[""]</c> reported the
    /// question answered and any strict gate built on it certified a blank decision as a made one
    /// (Charter #188). Narrowing it changes what a published field MEANS, which is a
    /// <see cref="HeadlessRecord.Schema"/> bump by the constant's own rule — except that schema 2 has not
    /// shipped: it was raised from 1 by Charter #173 AFTER 0.24.0 was released, so no consumer has ever seen
    /// a schema-2 record and this rides the same 2 rather than forcing a 3.
    /// </remarks>
    public bool Answered => AnswerRules.IsDecision(Answer);
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

    /// <summary>
    /// An open, human-targeted, select-mode <c>:::question</c> carries no <c>recommended</c> key at all
    /// (Charter #142) — so an escalation on it can say a human must decide while offering nothing to decide
    /// with. A warning: it never raises <see cref="HeadlessRecord.NeedsHuman"/>.
    /// </summary>
    MissingRecommendation,

    /// <summary>
    /// A paragraph defers work without naming anything that tracks it (Charter #156). A warning: it never
    /// raises <see cref="HeadlessRecord.NeedsHuman"/>.
    /// </summary>
    UntrackedDeferral,

    /// <summary>
    /// A <c>:::question</c> nested inside a container that renders its children, so it is drawn as a real,
    /// answerable form and is invisible to the block model (Charter #203). It RAISES
    /// <see cref="HeadlessRecord.NeedsHuman"/>: the decision is absent from <c>questions[]</c> entirely, so
    /// without it the record would say nobody is needed over a question a human is looking at.
    /// </summary>
    NestedQuestion,

    /// <summary>
    /// A <c>:::diff</c> nested inside a container that renders its children (Charter #203). A warning in the
    /// record, a BLOCKER for strict handoff: it flattens as blockquoted prose, where line-initial <c>+</c> and
    /// <c>-</c> are consumed as CommonMark bullet markers, so an added and a removed line become
    /// indistinguishable.
    /// </summary>
    NestedDiff,

    /// <summary>
    /// An unrecognized <c>:::foo</c> nested inside a container that renders its children (Charter #203). A
    /// warning in the record, a BLOCKER for strict handoff, for exactly the reason a top-level one is: a
    /// misspelled <c>:::questoin</c> classifies as one and may hide a <c>target: human</c> decision.
    /// </summary>
    NestedUnknownDirective,

    /// <summary>
    /// Any OTHER <c>:::</c> directive nested inside a container that renders its children — a
    /// <c>:::comparison</c>, <c>:::diagram</c>, <c>:::note</c> or <c>:::warn</c> (Charter #203). A warning
    /// only: each of those bodies was READ out of the flatten and survives blockquoting as CommonMark prose
    /// (a table, a list, Mermaid source in a fence, prose), so the loss is presentational — the block's own
    /// framing and its anchors — never a corrupted or absent fact.
    /// </summary>
    NestedDirective,
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
        HeadlessNoteKind.MissingRecommendation => "missing-recommendation",
        HeadlessNoteKind.UntrackedDeferral => "untracked-deferral",
        HeadlessNoteKind.NestedQuestion => "nested-question",
        HeadlessNoteKind.NestedDiff => "nested-diff",
        HeadlessNoteKind.NestedUnknownDirective => "nested-unknown-directive",
        HeadlessNoteKind.NestedDirective => "nested-directive",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown headless note kind."),
    };
}
