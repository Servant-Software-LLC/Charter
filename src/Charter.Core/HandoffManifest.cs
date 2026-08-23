using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Charter.Core;

/// <summary>
/// The three files one <c>charter handoff</c> run is about, by BARE NAME (Charter #187).
/// </summary>
/// <remarks>
/// <b>These are the least load-bearing fields in the manifest and the first ones a reader reaches for.</b>
/// <c>-o ../gr/plan.md</c> records <c>"plan.md"</c> — never a path, the same no-local-path guarantee the
/// forensic record and the exported artifact keep — and effectively every Guardrails handoff is named
/// <c>plan.md</c>, so the names discriminate almost nothing. <b>The hashes are the join key; the names are
/// decoration.</b>
/// </remarks>
/// <param name="Plan">The <c>.charter.md</c> this run read.</param>
/// <param name="Answers">The <c>--answers</c> file, or null when none was supplied.</param>
/// <param name="Handoff">The flattened CommonMark this run wrote.</param>
public sealed record HandoffManifestFiles(string Plan, string? Answers, string Handoff);

/// <summary>
/// The chain-of-custody manifest for one <c>charter handoff</c> run: which plan, which answers, which output,
/// and what each <c>:::question</c> resolved to — <b>from the same resolution pass that wrote the CommonMark</b>
/// (Charter #187).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap it closes.</b> The flattened plan asserts a set of resolved decisions and nothing beside it
/// recorded which inputs produced them, so "was every question answered before handoff, and by what" had to be
/// answered from <c>&lt;plan&gt;.headless.json</c> — a file written by a DIFFERENT verb from a DIFFERENT
/// resolution (the plan on disk, with no <c>--answers</c> merged at all). Both exit 0, both are internally
/// consistent, and they were reproduced disagreeing: the record said <c>"answered": true, "answer":
/// ["Postgres"]</c> while the handoff said <c>Answered: Cassandra</c>.
/// </para>
/// <para>
/// <b>Why this is not a <see cref="HeadlessRecord"/> emitted from <c>handoff</c>.</b> Three of that record's
/// fields are WRONG on this path, and they are the reason this type exists rather than a reuse:
/// <c>artifact</c> has no value here (emitting null retypes an always-string field, a schema bump by the
/// record's own rule); <c>sourceMap</c> and <c>anchorId</c> describe the RENDERED artifact, which this run does
/// not produce; and a consumer joining a source map into the flattened output would be joining against the
/// wrong file entirely. Hence the governing rule, which has a negative test of its own:
/// </para>
/// <para>
/// <b>Every line number in this manifest is a line in the PLAN, and the manifest carries no map into the
/// handoff output at all.</b> Do not add <c>artifact</c>, <c>sourceMap</c> or <c>anchorId</c> here.
/// </para>
/// <para>
/// <b>Pure in its inputs.</b> A function of (plan text, answers text, the <c>--fail-if-needs-human</c> flag,
/// the tool version, three file names). No clock, no random, no local path — so two runs over the same inputs
/// are byte-identical and reproducibility is itself assertable, the same discipline
/// <see cref="HeadlessRecord"/> keeps.
/// </para>
/// </remarks>
public sealed class HandoffManifest
{
    /// <summary>
    /// The manifest's schema version, bumped when the on-disk shape changes INCOMPATIBLY — a field removed,
    /// retyped, or given a new meaning. Adding a field is the compatible change this contract absorbs, so a
    /// consumer must ignore keys it does not recognise rather than reject the file.
    /// </summary>
    /// <remarks>
    /// It starts at 1 and is deliberately its OWN number rather than a share of
    /// <see cref="HeadlessRecord.Schema"/>: the two artifacts are written by different verbs, describe
    /// different things, and will not move together. A shared number would force a bump on one every time the
    /// other changed, which is how a version stops carrying information.
    /// </remarks>
    public const int Schema = 1;

