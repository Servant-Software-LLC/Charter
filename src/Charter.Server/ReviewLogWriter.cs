using System.Globalization;
using System.Text;
using Charter.Core;

namespace Charter.Server;

/// <summary>
/// Appends records to ONE author's per-plan review log — the writer half of the git-mediated team review
/// (<c>docs/plans/03-git-mediated-team-review.md</c> §3, which is normative). Every rule it follows exists
/// because the merge spike reproduced the failure it prevents.
/// </summary>
/// <remarks>
/// <para>
/// <b>One JSON object per line, never pretty-printed.</b> Pretty-printed JSON under a union merge FUSES
/// records; on a single-object <c>.json</c> that fusion is silent. <see cref="ReviewRecord.ToJson"/> is the
/// single serializer, and it is compact and ASCII-escaped, so a raw control byte — in particular the NUL that
/// would make git treat the log as binary and bypass the merge driver entirely — can never reach the file.
/// <see cref="Append(ReviewRecord)"/> re-checks that as a cheap guard rather than trusting it.
/// </para>
/// <para>
/// <b>The last byte is read before every append.</b> Union merge itself never concatenated two records, but a
/// merge can leave the file WITHOUT a trailing newline (inheriting the sloppy side's state) and then the next
/// append fuses two records. So the rule is "read the last byte; if it is not <c>\n</c>, write one first" —
/// not "always write a trailing newline", which a reviewer tried and still corrupted the log.
/// </para>
/// <para>
/// <b>Ids are globally-unique and random.</b> A counter lets two teammates working offline mint the same id,
/// and the fold dedupes BY id — so a counter turns two different comments into one
/// <c>ConflictingDuplicate</c>.
/// </para>
/// <para>
/// <b>The concurrency and durability guarantee, stated exactly.</b> The record is written by ONE
/// <see cref="Stream.Write(byte[], int, int)"/> of the complete line into an <b>append-mode</b> handle opened
/// <see cref="FileShare.None"/>, followed by <c>Flush(flushToDisk: true)</c> (FlushFileBuffers / fsync) before
/// the handle closes. That is deliberately belt AND braces, because the two platforms guarantee different
/// halves: on Windows <see cref="FileShare.None"/> is a mandatory share-mode lock, so no second writer can
/// even open the log; on POSIX <see cref="FileMode.Append"/> is <c>O_APPEND</c>, which positions every write
/// at end-of-file atomically with the write itself, so two writers cannot land on the same offset — and .NET
/// additionally takes an advisory <c>flock</c> there. Relying on either alone would be a portability bug. A
/// writer that finds the log held retries with backoff for <see cref="LockRetryBudget"/> and then throws, so
/// a comment is never silently dropped.
/// </para>
/// <para>
/// <b>What is NOT claimed.</b> A power loss mid-append can still leave a truncated tail — no filesystem offers
/// atomic multi-byte appends — and the design answers that rather than preventing it: the fold reports a
/// partial line loudly and non-fatally (rule 5), and the next append's last-byte check repairs the file. The
/// last-byte read also happens on a separate short-lived handle, so a second writer that appends in the gap
/// could at worst cause a redundant repair newline — a blank line, which the fold ignores.
/// </para>
/// </remarks>
public sealed class ReviewLogWriter
{
    /// <summary>How long an append retries a log another Charter process is holding before giving up.</summary>
    public static readonly TimeSpan LockRetryBudget = TimeSpan.FromSeconds(5);

    // Back-off bounds for the retry: start immediate-ish (the lock is held for microseconds) and cap low
    // enough that a contended append still completes promptly.
    private const int InitialRetryDelayMs = 1;
    private const int MaxRetryDelayMs = 25;

    // Per-op id prefixes, so a record id is legible in a PR diff ("cmt_…" is a comment). The 32 hex digits
    // after it are Guid.NewGuid() — 122 random bits, minted independently on every machine.
    private static readonly IReadOnlyDictionary<ReviewOpKind, string> IdPrefixes =
        new Dictionary<ReviewOpKind, string>
        {
            [ReviewOpKind.Create] = "cmt_",
            [ReviewOpKind.Reply] = "rpl_",
            [ReviewOpKind.Edit] = "edt_",
            [ReviewOpKind.Resolve] = "rsv_",
            [ReviewOpKind.Reopen] = "rop_",
            [ReviewOpKind.Retract] = "rtr_",
        };

    // Serializes appends made through THIS writer (the review server calls it from many request threads).
    // Cross-process and cross-instance exclusion is the file lock's job, not this one's.
    private readonly object _gate = new();

