using System.Text.Json;
using Charter.Core;

namespace Charter.Cli;

/// <summary>
/// <c>charter verify &lt;handoff.md&gt;</c> — recompute the chain-of-custody joins between a flattened plan and
/// the manifest beside it, instead of leaving every consumer to reimplement them (Charter #192).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and completely so.</b> No writing, no network, no clock. It opens exactly two files: the
/// handoff named on the command line, and the manifest at the name
/// <see cref="CharterCommands.DeriveManifestPath"/> derives from it.
/// </para>
/// <para>
/// <b>THE THING THIS VERB CANNOT DO, stated first because a green result will be quoted as if it could.</b>
/// The handoff and its manifest sit in one directory and are writable by the same party, so
/// <c>verify</c> detects <b>inconsistency between two mutually-writable files; it can never detect
/// incorrectness</b>. Edit <c>Answered: Postgres</c> to <c>Cassandra</c> in the handoff, recompute
/// <c>handoffSha256</c> in the manifest — a plain JSON file — and every join here passes. That is not a defect
/// to be fixed by adding checks; there is no independent witness available to this process. It is why
/// <see cref="NotProvenNote"/> is printed on SUCCESS as well as on failure, and why the negative suite
/// (<c>VerifyNegativeSuiteTests</c>) was written before any join was.
/// </para>
/// <para>
/// <b>Why the verb exists anyway.</b> The joins are real and consumers were expected to check them by hand —
/// which is the same argument that put the strict gate in Charter rather than in each caller (#172). A
/// verifier that catches the accidents (a stale manifest beside a re-run handoff, a truncated write, a
/// line-ending rewrite in transit, a manifest disagreeing with the document it vouches for) is worth having;
/// one that is quoted as proof of a proper run is not, which is what the help text is for.
/// </para>
/// </remarks>
internal static class VerifyCommand
{
    /// <summary>Every join holds and the manifest records no outstanding escalation.</summary>
    private const int Holds = 0;

    /// <summary>
    /// The escalation code, shared with <c>handoff</c>/<c>headless</c>: a human must act. Either a join
    /// disagreed, or the manifest itself records <c>gate.needsHuman: true</c>.
    /// </summary>
    /// <remarks>
    /// <b>The <c>needsHuman</c> clause is what stops this verb passing vacuously.</b> A verifier that reads
    /// <c>needsHuman: true</c> and exits <c>0</c> is lying by omission — every join can hold over a plan that
    /// Charter itself said needs a person. §10.0.2 forbids the PRODUCER changing an exit code as a side effect
    /// of writing a file; it says nothing about a READER re-reporting Charter's own recorded verdict, which is
    /// all this is. It also keeps the shared meaning of a 2 exactly intact: the output exists, go read it.
    /// </remarks>
    private const int NeedsAttention = HeadlessExitCodes.NeedsHuman;

    /// <summary>
    /// The bound on what a green result means, printed on EVERY run — success included — and repeated in
    /// <c>--help</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a constant rather than two hand-written paragraphs because the two must not drift: the help is
    /// what a pipeline author reads before wiring the verb in, and the report is what somebody pastes into a
    /// post-mortem. ASCII only, like every other line Charter writes.
    /// </para>
    /// <para>
    /// Each sentence is here because a specific input exits <c>0</c> and a reader would expect it not to.
    /// Do not trim this to make the output tidier; the negative suite asserts the tokens.
    /// </para>
    /// </remarks>
    internal const string NotProvenNote =
        "WHAT A GREEN VERIFY DOES NOT PROVE: the handoff and its manifest sit in one directory and are "
        + "writable by the same party, so this detects INCONSISTENCY between two mutually-writable files -- it "
        + "can never detect INCORRECTNESS. Edit an answer in the handoff, recompute handoffSha256 in the "
        + "manifest, and every join above passes. It does NOT check answer VALUES (that would mean "
        + "prose-parsing arbitrary text). It does NOT prove the handoff reached Guardrails unmodified, that a "
        + "human reviewed anything, or that the caller honoured an exit code. Do not quote a green verify in a "
        + "post-mortem as evidence that a run was proper.";