    /// <summary>
    /// The escalation exit code a <c>--fail-if-needs-human</c> run returns. Charter.Core cannot reference the
    /// CLI's <c>HeadlessExitCodes.NeedsHuman</c>, which is the constant of record; the binding is asserted
    /// instead, by a CLI test that compares this field against the REAL process exit code — a stronger check
    /// than sharing a literal, because it also proves the file and <c>$?</c> cannot disagree.
    /// </summary>
    private const int NeedsHumanExitCode = 2;

    private readonly JsonObject _json;

    private HandoffManifest(JsonObject json) => _json = json;

    /// <summary>
    /// Build the manifest for one run.
    /// </summary>
    /// <param name="markdown">The plan's markdown, exactly as Charter read it.</param>
    /// <param name="handoffMarkdown">The flattened CommonMark, exactly as it will be written.</param>
    /// <param name="answers">The parsed <c>--answers</c> file plus its hash, or null when none was supplied.</param>
    /// <param name="gate">
    /// The ALREADY-COMPUTED gate result. Taken rather than computed so there is exactly one evaluation per run:
    /// re-running the gate here would be a second resolution of the same plan, and a manifest assembled from a
    /// different pass than the one that judged the run is the defect this artifact exists to prevent.
    /// </param>
    /// <param name="failIfNeedsHumanPassed">
    /// Whether <c>--fail-if-needs-human</c> was on the command line. It records the ARGV, not obedience — it
    /// says nothing about whether the caller honoured the exit code, which is why the emitted field is called
    /// <c>flagPassed</c> and not <c>enforced</c>.
    /// </param>
    /// <param name="files">The three bare file names this run is about.</param>
    /// <param name="charterVersion">The tool version that produced this.</param>
    public static HandoffManifest Build(
        string markdown,
        string handoffMarkdown,
        HandoffAnswers? answers,
        HandoffGateResult gate,
        bool failIfNeedsHumanPassed,
        HandoffManifestFiles files,
        string charterVersion)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(charterVersion);
        BareFileName.Require(files.Plan, "files.Plan");
        BareFileName.Require(files.Handoff, "files.Handoff");
        if (files.Answers is not null)
        {
            BareFileName.Require(files.Answers, "files.Answers");
        }

