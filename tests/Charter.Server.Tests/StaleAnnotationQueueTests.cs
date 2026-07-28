using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Charter.Core;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #67: the durability sidecar is keyed by <c>sha256(absolute plan path)</c>, so deleting a
/// <c>.charter.md</c> and authoring a <b>substantially different</b> plan at the same path used to rehydrate the
/// dead document's queue into the new document's review — including annotations Charter's own anchor resolution
/// had already marked <c>orphaned</c>.
///
/// The rule under test (<see cref="ReviewSidecar.IsStale"/>): a queue belongs to a different document only when
/// it holds annotations, the plan is not byte-identical to the revision the queue was written against, and NOT
/// ONE of its anchors resolves in the plan as it is now. An ordinary edit — some anchors resolve, some orphan —
/// is the living-document model working, and must keep every note.
///
/// The remedy is surfacing, never deletion: the queue is set aside to a named file, reported on
/// <see cref="ReviewServer.StaleAnnotations"/>, and restorable with <c>--keep-annotations</c>.
/// </summary>
[Trait("Category", "ReviewSidecar")]
public class StaleAnnotationQueueTests
{
    // Three anchorable blocks. The heading is deliberately shared with ReplacementSharingABlock below, which is
    // how the FALSE NEGATIVE this rule accepts is pinned as a fact rather than left as a claim.
    private const string OriginalPlan =
        "# Rate limiting\n\n" +
        "The read path stays Postgres-only until the write path is proven.\n\n" +
        "The pinned version is resolved from the launcher's registry policy.\n";

    // The same first paragraph survives; the last is rewritten. An ordinary revision.
    private const string EditedPlan =
        "# Rate limiting\n\n" +
        "The read path stays Postgres-only until the write path is proven.\n\n" +
        "The maximum version is resolved from a completely rewritten policy sentence.\n";

    // Nothing in common: a different document that happens to have been written at the same path.
    private const string ReplacementPlan =
        "# Tenant onboarding\n\n" +
        "Every tenant gets an isolated schema provisioned at signup time.\n\n" +
        "Billing is reconciled nightly against the metering ledger.\n";

    // A different document that nevertheless reuses one block verbatim (a shared heading is the realistic
    // case). One surviving anchor is enough to make the queue look live — the rule's accepted false negative.
    private const string ReplacementSharingABlock =
        "# Rate limiting\n\n" +
        "Every tenant gets an isolated schema provisioned at signup time.\n";

    // ---- the rule itself ---------------------------------------------------------------------------------

    [Fact]
    public void IsStale_EditedPlanKeepingOneAnchor_IsNotStale()
    {
        Assert.False(
            ReviewSidecar.IsStale(SeededState(OriginalPlan), EditedPlan),
            "an ordinary edit orphans some anchors and keeps others; that is the living-document model, not a replacement.");
    }

    [Fact]
    public void IsStale_ReplacedPlanWithNoSurvivingAnchor_IsStale()
        => Assert.True(ReviewSidecar.IsStale(SeededState(OriginalPlan), ReplacementPlan));

    [Fact]
    public void IsStale_ByteIdenticalPlan_IsNeverStale_EvenWithUnresolvableAnchors()
    {
        // An annotation whose anchor can never resolve (an empty id, or a diagram node whose block was not
        // found) would otherwise quarantine a plan nobody has touched. The fingerprint exemption forecloses it.
        var state = SeededState(
            OriginalPlan, new[] { Note("a-1", "no-such-block"), Note("a-2", "also-missing") });

        Assert.False(ReviewSidecar.IsStale(state, OriginalPlan));
    }

    [Fact]
    public void IsStale_EmptyQueue_IsNeverStale()
        => Assert.False(
            ReviewSidecar.IsStale(SeededState(OriginalPlan, Array.Empty<Annotation>()), ReplacementPlan));

    [Fact]
    public void IsStale_ReplacementSharingOneBlock_IsNotStale_TheAcceptedFalseNegative()
    {
        // Documented behaviour, asserted so it can never change silently: one surviving anchor makes the whole
        // queue look live. The rule is deliberately the weakest one that still catches #67.
        Assert.False(ReviewSidecar.IsStale(SeededState(OriginalPlan), ReplacementSharingABlock));
    }