    /// <summary>
    /// Recompute every join for <paramref name="handoffPath"/> and report. Any unexpected I/O failure
    /// propagates to <c>RunVerb</c>, which turns it into one stderr line and exit 1 — the right code, because a
    /// verify that could not read its inputs has not answered the question.
    /// </summary>
    public static int Execute(string handoffPath)
    {
        if (WrongFileGuard(handoffPath) is { } guard)
        {
            return guard;
        }

        if (!File.Exists(handoffPath))
        {
            Console.Error.WriteLine($"charter verify: handoff not found: {handoffPath}");
            return 1;
        }

        var full = Path.GetFullPath(handoffPath);
        var manifestPath = CharterCommands.DeriveManifestPath(full);
        if (!File.Exists(manifestPath))
        {
            ReportMissingManifest(full, manifestPath);
            return 1;
        }

        HandoffManifest.Facts manifest;
        try
        {
            manifest = HandoffManifest.Read(File.ReadAllText(manifestPath));
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine(
                $"charter verify: could not read {Path.GetFileName(manifestPath)}: {ex.Message} Nothing was "
                    + "verified.");
            return 1;
        }

        if (manifest.Schema != HandoffManifest.Schema)
        {
            Console.Error.WriteLine(
                $"charter verify: {Path.GetFileName(manifestPath)} declares schema {manifest.Schema}; this "
                    + $"charter understands {HandoffManifest.Schema}. A newer manifest may join on fields this "
                    + "build does not know, so nothing was verified. Upgrade charter.");
            return 1;
        }

        // The bytes, once, so the encoding finding and the recompute describe ONE snapshot -- the same TOCTOU
        // discipline HandoffAnswers keeps for the answers file.
        var bytes = File.ReadAllBytes(full);
        var handoff = HandoffAnswers.Decode(bytes);

        var stamps = HandoffMarkdown.ReadStamps(handoff);
        if (stamps is null)
        {
            Console.Error.WriteLine(
                $"charter verify: {Path.GetFileName(full)} carries no `{HandoffMarkdown.StampPrefix}` stamp on "
                    + "its last line. Either it is not a flattened plan `charter handoff` wrote, or it was "
                    + "written by a charter older than the in-band stamps. Nothing was verified.");
            return 1;
        }

        // Two lists, deliberately. A FINDING is a disagreement a human must act on and it sets the exit code; a
        // NOTE is something worth saying that Charter cannot tell apart from legitimate content, and it NEVER
        // changes an exit code -- the same rule `WarnOnVersionMarker` / `WarnOnDuplicateQuestionIds` /
        // `WarnOnMissingRecommendation` already follow. A lint that cannot distinguish a defect from a
        // legitimate plan must not be able to fail one.
        var findings = new List<string>();
        var notes = new List<string>();
        var report = new List<string>
        {
            $"  manifest        {Path.GetFileName(manifestPath)} (schema {manifest.Schema}"
                + $", charter {manifest.CharterVersion ?? "unrecorded"})",
        };

        CheckPlanStamp(manifest, stamps, report, findings);
        CheckAnswersStamp(manifest, stamps, report, findings);
        CheckHandoffHash(manifest, handoff, bytes, full, report, findings);
        CheckQuestions(manifest, handoff, report, findings, notes);

        return Report(full, manifest, report, findings, notes);
    }

    // ---- the wrong-file guards -----------------------------------------------------------------------------

