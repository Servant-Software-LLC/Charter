using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Charter.Core;

namespace Charter.Server;

/// <summary>
/// The server-owned durability sidecar for one review session (§1.6): a per-plan file — <b>never</b> the
/// <c>.charter.md</c> — into which the running server persists its queued annotations and answers on every
/// change, and from which it rehydrates on start. Because solo review is supported (a human answers questions
/// with no agent draining), a <c>charter review</c> crash before drain must lose nothing; this is that
/// guarantee. Writing a file the server owns does not violate single-writer-of-the-plan (invariant 4): the
/// drafting agent stays the only writer of <c>.charter.md</c>.
/// </summary>
/// <remarks>
/// The sidecar lives under the per-user state dir (<see cref="StateDirectory.Sidecars"/>), named by a SHA-256
/// of the plan's canonical path so <c>charter resolve</c> resolves straight to it, and is written atomically
/// (temp + <see cref="File.Move(string, string, bool)"/> in the same directory, mirroring
/// <see cref="SessionRegistry.Write"/>) and <c>0600</c> on POSIX. A fully-drained session (no annotations, no
/// answers) deletes the file rather than leaving an empty husk. All reads degrade gracefully — a
/// missing/corrupt sidecar rehydrates as empty, never throws.
/// </remarks>
public sealed class ReviewSidecar
{
    // Schema 2 adds planHash — the fingerprint of the plan as it was when the queue was last written, which is
    // what lets a rehydrate tell "this document was edited" from "a different document was put at this path"
    // (Charter #67). A schema-1 sidecar simply has no hash and falls through to the anchor test, which is the
    // actual rule; the hash is only a fast, exact "definitely the same document" exemption.
    private const int CurrentSchema = 2;

    // The suffix a quarantined queue is moved aside under. It stays in the sidecar directory, beside the live
    // sidecar, so the notice can name a path the reviewer can actually open.
    private const string QuarantineMarker = ".stale-";

    // The UTC stamp format inside a quarantine file's name. It is the authoritative record of WHEN a queue was
    // set aside — unlike the file's mtime, which File.Copy inherits from the sidecar it copied.
    private const string QuarantineStampFormat = "yyyyMMdd'T'HHmmss'Z'";

    // Length of a rendered QuarantineStampFormat ("20260724T101530Z"), used to slice it back out of a name.
    private const int QuarantineStampLength = 16;

    /// <summary>
    /// How long a superseded quarantine copy is kept before <see cref="Quarantine"/> retires it (Charter #75
    /// item 4 — the copies used to accumulate forever, while the sidecar itself deletes when empty).
    /// </summary>
    /// <remarks>
    /// A quarantine copy is the ONLY copy of the notes it holds, so the bound is conservative in two directions
    /// at once: a file must be older than this <b>and</b> have been superseded by a newer set-aside queue for the
    /// same plan. The NEWEST preserved queue is never retired at any age — that is the one the stderr notice
    /// named and the one <c>--keep-annotations</c> restores first, so a reviewer who quarantined once and came
    /// back months later still finds their notes exactly where they were told. Thirty days is well past any
    /// plausible "I will get back to that review" window while still bounding the steady state at "replacements
    /// in the last month", instead of "replacements ever". Pruning runs only where the set GROWS (a successful
    /// quarantine), never on a read, so it can never race a reclaim.
    /// </remarks>
    private static readonly TimeSpan QuarantineRetention = TimeSpan.FromDays(30);

    // 0600 — the owning user may read/write the sidecar; nobody else. It mirrors queued review state.
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions SidecarJson =
        new(AnnotationApi.JsonOptions) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _sourcePath;
    private readonly AnnotationStore _annotations;
    private readonly AnswerStore _answers;

