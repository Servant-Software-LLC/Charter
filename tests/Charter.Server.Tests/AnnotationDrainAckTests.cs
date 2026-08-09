using System;
using System.Linq;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Annotation delivery was AT-MOST-ONCE past the socket write (#117). The drain cleared the buffer under lock
/// and the only requeue fired if the write THREW — but for the few-KB payloads Charter produces,
/// <c>Stream.Write</c> lands in the kernel send buffer and returns success whether or not the peer ever reads
/// it. A Ctrl-C, a harness-killed shell loop, or a laptop sleep in that window lost the reviewer's annotations
/// with no error and no trace, and the sidecar then deleted itself because both queues looked empty.
/// <para>
/// Answers (<c>/answers/ack</c>) and the round hand-off (<c>/review/ack</c>) both had an ack. Annotations —
/// the one payload that exists nowhere else once cleared — did not.
/// </para>
/// </summary>
[Trait("Category", "AnnotationDrainAck")]
public class AnnotationDrainAckTests
{
    private static Annotation Note(string id) =>
        new(id, AnnotationKind.Element, "b0000", "the reviewer's note " + id, SourceLine: 12);

    /// <summary>The defect: a caller that never acknowledged must be handed the same batch again.</summary>
    [Fact]
    public void AnUnacknowledgedBatch_IsRedeliveredOnTheNextDrain()
    {
        var store = new AnnotationStore(TimeSpan.Zero);
        store.Enqueue(Note("a"));
        store.Enqueue(Note("b"));

        var first = store.DrainBatch();
        Assert.Equal(2, first.Annotations.Count);

        // The caller dies here — no ack. Before #117 this batch was simply gone.
        var second = store.DrainBatch();

        Assert.Equal(2, second.Annotations.Count);
        Assert.Equal(
            new[] { "a", "b" },
            second.Annotations.Select(a => a.Id).ToArray());
    }

    /// <summary>Acknowledged is gone: at-least-once must not become forever.</summary>
    [Fact]
    public void AnAcknowledgedBatch_IsNotRedelivered()
    {
        var store = new AnnotationStore();
        store.Enqueue(Note("a"));

        var batch = store.DrainBatch();
        Assert.Equal(1, store.Ack(batch.Sequence));

        Assert.Empty(store.DrainBatch().Annotations);
    }

    /// <summary>
    /// A late ack — one arriving after a re-delivery has already issued a newer sequence — must release
    /// nothing. Otherwise a dying caller's straggling ack would discard the batch its successor is holding.
    /// </summary>
    [Fact]
    public void AStaleSequence_ReleasesNothing_AndTheBatchSurvives()
    {
        var store = new AnnotationStore(TimeSpan.Zero);
        store.Enqueue(Note("a"));

        var first = store.DrainBatch();
        var second = store.DrainBatch();          // re-delivery; supersedes `first`
        Assert.NotEqual(first.Sequence, second.Sequence);

        Assert.Equal(0, store.Ack(first.Sequence));

        // Still owed to whoever holds the current sequence.
        Assert.Single(store.DrainBatch().Annotations);
    }

    /// <summary>
    /// An in-flight batch must NOT hold the wake signal open. It is not queued — it is already with a caller —
    /// and a signal that stayed hot would spin every later <c>poll --wait</c> instead of blocking.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task AnInFlightBatch_DoesNotKeepTheWakeSignalHot()
    {
        var store = new AnnotationStore();
        store.Enqueue(Note("a"));
        store.DrainBatch();   // in flight, unacked

        var woke = await store.WaitForPendingAsync(
            TimeSpan.FromMilliseconds(150), System.Threading.CancellationToken.None);

        Assert.False(woke, "an unacknowledged in-flight batch must not make poll --wait spin");
    }

    /// <summary>
    /// The durability sidecar persists the in-flight batch too. A batch handed to a caller that then crashed is
    /// owed to the next one, so persisting only the pending queue would lose exactly what the ack protects —
    /// and would let the sidecar delete itself while a batch was outstanding.
    /// </summary>
    [Fact]
    public void TheDurableSnapshot_IncludesTheInFlightBatch_SoACrashDoesNotLoseIt()
    {
        var store = new AnnotationStore();
        store.Enqueue(Note("a"));
        store.DrainBatch();
        store.Enqueue(Note("b"));

        Assert.Equal(new[] { "a", "b" }, store.DurableSnapshot().Select(a => a.Id).ToArray());

        // The panel keeps showing only the PRE-DRAIN queue — a reviewer must not see a note as still pending
        // once it is the agent's.
        Assert.Equal(new[] { "b" }, store.Snapshot().Select(a => a.Id).ToArray());
    }

    /// <summary>
    /// A requeue (the write demonstrably failed) supersedes the in-flight batch and restores it to PENDING, so
    /// the wake signal re-arms and the next <c>poll --wait</c> re-delivers immediately rather than waiting out
    /// a full 30-second window.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task ARequeue_RestoresToPending_AndRearmsTheSignal()
    {
        var store = new AnnotationStore();
        store.Enqueue(Note("a"));

        var batch = store.DrainBatch();
        store.Requeue(batch.Annotations);

        Assert.True(
            await store.WaitForPendingAsync(TimeSpan.FromSeconds(1), System.Threading.CancellationToken.None),
            "a requeued batch is queued again, so a waiting poll must wake immediately");

        Assert.Single(store.Snapshot());
        Assert.Equal(0, store.Ack(batch.Sequence));   // it is no longer in flight
    }
}