    /// <summary>
    /// Refuse the two files a caller will hand this verb by mistake, naming the right one — or null when the
    /// argument looks like a handoff.
    /// </summary>
    /// <remarks>
    /// Without these, both mistakes fail as <i>"no manifest beside it"</i> or <i>"no stamp"</i>, which reads as
    /// <b>the custody chain is broken</b> rather than <b>you passed the wrong file</b>. That is the loud-vs-silent
    /// distinction that made `charter verify` an acceptable name for a second meaning at all (§12.6): reaching
    /// for `headless` when you wanted `handoff` fails SILENTLY at exit 0, and this does not.
    /// </remarks>
    private static int? WrongFileGuard(string handoffPath)
    {
        var name = Path.GetFileName(handoffPath);

        if (name.EndsWith(".charter.md", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"charter verify: {name} is a Charter PLAN, not a handoff. This verb verifies the flattened "
                    + "CommonMark `charter handoff -o` wrote, against the manifest beside it. Run `charter "
                    + "handoff <plan> -o <out.md> --manifest` first, then verify the OUT file. (For the "
                    + "review-side checks -- a stale plan, uncommitted comments -- see `charter review "
                    + "verify`, which is a different verb and is not built yet.)");
            return 1;
        }

        if (name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"charter verify: {name} is the MANIFEST. Pass the handoff instead -- the manifest is found "
                    + "from it, not the other way round.");
            return 1;
        }