    /// <summary>
    /// Bind a sidecar at <paramref name="path"/> to the live <paramref name="annotations"/> and
    /// <paramref name="answers"/> stores for the plan at <paramref name="sourcePath"/>. <see cref="Persist"/>
    /// snapshots those stores; the server calls it after every store mutation.
    /// </summary>
    public ReviewSidecar(string path, string sourcePath, AnnotationStore annotations, AnswerStore answers)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        _path = path;
        _sourcePath = sourcePath;
        _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
        _answers = answers ?? throw new ArgumentNullException(nameof(answers));
    }

    /// <summary>The current queued annotations and answers rehydrated from a sidecar (empty when none).</summary>
    /// <param name="Annotations">The queued, not-yet-drained annotations.</param>
    /// <param name="Answers">The queued, not-yet-applied question answers.</param>
    /// <param name="PlanHash">
    /// The plan's content fingerprint as of the last write, or <see langword="null"/> for a sidecar written
    /// before schema 2. See <see cref="IsStale"/> for what it is (and is not) used for.
    /// </param>
    public sealed record State(
        IReadOnlyList<Annotation> Annotations, IReadOnlyList<Answer> Answers, string? PlanHash = null)
    {
        /// <summary>An empty rehydration result — the sidecar was missing, empty, or unreadable.</summary>
        public static State Empty { get; } = new(Array.Empty<Annotation>(), Array.Empty<Answer>());
    }

    /// <summary>
    /// The sidecar file path for <paramref name="planPath"/> inside <paramref name="sidecarsDirectory"/>:
    /// <c>&lt;sha256(Path.GetFullPath(planPath))&gt;.review.json</c>. Canonicalizing first makes the name
    /// stable across equivalent relative/absolute references — the same name the server, <c>poll</c>, and
    /// <c>resolve</c> all compute for one plan.
    /// </summary>
    public static string PathForPlan(string sidecarsDirectory, string planPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sidecarsDirectory);
        ArgumentException.ThrowIfNullOrEmpty(planPath);

        var canonical = Path.GetFullPath(planPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return Path.Combine(sidecarsDirectory, hash + ".review.json");
    }

    /// <summary>
    /// Snapshot the bound stores and persist them, serialized so concurrent request threads never tear a
    /// write. Each call reads the live store state, so whichever call runs last writes the current state.
    /// </summary>
    public void Persist()
    {
        lock (_gate)
        {
            // #117 — the DURABLE view, which includes the in-flight batch. A batch handed to a caller that then
            // crashed is owed to the next one; persisting only the pending queue would lose exactly the
            // annotations the ack exists to protect.
            WriteState(_path, _sourcePath, _annotations.DurableSnapshot(), _answers.Peek());
        }
    }

    /// <summary>
    /// Read the sidecar at <paramref name="path"/> into its queued annotations and answers, or
    /// <see cref="State.Empty"/> when the file is missing, empty, or unreadable — a corrupt hint must never
    /// break server start or <c>resolve</c>.
    /// </summary>
    public static State Rehydrate(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return State.Empty;
        }

        try
        {
            if (!File.Exists(path))
            {
                return State.Empty;
            }

            var document = JsonSerializer.Deserialize<SidecarDocument>(File.ReadAllText(path), SidecarJson);
            if (document is null)
            {
                return State.Empty;
            }

            return new State(
                document.Annotations ?? Array.Empty<Annotation>(),
                document.Answers ?? Array.Empty<Answer>(),
                document.PlanHash);
        }
        catch (Exception)
        {
            // Corrupt / partially written / unreadable sidecar: treat as empty, never fatal.
            return State.Empty;
        }
    }

    /// <summary>
    /// Atomically persist <paramref name="annotations"/> and <paramref name="answers"/> for the plan at
    /// <paramref name="sourcePath"/> to <paramref name="path"/>, or DELETE the file when both are empty (a
    /// fully-drained session leaves no husk). The temp file is written in the sidecar's own directory and
    /// renamed over the target, so a concurrent reader (the server rehydrating, or <c>resolve</c>) sees a
    /// complete old-or-new file. Exposed <c>static</c> so <c>charter resolve</c> can clear the answers it
    /// applied in the no-live-server (solo) case.
    /// </summary>
    public static void WriteState(
        string path, string sourcePath, IReadOnlyList<Annotation> annotations, IReadOnlyList<Answer> answers)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(answers);

        if (annotations.Count == 0 && answers.Count == 0)
        {
            Delete(path);
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            StateDirectory.EnsureOwnerOnlyDirectory(directory);
        }

        // Fingerprint the plan as it is at this write. Best-effort by design: a plan that cannot be read yields
        // no hash, and IsStale then falls back to the anchor test, which is the rule that actually decides.
        var document = new SidecarDocument(
            CurrentSchema, sourcePath, annotations, answers, HashOfFile(sourcePath));
        var json = JsonSerializer.Serialize(document, SidecarJson);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, OwnerOnlyFile);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="state"/>'s queued annotations belong to a <b>different document</b> than the one
    /// now at the plan's path — the Charter #67 case, where a <c>.charter.md</c> was deleted and a substantially
    /// different plan was authored at the same path, and the path-keyed sidecar rehydrated the dead queue into
    /// the new document's review.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule, exactly:</b> the sidecar holds at least one annotation, the plan is not byte-identical to
    /// the revision the queue was last written against, and <b>not one</b> of those annotations' anchors
    /// resolves in the plan as it is now.
    /// </para>
    /// <para>
    /// Orphaning after an edit is normal and expected — that is the living-document model, and a queue where
    /// <i>some</i> anchors still resolve is an edited plan, never a replaced one, so it is delivered untouched.
    /// It takes the total absence of any surviving anchor to conclude "this is not the document these notes
    /// were written on", which is the weakest claim that still catches the reported bug.
    /// </para>
    /// <para>
    /// <b>The hash is an exemption, not the test.</b> A plan byte-identical to the one the queue was written
    /// against is definitionally the same document, so it can never be stale — which is what keeps an
    /// annotation that could never resolve (an empty or unknown anchor id) from quarantining an untouched plan.
    /// A schema-1 sidecar carries no hash and simply falls through to the anchor test.
    /// </para>
    /// <para>
    /// <b>False positive:</b> a reviewer's notes all target blocks the agent then rewrites in one pass, so no
    /// anchor survives. Nothing is destroyed — the queue is moved aside by <see cref="Quarantine"/>, named, and
    /// restorable — but it is not handed off until the reviewer says so.
    /// <b>False negative:</b> the replacement happens to contain a block whose content-derived id matches one of
    /// the old anchors (an identical heading, a boilerplate status line). One survivor is enough to make the
    /// whole queue look live, and it is delivered exactly as before.
    /// </para>
    /// </remarks>
    public static bool IsStale(State state, string currentMarkdown)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Annotations.Count == 0)
        {
            return false;
        }

        if (state.PlanHash is not null &&
            string.Equals(state.PlanHash, Hash(currentMarkdown ?? string.Empty), StringComparison.Ordinal))
        {
            return false;
        }

        var map = SourceMap.Build(currentMarkdown ?? string.Empty);
        return state.Annotations.All(annotation => map.LineForAnchor(annotation.AnchorId) is null);
    }

    /// <summary>
    /// Move a stale queue aside: copy the whole sidecar to a timestamped sibling and rewrite the live sidecar
    /// carrying only <paramref name="state"/>'s answers. Returns the path the old queue was preserved at, or
    /// <see langword="null"/> when it could not be preserved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is deleted.</b> Silently destroying a reviewer's notes is the failure mode this project has
    /// been bitten by before, so the queue is preserved as a file the notice can name and the reviewer can
    /// restore from. The copy happens FIRST and the live sidecar is rewritten only if it succeeded — a caller
    /// that gets <see langword="null"/> must leave the sidecar alone entirely (see
    /// <c>ReviewServer.StartCore</c>, which then runs the session with durability off rather than overwriting
    /// notes it could not save).
    /// </para>
    /// <para>
    /// <b>Answers are not quarantined.</b> An answer is keyed by <c>:::question</c> id, not by an anchor, so the
    /// evidence that decided the annotations says nothing about it; it stays queued and is still applied by
    /// <c>charter resolve</c> / <c>poll --apply</c> when its question still exists.
    /// </para>
    /// </remarks>
    public static string? Quarantine(string path, string sourcePath, State state)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var preserved = QuarantinePath(path);
            File.Copy(path, preserved, overwrite: false);
            WriteState(path, sourcePath, Array.Empty<Annotation>(), state.Answers);

            // Retire only what is BOTH aged out and superseded — and only after this copy is on disk, so the
            // queue being set aside right now is always the protected newest one.
            PruneQuarantined(path);
            return preserved;
        }
        catch (Exception)
        {
            // Could not preserve the queue (permissions, a full disk, a racing writer). Report the failure by
            // returning null; the caller must NOT clear a queue it could not save.
            return null;
        }
    }

    /// <summary>
    /// The reviewer's explicit KEEP: fold every queue <see cref="Quarantine"/> preserved beside the sidecar at
    /// <paramref name="path"/> back into <paramref name="live"/>, and name the files it read so the caller can
    /// retire them <b>after</b> persisting the merged queue.
    /// </summary>
    /// <remarks>
    /// Annotations already present in the live queue (by id) are not duplicated, so keeping twice is a no-op.
    /// Answers are taken from the live sidecar only — <see cref="Quarantine"/> never took any away. Unreadable
    /// preserved files are skipped and left on disk rather than reported as empty.
    /// </remarks>
    public static (State Merged, IReadOnlyList<string> Sources) Reclaim(string path, State live)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(live);

        var merged = new List<Annotation>(live.Annotations);
        var seen = new HashSet<string>(merged.Select(a => a.Id), StringComparer.Ordinal);
        var sources = new List<string>();

        foreach (var preserved in Quarantined(path))
        {
            var state = Rehydrate(preserved);
            if (state.Annotations.Count == 0)
            {
                continue;
            }

            foreach (var annotation in state.Annotations.Where(a => seen.Add(a.Id)))
            {
                merged.Add(annotation);
            }

            sources.Add(preserved);
        }

        return sources.Count == 0
            ? (live, Array.Empty<string>())
            : (live with { Annotations = merged }, sources);
    }

    /// <summary>
    /// Best-effort removal of preserved queues whose contents have been folded back in and persisted. Called
    /// only after the merged queue is durable, so this can never be the step that loses a note.
    /// </summary>
    public static void RetireQuarantined(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            Delete(path);
        }
    }

    // Every preserved queue beside the sidecar at `path`, in a stable order (the names are timestamped, so
    // ordinal order is chronological).
    private static IReadOnlyList<string> Quarantined(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            var stem = Path.GetFileName(path);
            stem = stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? stem[..^5] : stem;
            var found = Directory.GetFiles(directory, stem + QuarantineMarker + "*.json");
            Array.Sort(found, StringComparer.Ordinal);
            return found;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    // A free sibling path for the preserved queue: <sidecar>.stale-<utc timestamp>.json, disambiguated with a
    // short random suffix on the (rare) same-second collision so a second quarantine never overwrites a first.
    private static string QuarantinePath(string path)
    {
        var stem = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path[..^5] : path;
        var stamp = DateTimeOffset.UtcNow.ToString(QuarantineStampFormat, CultureInfo.InvariantCulture);
        var candidate = stem + QuarantineMarker + stamp + ".json";
        return File.Exists(candidate)
            ? stem + QuarantineMarker + stamp + "-" + Guid.NewGuid().ToString("N")[..8] + ".json"
            : candidate;
    }

    /// <summary>
    /// Delete every preserved queue beside the sidecar at <paramref name="path"/> that is BOTH older than
    /// <see cref="QuarantineRetention"/> and superseded by at least one newer one. Best-effort and total: a file
    /// whose stamp cannot be read is left alone (an unreadable age is not evidence of an expired one), and any
    /// I/O failure simply leaves the file — an extra set-aside queue costs disk, whereas a wrong delete costs a
    /// reviewer's notes.
    /// </summary>
    private static void PruneQuarantined(string path)
    {
        try
        {
            // Chronological (the names are timestamped and sorted ordinally), so everything except the LAST
            // entry has been superseded.
            var preserved = Quarantined(path);
            var cutoff = DateTimeOffset.UtcNow - QuarantineRetention;
            for (var i = 0; i < preserved.Count - 1; i++)
            {
                if (SetAsideAt(preserved[i]) is { } stamp && stamp < cutoff)
                {
                    Delete(preserved[i]);
                }
            }
        }
        catch (Exception)
        {
            // Retention is housekeeping; it must never be the reason a quarantine reports failure.
        }
    }

    /// <summary>
    /// When the queue at <paramref name="preservedPath"/> was set aside, read from its FILE NAME, or
    /// <see langword="null"/> when the name carries no parseable stamp. The name is used rather than the file's
    /// mtime because <see cref="File.Copy(string, string, bool)"/> inherits the source sidecar's timestamp, which
    /// says when the queue was last written, not when it was quarantined.
    /// </summary>
    private static DateTimeOffset? SetAsideAt(string preservedPath)
    {
        var name = Path.GetFileName(preservedPath);
        var marker = name.IndexOf(QuarantineMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var start = marker + QuarantineMarker.Length;
        if (name.Length < start + QuarantineStampLength)
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            name.Substring(start, QuarantineStampLength),
            QuarantineStampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The plan fingerprint: <c>sha256</c> of the markdown, lowercase hex.</summary>
    private static string Hash(string markdown)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant();

    /// <summary>The fingerprint of the plan at <paramref name="sourcePath"/>, or null when it cannot be read.</summary>
    private static string? HashOfFile(string sourcePath)
    {
        try
        {
            return string.IsNullOrEmpty(sourcePath) ? null : Hash(File.ReadAllText(sourcePath));
        }
        catch (Exception)
        {
            // An unreadable plan simply has no fingerprint this write; IsStale falls back to the anchor test.
            return null;
        }
    }

    /// <summary>Best-effort delete of the sidecar (or a leftover temp) at <paramref name="path"/>.</summary>
    private static void Delete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A leftover sidecar is harmless — it rehydrates as state, or is overwritten on the next persist.
        }
    }

    /// <summary>
    /// The on-disk sidecar shape: schema, the plan it belongs to, the two queues, and the plan's content
    /// fingerprint at the time of the write (schema 2; absent and deserialized as null on a schema-1 file).
    /// </summary>
    private sealed record SidecarDocument(
        int Schema,
        string SourcePath,
        IReadOnlyList<Annotation> Annotations,
        IReadOnlyList<Answer> Answers,
        string? PlanHash = null);
}
