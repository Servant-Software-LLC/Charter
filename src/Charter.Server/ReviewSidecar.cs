using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    private const int CurrentSchema = 1;

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
    public sealed record State(IReadOnlyList<Annotation> Annotations, IReadOnlyList<Answer> Answers)
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
            WriteState(_path, _sourcePath, _annotations.Snapshot(), _answers.Peek());
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
                document.Answers ?? Array.Empty<Answer>());
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

        var document = new SidecarDocument(CurrentSchema, sourcePath, annotations, answers);
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

    /// <summary>The on-disk sidecar shape: schema, the plan it belongs to, and the two queues.</summary>
    private sealed record SidecarDocument(
        int Schema,
        string SourcePath,
        IReadOnlyList<Annotation> Annotations,
        IReadOnlyList<Answer> Answers);
}