    // Whether this writer has already appended a record (and therefore already fired OnFirstRecordWritten).
    // Guarded by _gate.
    private bool _written;

    /// <summary>
    /// Bind a writer to <paramref name="author"/>'s log for the plan at <paramref name="planPath"/>. Creates
    /// nothing: the <c>.review/</c> directory comes into existence only when a record is actually appended.
    /// That laziness is binding, not incidental — §5.0 of the design of record keeps solo the primary use case,
    /// and a reviewer who opens a plan and writes nothing must leave no trace beside their file.
    /// </summary>
    public ReviewLogWriter(string planPath, ReviewAuthor author)
    {
        ArgumentException.ThrowIfNullOrEmpty(planPath);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentException.ThrowIfNullOrEmpty(author.Email);

        Author = author;
        ReviewDirectory = ReviewLogPaths.DirectoryForPlan(planPath);
        LogPath = Path.Combine(ReviewDirectory, ReviewLogPaths.FileNameForAuthor(author.Email));
    }

    /// <summary>The author every record this writer appends is attributed to.</summary>
    public ReviewAuthor Author { get; }

    /// <summary>The plan's review-log directory — one file per author lives here.</summary>
    public string ReviewDirectory { get; }

    /// <summary>This author's log file. Created on the first append.</summary>
    public string LogPath { get; }

    /// <summary>
    /// Invoked exactly once per writer, immediately after its FIRST successful append — the moment the
    /// <c>.review/</c> directory and this author's log actually come into existence. It exists so the CLI can
    /// state §7's permanence notice <b>when it becomes relevant</b> rather than as a per-session banner: before
    /// the first record there is nothing permanent to warn about, and for a solo reviewer who writes nothing
    /// there never is. Never invoked when an append fails.
    /// </summary>
    public Action? OnFirstRecordWritten { get; set; }

    /// <summary>A fresh, globally-unique record id for <paramref name="op"/>.</summary>
    public static string NewId(ReviewOpKind op)
        => (IdPrefixes.TryGetValue(op, out var prefix) ? prefix : "rec_") + Guid.NewGuid().ToString("N");