        return new HandoffManifest(
            Serialize(
                markdown ?? string.Empty,
                handoffMarkdown ?? string.Empty,
                answers,
                gate,
                failIfNeedsHumanPassed,
                files,
                charterVersion));
    }

    /// <summary>
    /// The manifest as indented JSON — deterministic in its inputs, so repeated runs are byte-identical.
    /// </summary>
    /// <remarks>
    /// Written through a <see cref="Utf8JsonWriter"/> rather than <c>JsonNode.ToJsonString</c> for the same two
    /// reasons <see cref="HeadlessRecord.ToJson"/> is: that overload needs a <c>TypeInfoResolver</c> on any
    /// custom options and throws without one, and the writer indents with a bare LF on every platform, so the
    /// file's bytes do not depend on the OS that produced it. The trailing newline is deliberate and part of
    /// the byte-stability.
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
        string handoffMarkdown,
        HandoffAnswers? answers,
        HandoffGateResult gate,
        bool failIfNeedsHumanPassed,
        HandoffManifestFiles files,
        string charterVersion)
    {
        var questionArray = new JsonArray();
        foreach (var question in gate.Questions)
        {
            questionArray.Add(new JsonObject
            {
                ["id"] = question.Id,
                ["title"] = question.Title,

                // A line in the PLAN. It is here because questions[] is not a map: duplicate ids are a gate
                // blocker, but the plan still carries two blocks and both are emitted, and this is what tells
                // them apart.
                ["sourceLine"] = question.SourceLine,
                ["answered"] = question.Answered,
                ["answer"] = ToJsonArray(question.Answer),

                // `inline` | `answers-file` | null. TWO values and no more: Charter #186 shipped REFUSAL of an
                // override, so "the file overrode the plan" is not a state that can occur, and a field that can
                // never be true is worse than an absent one. Null when the question is unanswered -- no value
                // means no source, and a third token would be a state a consumer has to learn.
                ["answerSource"] = question.Source is null ? null : JsonValue.Create(question.Source),
            });
        }

        var blockerArray = new JsonArray();
        foreach (var blocker in gate.Blockers)
        {
            blockerArray.Add(new JsonObject
            {
                ["kind"] = blocker.Kind,
                ["id"] = blocker.Id is null ? null : JsonValue.Create(blocker.Id),
                ["title"] = blocker.Title is null ? null : JsonValue.Create(blocker.Title),
                ["target"] = blocker.Target is null ? null : JsonValue.Create(blocker.Target),
                ["sourceLine"] = blocker.SourceLine is { } line ? JsonValue.Create(line) : null,

                // `detail` is NOT serialized. HandoffGate documents it as "Not a contract; do not parse it",
                // and putting it in a versioned schema makes it a de-facto contract the first time a harness
                // greps it. `kind` carries the wire meaning; the rest of this object carries the rest.
            });
        }

        var unmatched = new JsonArray();
        foreach (var id in gate.UnmatchedAnswerIds)
        {
            unmatched.Add(id);
        }

        return new JsonObject
        {
            ["schema"] = Schema,
            ["charterVersion"] = charterVersion,

            ["plan"] = files.Plan,
            ["planSha256"] = PlanHash.Sha256Hex(markdown),

            // null + null means NO --answers was passed. That is NOT the same as an empty answers file, which
            // is a file and hashes to the hash of its text -- `{}` produces a real hex here.
            ["answers"] = files.Answers is null ? null : JsonValue.Create(files.Answers),
            ["answersSha256"] = answers is null ? null : JsonValue.Create(answers.Sha256),

            ["handoff"] = files.Handoff,

            // Over the bytes this run WRITES, provenance stamps INCLUDED -- the file as a consumer will find
            // it, not the text before the stamps were appended. ADVISORY, and documented as such: nothing
            // downstream records the source plan's hash (Guardrails #505), so this is the only tamper detector
            // Charter can offer, and a mismatch means tampering OR a line-ending rewrite in transit, which the
            // hash alone cannot separate. The in-band stamps are the CRLF-immune half of the same question.
            ["handoffSha256"] = PlanHash.Sha256Hex(handoffMarkdown),

            // Emitted so `> 0` is a ONE-FIELD detection. A :::question whose body will not parse is absent from
            // questions[] entirely -- Charter has no id to list it under -- so without this a plan with a broken
            // question yields a questions[] that looks complete and entirely answered, while the handoff has
            // DELETED that question's id, title and target from the document this file vouches for.
            ["malformedQuestions"] = gate.MalformedQuestions,

            ["gate"] = new JsonObject
            {
                ["flagPassed"] = failIfNeedsHumanPassed,
                ["needsHuman"] = gate.NeedsHuman,

                // Derived, never passed in, so the manifest and $? cannot disagree -- the same discipline that
                // keeps the forensic record's needsHuman equal to `charter headless`'s exit code. A gate verdict
                // with the flag OFF is reported here and NOT in the exit code, which is exactly what
                // flagPassed: false records.
                ["exitCode"] = failIfNeedsHumanPassed && gate.NeedsHuman ? NeedsHumanExitCode : 0,

                // [] genuinely means NOTHING BLOCKS. Nothing in this file is computed conditionally, unlike the
                // forensic record's old `notes: []`, which meant "nothing of a kind Charter had" while two
                // lints went to stderr with no matching note kind (Charter #173).
                ["blockers"] = blockerArray,
                ["unmatchedAnswerIds"] = unmatched,
            },

            ["questions"] = questionArray,
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
}
