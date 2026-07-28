using System;
using System.IO;
using System.Linq;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #75 item 4 — the sidecar deletes itself when it empties ("no husk"), but its <c>.stale-*.json</c>
/// quarantine copies never did, so they accumulated forever under the per-user state dir.
///
/// The retention rule under test is conservative in BOTH directions, because a quarantine copy is the only copy
/// of the notes it holds: a file is retired only once it is <b>older than 30 days</b> <i>and</i> has been
/// <b>superseded by a newer set-aside queue for the same plan</b>. The newest is never retired at any age — it
/// is the one the stderr notice named and the one <c>--keep-annotations</c> restores first. Pruning runs only
/// where the set grows (a successful quarantine), never on a read.
/// </summary>
[Trait("Category", "ReviewSidecar")]
public class QuarantineRetentionTests
{
    private const string OriginalPlan =
        "# Rate limiting\n\nThe read path stays Postgres-only until the write path is proven.\n";

    private const string ReplacementPlan =
        "# Tenant onboarding\n\nEvery tenant gets an isolated schema provisioned at signup time.\n";

    [Fact]
    public void Quarantine_RetiresASupersededFileThatHasAgedOut()
    {
        using var work = new Workspace();
        var ancient = work.PlantQuarantineFile(DateTimeOffset.UtcNow.AddDays(-90));

        work.SeedSidecar();
        Assert.NotNull(ReviewSidecar.Quarantine(work.SidecarPath, work.PlanPath, work.State()));

        Assert.False(File.Exists(ancient), "a 90-day-old, superseded set-aside queue should have been retired.");
        Assert.Single(work.Quarantined());
    }

    [Fact]
    public void Quarantine_KeepsTheNewestFileAtAnyAge_ItMayBeTheOnlyCopy()
    {
        // A reviewer who quarantined once and came back months later must still find their notes exactly where
        // the notice said they were. Age alone is never a reason to delete.
        using var work = new Workspace();
        var ancient = work.PlantQuarantineFile(DateTimeOffset.UtcNow.AddDays(-400));

        // No new quarantine happens here — pruning only runs when the set GROWS — so nothing is even eligible.
        Assert.True(File.Exists(ancient));

        // And even after a quarantine makes it superseded, the copy JUST written survives regardless of the
        // sidecar's own timestamps.
        work.SeedSidecar();
        var preserved = ReviewSidecar.Quarantine(work.SidecarPath, work.PlanPath, work.State());
        Assert.NotNull(preserved);
        Assert.True(File.Exists(preserved!), "the queue just set aside is the newest and must never be pruned.");
    }

    [Fact]
    public void Quarantine_KeepsARecentSupersededFile()
    {
        // Superseded but young: within the retention window a reviewer may still want the earlier queue, and
        // the accumulation this bounds is measured in months, not minutes.
        using var work = new Workspace();
        var recent = work.PlantQuarantineFile(DateTimeOffset.UtcNow.AddDays(-3));

        work.SeedSidecar();
        Assert.NotNull(ReviewSidecar.Quarantine(work.SidecarPath, work.PlanPath, work.State()));

        Assert.True(File.Exists(recent), "a 3-day-old set-aside queue is well inside the retention window.");
        Assert.Equal(2, work.Quarantined().Count);
    }

    [Fact]
    public void Quarantine_LeavesAForeignFileAlone_AnUnreadableAgeIsNotAnExpiredOne()
    {
        using var work = new Workspace();
        var unparseable = work.SidecarPath[..^5] + ".stale-not-a-timestamp.json";
        File.WriteAllText(unparseable, "{}");

        work.SeedSidecar();
        Assert.NotNull(ReviewSidecar.Quarantine(work.SidecarPath, work.PlanPath, work.State()));

        Assert.True(File.Exists(unparseable), "a name whose stamp cannot be read carries no evidence of age.");
    }

    [Fact]
    public void Reclaim_StillFindsEveryQueueRetentionKept()
    {
        // Retention must not quietly narrow what --keep-annotations can restore: whatever survives the prune is
        // still folded back in.
        using var work = new Workspace();
        work.PlantQuarantineFile(DateTimeOffset.UtcNow.AddDays(-2), annotationId: "recent-note");

        var (merged, sources) = ReviewSidecar.Reclaim(
            work.SidecarPath, new ReviewSidecar.State(Array.Empty<Annotation>(), Array.Empty<Answer>()));

        Assert.Single(sources);
        Assert.Contains(merged.Annotations, annotation => annotation.Id == "recent-note");
    }

    // ---- Plumbing ----------------------------------------------------------------------------------------

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "charter-retention-" + Guid.NewGuid().ToString("N"));

        public Workspace()
        {
            Directory.CreateDirectory(_root);
            SidecarDirectory = Path.Combine(_root, "sidecars");
            Directory.CreateDirectory(SidecarDirectory);

            PlanPath = Path.Combine(_root, "plan.charter.md");
            File.WriteAllText(PlanPath, ReplacementPlan);
            SidecarPath = ReviewSidecar.PathForPlan(SidecarDirectory, PlanPath);
        }

        public string SidecarDirectory { get; }

        public string PlanPath { get; }

        public string SidecarPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        /// <summary>A live sidecar holding a queue that <see cref="ReviewSidecar.Quarantine"/> can set aside.</summary>
        public void SeedSidecar()
            => ReviewSidecar.WriteState(SidecarPath, PlanPath, State().Annotations, Array.Empty<Answer>());

        public ReviewSidecar.State State()
            => new(new[] { new Annotation("a-1", AnnotationKind.Element, "b0000", "a queued note") },
                Array.Empty<Answer>());

        /// <summary>
        /// Write a quarantine file whose NAME says it was set aside at <paramref name="setAsideAt"/> — the
        /// stamp the retention rule reads, and the only clock that survives a File.Copy.
        /// </summary>
        public string PlantQuarantineFile(DateTimeOffset setAsideAt, string annotationId = "planted-note")
        {
            var stamp = setAsideAt.UtcDateTime.ToString(
                "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var path = SidecarPath[..^5] + ".stale-" + stamp + ".json";

            ReviewSidecar.WriteState(
                path,
                PlanPath,
                new[] { new Annotation(annotationId, AnnotationKind.Element, "b0000", "a planted note") },
                Array.Empty<Answer>());
            return path;
        }

        public IReadOnlyList<string> Quarantined()
            => Directory.GetFiles(SidecarDirectory, "*.stale-*.json").OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
}