    /// <summary>Open a comment on <paramref name="anchor"/> (§4.3 carries the quote so an orphan is never blind).</summary>
    public ReviewRecord AppendCreate(ReviewAnchor anchor, string body, string actor = ReviewActors.Human)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return Append(NewRecord(ReviewOpKind.Create, actor) with { Anchor = anchor, Body = body ?? string.Empty });
    }

    /// <summary>Replace the body of <paramref name="target"/>. <paramref name="prev"/> is what its author had observed (§4.2).</summary>
    public ReviewRecord AppendEdit(string target, string body, string? prev, string actor = ReviewActors.Human)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        return Append(NewRecord(ReviewOpKind.Edit, actor) with
        {
            Target = target,
            Prev = prev,
            Body = body ?? string.Empty,
        });
    }

    /// <summary>
    /// Reply in <paramref name="target"/>'s thread. Open to anyone — review is collaborative (§4.2).
    /// <para>
    /// This is how an <b>agent</b> answers a reviewer's comment (<see cref="ReviewActors.Agent"/>): accept it,
    /// push back on it, or ask for clarification — <b>without touching the plan file</b>. The plan stays
    /// single-writer and the server writes nothing; the agent appends to its own author log, and the
    /// reviewer's open page picks it up over the existing review-log watch. Before this, an agent that
    /// disagreed with a comment had no way to say so — it revised the plan or it did not, and those two look
    /// identical to the reviewer (issue #106).
    /// </para>
    /// <para>
    /// A reply is <b>not</b> a state op (<see cref="ReviewOps.IsStateOp"/>), so it carries no <c>prev</c>: it
    /// adds to the thread, it does not settle the item. Deciding a comment is <i>done</i> stays a separate,
    /// deliberate <c>resolve</c>.
    /// </para>
    /// </summary>
    public ReviewRecord AppendReply(string target, string body, string actor = ReviewActors.Human)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        return Append(NewRecord(ReviewOpKind.Reply, actor) with
        {
            Target = target,
            Body = body ?? string.Empty,
        });
    }

    /// <summary>Close <paramref name="target"/>. Open to anyone — review is collaborative (§4.2).</summary>
    public ReviewRecord AppendResolve(string target, string? prev, string actor = ReviewActors.Human)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        return Append(NewRecord(ReviewOpKind.Resolve, actor) with { Target = target, Prev = prev });
    }

    /// <summary>
    /// Withdraw <paramref name="target"/>. Valid only from the item's OWN author — a retract by anyone else is
    /// retained and reported by the fold but never applied, or a teammate could silently delete a blocking
    /// objection (§4.2). The caller checks authorship; this only writes what it is given.
    /// </summary>
    public ReviewRecord AppendRetract(string target, string? prev, string actor = ReviewActors.Human)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        return Append(NewRecord(ReviewOpKind.Retract, actor) with { Target = target, Prev = prev });
    }

    /// <summary>
    /// Append <paramref name="record"/> as one line, creating the <c>.review/</c> directory on demand.
    /// Returns the record it wrote, so a caller can report the minted id.
    /// </summary>
    public ReviewRecord Append(ReviewRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var line = record.ToJson();
        GuardLine(line);

        bool announce;
        lock (_gate)
        {
            Directory.CreateDirectory(ReviewDirectory);
            AppendLine(LogPath, line);

            // Only after the append actually landed, and only the first time: this is what makes the CLI's
            // permanence notice a one-line fact about a file that now exists rather than a per-session banner.
            announce = !_written;
            _written = true;
        }

        if (announce)
        {
            OnFirstRecordWritten?.Invoke();
        }

        return record;
    }

    /// <summary>A record of <paramref name="op"/> with a fresh id, this writer's author, and a UTC stamp.</summary>
    private ReviewRecord NewRecord(ReviewOpKind op, string actor) => new()
    {
        Version = ReviewRecord.CurrentVersion,
        Id = NewId(op),
        Op = ReviewOps.Token(op),
        Author = Author,
        Actor = string.IsNullOrEmpty(actor) ? ReviewActors.Human : actor,
        // Presentation only — the fold NEVER uses ts for causality (rule 3). Millisecond precision so a
        // panel's display order matches the order a reviewer typed in, invariant across machines and cultures.
        Ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Refuse to write a line carrying a raw control byte or a newline. <see cref="ReviewRecord.ToJson"/>
    /// escapes both, so this can only fire if that ever changes — and it must fire loudly, because a NUL makes
    /// git treat the log as binary (bypassing the merge driver) and a raw newline splits one record into two
    /// malformed ones.
    /// </summary>
    private static void GuardLine(string line)
    {
        foreach (var c in line)
        {
            if (char.IsControl(c))
            {
                throw new InvalidOperationException(
                    "A review-log line may not contain a raw control character; the record serializer must escape it.");
            }
        }
    }

    /// <summary>
    /// The append itself: repair a missing trailing newline, then write the complete line in one call into an
    /// append-mode, exclusively-opened handle and flush it to disk. Retries only while another writer holds
    /// the log (see the class remarks for the exact guarantee); a failure that outlives the budget propagates
    /// so the caller can tell the reviewer their comment was not saved.
    /// </summary>
    private static void AppendLine(string path, string line)
    {
        var deadline = Environment.TickCount64 + (long)LockRetryBudget.TotalMilliseconds;
        var delayMs = InitialRetryDelayMs;

        while (true)
        {
            try
            {
                var payload = BuildPayload(path, line);

                // FileMode.Append is O_APPEND on POSIX (atomic end-of-file positioning) and FileShare.None is
                // a mandatory lock on Windows. bufferSize 1 means "unbuffered", so the whole payload leaves in
                // ONE write call rather than in whatever chunks a buffer happened to hold.
                using var log = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.None, bufferSize: 1);

                log.Write(payload, 0, payload.Length);
                log.Flush(flushToDisk: true);
                return;
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, MaxRetryDelayMs);
            }
        }
    }

    /// <summary>
    /// The bytes to append: the line plus its terminator, preceded by a repair newline when the file does not
    /// already end in one. Always LF — the shipped <c>.gitattributes</c> pins these logs to <c>eol=lf</c>, and
    /// a CRLF-writing teammate would otherwise fork every line under a union merge.
    /// </summary>
    /// <remarks>
    /// Reads the last byte on its OWN short-lived handle, because an append-mode handle cannot read. A file
    /// that does not exist, or is empty, needs no repair — and a second writer appending in the gap can at
    /// worst cost a redundant blank line, which the fold ignores.
    /// </remarks>
    private static byte[] BuildPayload(string path, string line)
    {
        var needsRepair = false;
        if (File.Exists(path))
        {
            using var log = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (log.Length > 0)
            {
                log.Seek(-1, SeekOrigin.End);
                needsRepair = log.ReadByte() != '\n';
            }
        }

        return Encoding.UTF8.GetBytes(needsRepair ? "\n" + line + "\n" : line + "\n");
    }
}