        return null;
    }

    private static void ReportMissingManifest(string handoffPath, string manifestPath)
    {
        Console.Error.WriteLine(
            $"charter verify: no manifest beside {Path.GetFileName(handoffPath)} (looked for "
                + $"{Path.GetFileName(manifestPath)}). Nothing was verified.");

        // The known limit, stated honestly rather than alarmingly. The artifacts are DESIGNED to travel -- they
        // carry bare names and no local paths (10.4) -- but discovery is co-location plus co-naming, so a
        // handoff copied into a task folder without its manifest is unverifiable forever. That is a fact about
        // where the file is, not evidence about the run.
        Console.Error.WriteLine(
            "  This is not evidence of tampering: the manifest is found by co-location and co-naming, so a "
                + "handoff copied or moved WITHOUT it cannot be verified at its new home. Re-run `charter "
                + "handoff ... --manifest`, or verify the pair where they were written.");
    }

    // ---- the joins -----------------------------------------------------------------------------------------

    private static void CheckPlanStamp(
        HandoffManifest.Facts manifest,
        HandoffMarkdown.HandoffStamps stamps,
        List<string> report,
        List<string> findings)
    {
        if (string.Equals(manifest.PlanSha256, stamps.PlanSha256, StringComparison.OrdinalIgnoreCase))
        {
            report.Add($"  plan-sha256     MATCH     {stamps.PlanSha256}");
            return;
        }

        report.Add("  plan-sha256     MISMATCH");
        findings.Add(
            $"plan-sha256: the manifest says {manifest.PlanSha256}, the handoff's in-band stamp says "
                + $"{stamps.PlanSha256}. These two are written by ONE run over ONE plan, so they cannot "
                + "honestly differ -- the manifest belongs to a different run than this handoff, or one of "
                + "them was edited.");
    }

    private static void CheckAnswersStamp(
        HandoffManifest.Facts manifest,
        HandoffMarkdown.HandoffStamps stamps,
        List<string> report,
        List<string> findings)
    {
        if (stamps.AnswersStamp is null)
        {
            report.Add("  answers-sha256  SKIPPED   (the handoff carries no answers stamp)");
            findings.Add(
                "answers-sha256: this handoff predates the answers stamp, so the STALE-MANIFEST hazard it "
                    + "exists to close cannot be ruled out here: a run with an --answers file and a later "
                    + "plain re-run over the same plan produce the same plan-sha256, and the older manifest "
                    + "would still join cleanly. Re-run `charter handoff` with a current charter.");
            return;
        }

        // `none` and null are the SAME claim from the two sides -- "this run merged no answers file". The stamp
        // spells it as a word because an omitted line would be indistinguishable from a producer too old to
        // write one, which is the case handled above.
        var stampSaysNone = string.Equals(
            stamps.AnswersStamp, HandoffMarkdown.NoAnswersFile, StringComparison.Ordinal);

        if (manifest.AnswersSha256 is null && stampSaysNone)
        {
            report.Add($"  answers-sha256  MATCH     {HandoffMarkdown.NoAnswersFile}");
            return;
        }

        if (manifest.AnswersSha256 is { } hash
            && string.Equals(hash, stamps.AnswersStamp, StringComparison.OrdinalIgnoreCase))
        {
            report.Add($"  answers-sha256  MATCH     {hash}");
            return;
        }

        report.Add("  answers-sha256  MISMATCH");
        findings.Add(
            $"answers-sha256: the manifest says {manifest.AnswersSha256 ?? "no answers file"}, the handoff's "
                + $"in-band stamp says {stamps.AnswersStamp}. This is exactly the disagreement the second "
                + "stamp exists to make visible -- the two files describe runs with DIFFERENT resolution "
                + "inputs, which the plan hash alone cannot show.");
    }

    /// <summary>
    /// The <c>handoffSha256</c> join, plus the diagnostics that name a benign cause WITHOUT excusing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A mismatch FAILS, always.</b> <see cref="PlanHash"/> defines the question this field answers as
    /// <i>"are these two files byte-for-byte the same revision?"</i>, and a verifier must not quietly answer a
    /// different, weaker one. So the recompute below is a LABELLED DIAGNOSTIC naming the likely cause — never a
    /// second definition of the field, and never a pass.
    /// </para>
    /// <para>
    /// <b>Why the trailing-newline case gets its own branch.</b> <c>Emit</c>'s output has no trailing newline
    /// while <c>HandoffManifest.ToJson()</c> appends one, so any editor with "insert final newline" adds one to
    /// the handoff and not to the manifest. That is neither tampering nor a line-ending rewrite, and it is
    /// MORE LIKELY than a wholesale CRLF rewrite — without its own branch, the most common benign mutation
    /// would get the most alarming message.
    /// </para>
    /// </remarks>
    private static void CheckHandoffHash(
        HandoffManifest.Facts manifest,
        string handoff,
        byte[] bytes,
        string handoffPath,
        List<string> report,
        List<string> findings)
    {
        var recomputed = PlanHash.Sha256Hex(handoff);
        if (string.Equals(manifest.HandoffSha256, recomputed, StringComparison.OrdinalIgnoreCase))
        {
            report.Add($"  handoff-sha256  MATCH     {recomputed}");
            ReportEncoding(bytes, handoffPath, findings);
            return;
        }

        report.Add("  handoff-sha256  MISMATCH");

        // ONE finding, carrying its diagnosis as an indented continuation line. Adding the diagnosis as a
        // second list entry would make the closing "N finding(s)" count the EXPLANATION as a second problem --
        // a verifier that inflates its own count is the last thing this verb should do.
        findings.Add(
            $"handoff-sha256: the manifest says {manifest.HandoffSha256}, this file hashes to {recomputed}. "
                + "The file is NOT the revision the manifest describes."
                + string.Concat(
                    DiagnoseHandoffHash(manifest.HandoffSha256, handoff).Select(line => "\n  " + line)));

        ReportEncoding(bytes, handoffPath, findings);
    }

    /// <summary>
    /// Name a benign cause for a <c>handoffSha256</c> mismatch when one can be demonstrated, or say that none
    /// could be.
    /// </summary>
    private static IEnumerable<string> DiagnoseHandoffHash(string expected, string handoff)
    {
        var trimmed = handoff.TrimEnd('\n');
        if (!string.Equals(trimmed, handoff, StringComparison.Ordinal)
            && string.Equals(PlanHash.Sha256Hex(trimmed), expected, StringComparison.OrdinalIgnoreCase))
        {
            yield return
                "cause: a TRAILING NEWLINE was added. `charter handoff` writes no final newline (the manifest "
                + "JSON does, which is why only this file differs), so an editor set to `insert final newline` "
                + "produces exactly this. Benign, but the hash still does not match -- re-run `charter "
                + "handoff` if the manifest must join.";
            yield break;
        }

        // A LONE \r is never normalised. ReviewBaseStatus's hash does collapse it, and copying that form here
        // would bless a CONTENT change as a line-ending rewrite: a question answer containing a lone \r
        // flattens as `Answered: line1<CR>line2` (Charter #202), so a "normalised" match would mean the two
        // files differ in the plan's own text. Where the original carries one, this DECLINES to diagnose.
        if (HasLoneCarriageReturn(handoff))
        {
            yield return
                "cause: not determined. This file contains a lone CR that is not part of a CRLF, so the "
                + "line-ending test below is not applied -- a lone CR can be plan CONTENT (a question answer "
                + "carrying one), and normalising it away would report a real content change as a harmless "
                + "rewrite.";
            yield break;
        }

        var toCrLf = handoff.Replace("\n", "\r\n", StringComparison.Ordinal);
        var toLf = handoff.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (string.Equals(PlanHash.Sha256Hex(toLf), expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PlanHash.Sha256Hex(toCrLf), expected, StringComparison.OrdinalIgnoreCase))
        {
            yield return
                "cause: a LINE-ENDING REWRITE. The text matches the manifest once CRLF and LF are treated "
                + "alike, so the content is intact and only the newlines differ -- `core.autocrlf`, a "
                + "`.gitattributes text=auto` checkout, or an editor. Because the file was read through a "
                + "decode that strips a BOM and honours UTF-16, this also covers a pure RE-ENCODING. Nothing "
                + "further is claimed: the bytes were not compared to the originals, which no longer exist.";
            yield break;
        }

        yield return
            "cause: NOT a line-ending rewrite and NOT an added trailing newline -- both were tested and "
            + "neither reproduces the manifest's hash. The file's CONTENT differs from what this manifest was "
            + "written over.";
    }

    /// <summary>True when <paramref name="text"/> carries a CR that is not the first half of a CRLF.</summary>
    private static bool HasLoneCarriageReturn(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Report the handoff's encoding as a FINDING when it is not BOM-less UTF-8.
    /// </summary>
    /// <remarks>
    /// <b><see cref="HandoffAnswers.EncodingWarning"/> is deliberately NOT reused.</b> For an <c>--answers</c>
    /// file a human chose the encoding, the file decodes correctly and the run is honest, so a warning whose
    /// remedy is "write it as BOM-less UTF-8" is right. <b>Charter writes the handoff itself</b>, as BOM-less
    /// UTF-8 — so a mark on this file means somebody rewrote it, which is EVIDENCE, not an excuse. Reusing that
    /// text would invert evidence into reassurance and would tell the user to rewrite the very artifact whose
    /// integrity is in question.
    /// </remarks>
    private static void ReportEncoding(byte[] bytes, string handoffPath, List<string> findings)
    {
        if (PlanHash.ByteOrderMarkName(bytes) is not { } mark)
        {
            return;
        }

        findings.Add(
            $"encoding: {Path.GetFileName(handoffPath)} begins with a {mark} byte order mark. Charter writes "
                + "this file as BOM-less UTF-8, so the mark means the file was rewritten after Charter wrote "
                + "it -- by an editor, a shell redirect, or a copy step. The hash above was computed over the "
                + "DECODED text, so it is unaffected by the mark itself; the rewrite is the finding.");
    }

    // ---- the payload cross-check ---------------------------------------------------------------------------

    /// <summary>
    /// The check that earns the verb its name: does the manifest describe the DOCUMENT beside it?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it, a manifest saying <c>"answered": true, "answer": ["Postgres"]</c> beside a handoff saying
    /// <c>&gt; **Open question (unresolved):**</c> passes every hash join — which is #187's own opening
    /// reproduction surviving verification. The hashes prove the two files were not edited since they were
    /// written; only this proves they were written about the same resolution.
    /// </para>
    /// <para>
    /// <b>A containment check against the producer's constants, NEVER a second <c>Emit</c>.</b> Re-deriving the
    /// flatten would make <c>verify</c> agree with itself rather than with the file on disk, which is the whole
    /// failure mode it exists to catch one level up.
    /// </para>
    /// <para>
    /// <b>Answer VALUES are deliberately not compared</b> — that would mean prose-parsing arbitrary user text,
    /// and a wrong parse would fail honest runs. The report says so, so nobody over-reads a green.
    /// </para>
    /// </remarks>
    private static void CheckQuestions(
        HandoffManifest.Facts manifest,
        string handoff,
        List<string> report,
        List<string> findings,
        List<string> notes)
    {
        var emitted = ScanQuestions(handoff, out var unclassified);

        var manifestIds = manifest.Questions.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        var handoffIds = emitted.Keys.ToHashSet(StringComparer.Ordinal);

        var missing = manifestIds.Except(handoffIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var extra = handoffIds.Except(manifestIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var disagreed = manifest.Questions
            .Where(q => emitted.TryGetValue(q.Id, out var answered) && answered != q.Answered)
            .ToList();

        if (missing.Count == 0 && extra.Count == 0 && disagreed.Count == 0)
        {
            report.Add(
                $"  questions       MATCH     {manifestIds.Count} id(s); answered flags agree (values NOT checked)");
        }
        else
        {
            report.Add("  questions       MISMATCH");
        }

        foreach (var id in missing)
        {
            findings.Add(
                $"questions: the manifest records `{id}` but the handoff emits no such question. The manifest "
                    + "is vouching for a decision that is not in the document beside it.");
        }

        foreach (var id in extra)
        {
            findings.Add(
                $"questions: the handoff emits `{id}` but the manifest does not record it. The manifest is "
                    + "incomplete for this document -- it may belong to an earlier revision of the plan.");
        }

        foreach (var question in disagreed)
        {
            var manifestSays = question.Answered ? "answered" : "unanswered";
            var handoffSays = question.Answered ? "OPEN" : "Answered";
            findings.Add(
                $"questions: `{question.Id}` is {manifestSays} in the manifest but the handoff shows it "
                    + $"{handoffSays}. This is the disagreement the manifest exists to make impossible.");
        }

        if (unclassified > 0)
        {
            // A NOTE, never a finding, so it cannot change the exit code. `:::custom-html` and ordinary prose
            // can reproduce Charter's own literals -- a plan DOCUMENTING Charter does exactly that, and this
            // repo's own plans would -- so a metadata line with no recognisable lead above it is far more
            // likely to be prose than a defect. Failing on it would make an honest plan unverifiable forever,
            // which is the false-alarm class this verb is supposed to avoid, and Charter's standing rule is
            // that a lint which cannot tell a defect from legitimate content never touches an exit code.
            //
            // Excluding it from the id set is safe in the other direction too: real tampering that strips a
            // question's lead line ALSO removes that id from the handoff's set, which IS a finding.
            notes.Add(
                $"questions: {unclassified} line(s) look like a question metadata line but carry no Answered / "
                    + "Open / Delegated lead above them, so they were NOT counted as questions. Ordinary prose "
                    + "can spell Charter's own literals (a plan documenting Charter does), so this is "
                    + "informational and does not affect the exit code.");
        }
    }

    /// <summary>
    /// Every question the handoff actually emits: id → whether it is shown as answered.
    /// </summary>
    /// <remarks>
    /// A question is recognised by its metadata line — <see cref="HandoffMarkdown.QuestionIdMarker"/> — with one
    /// of the three lead markers on the line above. Requiring the LEAD as well as the marker is what keeps a
    /// plan's own prose from injecting a phantom id; requiring the marker at all is what keeps this from
    /// guessing. A blockquote prefix is stripped first, because an open question emits both lines inside one.
    /// </remarks>
    private static Dictionary<string, bool> ScanQuestions(string handoff, out int unclassified)
    {
        // CRLF is folded; a LONE CR IS NOT A LINE BREAK HERE. This is the same rule the hash diagnostic keeps,
        // and it is not a nicety -- it was a live false alarm. A question answer may contain a lone CR (it
        // arrives JSON-escaped in the :::question body, so `Emit`'s source normalization never sees it, and the
        // flatten carries `Answered: alpha<CR>beta` -- Charter #202). Splitting on it tears the Answered line
        // in two, leaves the metadata line with `beta` above it, drops that id from the set, and reports an
        // UNTOUCHED, HONEST pair as `questions MISMATCH`. Reproduced before this line was written.
        var lines = handoff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        unclassified = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = Unquote(lines[i]);
            if (!line.StartsWith(HandoffMarkdown.QuestionIdMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[HandoffMarkdown.QuestionIdMarker.Length..];
            var close = rest.IndexOf('`');
            if (close <= 0)
            {
                unclassified++;
                continue;
            }

            switch (LeadAbove(lines, i))
            {
                case true:
                    found[rest[..close]] = true;
                    break;
                case false:
                    found[rest[..close]] = false;
                    break;
                default:
                    unclassified++;
                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// Whether the question whose metadata line is at <paramref name="index"/> is shown as answered (true),
    /// open (false), or carries no recognisable lead at all (null).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It searches BACKWARDS to the start of the metadata line's own block — the previous blank line — rather
    /// than looking only at the line immediately above. <see cref="Emit"/> joins blocks with a blank line, so
    /// the walk can never leave the question it belongs to, and stopping at the first blank keeps a preceding
    /// prose paragraph out of the answer.
    /// </para>
    /// <para>
    /// <b>The line immediately above is not reliably the lead</b>, and the reason is the same family as the
    /// lone-CR trap: a question's TITLE or its ANSWER VALUE may contain a newline (both come from JSON, and
    /// neither is collapsed by the emitter), so an honest flatten can read
    /// <c>**Q: T** — Answered: alpha / beta / _Question — id: …</c> over three lines. Looking one line up would
    /// find <c>beta</c>, drop that id from the set, and report an untouched pair as a mismatch.
    /// </para>
    /// </remarks>
    private static bool? LeadAbove(string[] lines, int index)
    {
        for (var i = index - 1; i >= 0 && lines[i].Trim().Length > 0; i--)
        {
            var line = Unquote(lines[i]);

            if (line.Contains(HandoffMarkdown.AnsweredMarker, StringComparison.Ordinal))
            {
                return true;
            }

            if (line.Contains(HandoffMarkdown.OpenQuestionMarker, StringComparison.Ordinal)
                || line.Contains(HandoffMarkdown.DelegatedDecisionMarker, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>A line with one leading blockquote marker removed, so an open question's two lines read the
    /// same as an answered question's.</summary>
    private static string Unquote(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("> ", StringComparison.Ordinal) ? trimmed[2..].TrimStart()
             : trimmed.StartsWith('>') ? trimmed[1..].TrimStart()
             : trimmed;
    }

    // ---- the report ----------------------------------------------------------------------------------------

    private static int Report(
        string handoffPath,
        HandoffManifest.Facts manifest,
        List<string> report,
        List<string> findings,
        List<string> notes)
    {
        Console.WriteLine($"charter verify: {Path.GetFileName(handoffPath)}");
        foreach (var line in report)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"  gate.needsHuman {(manifest.NeedsHuman ? "true" : "false")}");

        var joinsHold = findings.Count == 0;
        foreach (var finding in findings)
        {
            Console.Error.WriteLine($"charter verify: {finding}");
        }

        foreach (var note in notes)
        {
            Console.Error.WriteLine($"charter verify: note: {note}");
        }

        // Printed on SUCCESS too, and that is the point of the constant. A verify that only disclaims when it
        // fails is a verify whose green output gets pasted into a post-mortem with nothing attached.
        Console.WriteLine(NotProvenNote);

        if (joinsHold && !manifest.NeedsHuman)
        {
            Console.WriteLine("OK: every join holds and the manifest records no outstanding escalation.");
            return Holds;
        }

        if (!joinsHold)
        {
            Console.Error.WriteLine(
                $"charter verify: {findings.Count} finding(s) -- exit {NeedsAttention}. A human must read "
                    + "them; nothing was changed.");
        }

        if (manifest.NeedsHuman)
        {
            // Re-reporting Charter's OWN recorded verdict, not inventing one. Without this clause a manifest
            // saying `needsHuman: true` exits 0 from this verb, which is a vacuous pass: every join can hold
            // over a plan Charter itself said needs a person.
            Console.Error.WriteLine(
                $"charter verify: the manifest records gate.needsHuman: true -- exit {NeedsAttention}. The "
                    + "joins say the two files agree; this says what they agree ABOUT still needs a person. "
                    + "See the manifest's gate.blockers.");
        }

        return NeedsAttention;
    }
}
