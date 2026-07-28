using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Charter.Server;

/// <summary>
/// Which review-log records THIS MACHINE's agent has already been handed, for one plan. Machine-local by
/// construction: it lives under the per-user state directory and is <b>never</b> a log record
/// (<c>docs/plans/03-git-mediated-team-review.md</c> §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it cannot be in the log.</b> The log is the shared transport — every teammate has every record. If
/// "consumed" were a record, A's agent draining a comment would mark it handled for B, whose agent has never
/// seen it. Consumption is a property of one machine's handoff, not of the review.
/// </para>
/// <para>
/// <b>Why it is a separate file rather than the review sidecar.</b> The sidecar deliberately deletes itself
/// once its queues drain (it leaves no husk), which would erase the ledger at exactly the moment it starts
/// mattering.
/// </para>
/// <para>
/// A missing or corrupt ledger reads as EMPTY — the worst outcome is re-delivering a comment the agent has
/// already seen, which is recoverable; refusing to deliver one it has not is not.
/// </para>
/// </remarks>
public sealed class ReviewLogLedger
{
    private const int CurrentSchema = 1;

    // 0600 — the ledger reflects one user's handoff history and belongs to that user alone.
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions LedgerJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly HashSet<string> _consumed;
    private readonly string _path;
    private readonly string _planPath;

    private ReviewLogLedger(string path, string planPath, HashSet<string> consumed)
    {
        _path = path;
        _planPath = planPath;
        _consumed = consumed;
    }

    /// <summary>How many record ids this machine has recorded as delivered.</summary>
    public int Count => _consumed.Count;

    /// <summary>
    /// The ledger file for <paramref name="planPath"/> inside <paramref name="directory"/>:
    /// <c>&lt;sha256(full path)&gt;.consumed.json</c> — the same keying the review sidecar uses, so both
    /// resolve from a plan path alone.
    /// </summary>
    public static string PathForPlan(string directory, string planPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(planPath);

        var canonical = Path.GetFullPath(planPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return Path.Combine(directory, hash + ".consumed.json");
    }

    /// <summary>Load the ledger for <paramref name="planPath"/> from <paramref name="directory"/>.</summary>
    public static ReviewLogLedger Load(string directory, string planPath)
    {
        var path = PathForPlan(directory, planPath);
        var canonical = Path.GetFullPath(planPath);

        try
        {
            if (File.Exists(path))
            {
                var document = JsonSerializer.Deserialize<LedgerDocument>(File.ReadAllText(path), LedgerJson);
                if (document?.Consumed is not null)
                {
                    return new ReviewLogLedger(
                        path, canonical, new HashSet<string>(document.Consumed, StringComparer.Ordinal));
                }
            }
        }
        catch (Exception)
        {
            // Corrupt / partially written / unreadable: an empty ledger re-delivers, which is the safe error.
        }

        return new ReviewLogLedger(path, canonical, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>Whether every id in <paramref name="recordIds"/> has already been delivered on this machine.</summary>
    public bool HasConsumedAll(IEnumerable<string> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        return recordIds.All(_consumed.Contains);
    }

    /// <summary>Record <paramref name="recordIds"/> as delivered. Call <see cref="Save"/> to persist.</summary>
    public void MarkConsumed(IEnumerable<string> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        foreach (var id in recordIds.Where(id => !string.IsNullOrEmpty(id)))
        {
            _consumed.Add(id);
        }
    }

    /// <summary>
    /// Persist atomically (temp + rename in the same directory, mirroring the review sidecar), so a crash
    /// mid-write leaves the previous complete ledger rather than a truncated one.
    /// </summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            StateDirectory.EnsureOwnerOnlyDirectory(directory);
        }

        var json = JsonSerializer.Serialize(
            new LedgerDocument(
                CurrentSchema, _planPath, _consumed.OrderBy(id => id, StringComparer.Ordinal).ToArray()),
            LedgerJson);

        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, OwnerOnlyFile);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A leftover temp file is harmless; the next save overwrites the target regardless.
        }
    }

    /// <summary>The on-disk shape: schema, the plan it belongs to, and the delivered record ids.</summary>
    private sealed record LedgerDocument(int Schema, string PlanPath, IReadOnlyList<string> Consumed);
}