    [Fact]
    public void IsStale_SidecarWithNoFingerprint_StillJudgesByAnchors()
    {
        // A sidecar written by a pre-#67 Charter carries no planHash. The anchor test is the actual rule, so it
        // must still reach the right verdict on both sides without one.
        var unfingerprinted = new ReviewSidecar.State(
            AnnotationsFor(OriginalPlan), Array.Empty<Answer>(), PlanHash: null);

        Assert.False(ReviewSidecar.IsStale(unfingerprinted, EditedPlan));
        Assert.True(ReviewSidecar.IsStale(unfingerprinted, ReplacementPlan));
    }

    // ---- the server: what a replaced plan actually does to a review session ------------------------------

    [Fact]
    public async Task EditedPlan_KeepsItsQueuedAnnotations()
    {
        await RunAsync(OriginalPlan, EditedPlan, async (server, session, sidecarPath) =>
        {
            Assert.Null(server.StaleAnnotations);
            Assert.Equal(3, await DrainCountAsync(server, session));
        });
    }

    [Fact]
    public async Task ReplacedPlan_DoesNotSilentlyDeliverTheOldQueue()
    {
        await RunAsync(OriginalPlan, ReplacementPlan, async (server, session, sidecarPath) =>
        {
            // Nothing is handed off...
            Assert.Equal(0, await DrainCountAsync(server, session));

            // ...the fact is SURFACED rather than swallowed...
            var stale = Assert.IsType<StaleAnnotationQueue>(server.StaleAnnotations);
            Assert.Equal(3, stale.Count);
            Assert.False(stale.DurabilityDisabled);

            // ...and the reviewer's notes still exist, at the path the notice names.
            Assert.True(File.Exists(stale.PreservedAt), "a stale queue is set aside, never destroyed.");
            var preserved = ReviewSidecar.Rehydrate(stale.PreservedAt);
            Assert.Equal(3, preserved.Annotations.Count);
            Assert.Contains(preserved.Annotations, a => a.Note == "note for a-0");

            // The live sidecar now holds no annotations, so the next persist cannot overwrite them.
            Assert.Empty(ReviewSidecar.Rehydrate(sidecarPath).Annotations);
        });
    }

    [Fact]
    public async Task ReplacedPlan_PreservesQueuedAnswers()
    {
        // The evidence that condemned the annotations (no anchor resolves) says nothing about an answer, which
        // is keyed by :::question id. Quarantining one must never take the other with it.
        var directory = NewTempDir();
        try
        {
            var (planPath, sidecarDir, sidecarPath) = Seed(
                directory,
                OriginalPlan,
                AnnotationsFor(OriginalPlan),
                new[] { new Answer("q-1", "single", new[] { "A" }, "human") });

            await File.WriteAllTextAsync(planPath, ReplacementPlan);

            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(session, OptionsFor(sidecarDir));

            Assert.NotNull(server.StaleAnnotations);
            Assert.Equal(0, await DrainCountAsync(server, session));

            var live = ReviewSidecar.Rehydrate(sidecarPath);
            Assert.Empty(live.Annotations);
            Assert.Single(live.Answers);
        }
        finally
        {
            TryDeleteDir(directory);
        }
    }

    [Fact]
    public async Task KeepStaleAnnotations_RestoresTheSetAsideQueue_AndRetiresTheFile()
    {
        var directory = NewTempDir();
        try
        {
            var (planPath, sidecarDir, sidecarPath) = Seed(
                directory, OriginalPlan, AnnotationsFor(OriginalPlan), Array.Empty<Answer>());
            await File.WriteAllTextAsync(planPath, ReplacementPlan);

            string preservedAt;
            using (var quarantining = ReviewServer.Start(ReviewSession.Create(planPath), OptionsFor(sidecarDir)))
            {
                preservedAt = Assert.IsType<StaleAnnotationQueue>(quarantining.StaleAnnotations).PreservedAt;
            }

            // The reviewer says "this really is the same document": the queue comes back...
            var keepOptions = OptionsFor(sidecarDir);
            keepOptions.KeepStaleAnnotations = true;
            var session = ReviewSession.Create(planPath);
            using (var keeping = ReviewServer.Start(session, keepOptions))
            {
                Assert.Null(keeping.StaleAnnotations);
                Assert.Equal(3, await DrainCountAsync(keeping, session));
            }

            // ...it was made durable in the LIVE sidecar before the set-aside copy was retired, so the notes
            // existed in exactly one place at every instant of the hand-back.
            Assert.False(File.Exists(preservedAt), "a reclaimed copy is retired, not left to be restored twice.");
        }
        finally
        {
            TryDeleteDir(directory);
        }
    }

    [Fact]
    public async Task UnreadablePlan_DoesNotQuarantine_NoEvidenceMeansNoJudgement()
    {
        // A plan that cannot be read resolves no anchors, so treating it as empty would condemn a live queue on
        // a transient failure. The session must behave exactly as it does today instead.
        var directory = NewTempDir();
        try
        {
            var (planPath, sidecarDir, _) = Seed(
                directory, OriginalPlan, AnnotationsFor(OriginalPlan), Array.Empty<Answer>());

            var session = ReviewSession.Create(planPath);
            File.Delete(planPath);

            using var server = ReviewServer.Start(session, OptionsFor(sidecarDir));

            Assert.Null(server.StaleAnnotations);
            Assert.Equal(3, await DrainCountAsync(server, session));
        }
        finally
        {
            TryDeleteDir(directory);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>
    /// Seed a sidecar against <paramref name="original"/>, replace the plan at the SAME path with
    /// <paramref name="replacement"/>, start a server, and run <paramref name="assert"/> — the #67 shape.
    /// </summary>
    private static async Task RunAsync(
        string original, string replacement, Func<ReviewServer, ReviewSession, string, Task> assert)
    {
        var directory = NewTempDir();
        try
        {
            var (planPath, sidecarDir, sidecarPath) = Seed(
                directory, original, AnnotationsFor(original), Array.Empty<Answer>());
            await File.WriteAllTextAsync(planPath, replacement);

            var session = ReviewSession.Create(planPath);
            using var server = ReviewServer.Start(session, OptionsFor(sidecarDir));
            await assert(server, session, sidecarPath);
        }
        finally
        {
            TryDeleteDir(directory);
        }
    }

    /// <summary>Write a plan and a sidecar for it, exactly as a live review session would leave them.</summary>
    private static (string PlanPath, string SidecarDirectory, string SidecarPath) Seed(
        string directory,
        string markdown,
        IReadOnlyList<Annotation> annotations,
        IReadOnlyList<Answer> answers)
    {
        var planPath = Path.Combine(directory, "plan.charter.md");
        var sidecarDir = Path.Combine(directory, "state");
        Directory.CreateDirectory(sidecarDir);

        File.WriteAllText(planPath, markdown);
        var sidecarPath = ReviewSidecar.PathForPlan(sidecarDir, planPath);
        ReviewSidecar.WriteState(sidecarPath, planPath, annotations, answers);
        return (planPath, sidecarDir, sidecarPath);
    }

    /// <summary>
    /// The state a real sidecar round-trip produces for <paramref name="markdown"/> — including the plan
    /// fingerprint <see cref="ReviewSidecar.WriteState"/> stamps, which the rule's exemption reads. Built by
    /// writing and rehydrating rather than by recomputing the hash here, so the test cannot agree with a
    /// fingerprint the product does not actually write.
    /// </summary>
    private static ReviewSidecar.State SeededState(
        string markdown, IReadOnlyList<Annotation>? annotations = null)
    {
        var directory = NewTempDir();
        try
        {
            var (_, _, sidecarPath) = Seed(
                directory, markdown, annotations ?? AnnotationsFor(markdown), Array.Empty<Answer>());
            return ReviewSidecar.Rehydrate(sidecarPath);
        }
        finally
        {
            TryDeleteDir(directory);
        }
    }

    private static ReviewServerOptions OptionsFor(string sidecarDirectory) => new()
    {
        BindAddress = IPAddress.Loopback,
        Port = 0,
        SidecarDirectory = sidecarDirectory,
    };

    /// <summary>How many annotations the server would actually hand an agent — the real delivery boundary.</summary>
    private static async Task<int> DrainCountAsync(ReviewServer server, ReviewSession session)
    {
        using var client = new HttpClient();
        var uri = new Uri(
            server.Address, "api/poll?wait=0&key=" + Uri.EscapeDataString(session.Key.Value));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(uri, cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"poll should succeed, got {(int)response.StatusCode}.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        return document.RootElement.GetArrayLength();
    }

    /// <summary>One element annotation per anchor of <paramref name="markdown"/>, in a stable order.</summary>
    private static IReadOnlyList<Annotation> AnnotationsFor(string markdown)
        => SourceMap.Build(markdown).Anchors
            .OrderBy(anchor => anchor, StringComparer.Ordinal)
            .Select((anchor, index) => Note("a-" + index, anchor))
            .ToList();

    private static Annotation Note(string id, string anchorId)
        => new(id, AnnotationKind.Element, anchorId, "note for " + id);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "charter-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
