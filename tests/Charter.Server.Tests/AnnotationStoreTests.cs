using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Tests for the annotation session <see cref="AnnotationStore"/> — the plan's flagged store-concurrency
/// open item. Charter's store is single-writer / locked (unlike Lavish's whole-file read-modify-write of the
/// session JSON), so the load-bearing <see cref="ConcurrentEnqueueAndDrain_LosesAndDuplicatesNoAnnotations"/>
/// test proves a concurrent poll (<see cref="AnnotationStore.Drain"/>) interleaved with prompts
/// (<see cref="AnnotationStore.Enqueue(Annotation)"/>) loses no annotation and duplicates none.
///
/// This is the TDD "red": every test COMPILES against the stubs and FAILS (the stubs throw
/// <see cref="NotImplementedException"/>). Task <c>02-implement-session-store</c> turns it green.
/// </summary>
[Trait("Category", "AnnotationStore")]
public class AnnotationStoreTests
{
    // ---- Enqueue + Drain --------------------------------------------------------------------------------

    [Fact]
    public void Enqueue_ThenDrain_ReturnsTheAnnotation_PreservingItsFields()
    {
        var store = new AnnotationStore();
        var annotation = MakeAnnotation(1);

        store.Enqueue(annotation);
        var drained = store.Drain();

        var returned = Assert.Single(drained);
        // Drain preserves the annotation's identity and every field (records compare by value).
        Assert.Equal(annotation, returned);
        Assert.Equal(annotation.Id, returned.Id);
        Assert.Equal(annotation.Kind, returned.Kind);
        Assert.Equal(annotation.AnchorId, returned.AnchorId);
        Assert.Equal(annotation.Note, returned.Note);
    }

    [Fact]
    public void Drain_ClearsTheBuffer_SoASecondDrainIsEmpty()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));

        var first = store.Drain();
        var second = store.Drain();

        Assert.Single(first);   // the first Drain returns the enqueued annotation...
        Assert.Empty(second);   // ...and clears it, so the second Drain (no new Enqueue) is empty.
    }

    // ---- Concurrency race (load-bearing: the flagged store-concurrency open item) -----------------------

    [Fact]
    public async Task ConcurrentEnqueueAndDrain_LosesAndDuplicatesNoAnnotations()
    {
        const int count = 500;
        var store = new AnnotationStore();
        var toEnqueue = Enumerable.Range(0, count).Select(MakeAnnotation).ToArray();

        // Everything any Drain observes, across all threads, accumulates here.
        var observed = new ConcurrentBag<Annotation>();

        // N concurrent Enqueues (the "prompts" writes) interleaved with N concurrent Drains (the "poll"
        // reads) — no Thread.Sleep timing hacks; the scheduler does the interleaving. A single-writer /
        // locked store must not tear or drop an annotation under this contention.
        var enqueueTasks = toEnqueue.Select(a => Task.Run(() => store.Enqueue(a)));
        var drainTasks = Enumerable.Range(0, count).Select(_ => Task.Run(() =>
        {
            foreach (var a in store.Drain())
            {
                observed.Add(a);
            }
        }));

        await Task.WhenAll(enqueueTasks.Concat(drainTasks));

        // A final Drain sweeps up any annotations the interleaved Drains had not yet observed.
        foreach (var a in store.Drain())
        {
            observed.Add(a);
        }

        // The union of everything drained is EXACTLY the enqueued set: none lost, none duplicated.
        var observedIds = observed.Select(a => a.Id).ToList();
        Assert.Equal(count, observedIds.Count);                 // no losses and no duplicates change the total
        Assert.Equal(count, observedIds.Distinct().Count());    // every observed id is distinct (no duplicates)
        Assert.Equal(
            toEnqueue.Select(a => a.Id).OrderBy(id => id, StringComparer.Ordinal),
            observedIds.OrderBy(id => id, StringComparer.Ordinal)); // exact set match (nothing lost)
    }

    // ---- Long-poll signal -------------------------------------------------------------------------------

    [Fact]
    public async Task WaitForPendingAsync_OnEmptyStore_ReturnsFalseAfterTimeout()
    {
        var store = new AnnotationStore();

        var signaled = await store.WaitForPendingAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(signaled); // nothing was ever enqueued, so the wait times out to false
    }

    [Fact]
    public async Task WaitForPendingAsync_WhenEnqueueHappensDuringWait_ReturnsTrue()
    {
        var store = new AnnotationStore();

        // Begin waiting on an (initially) empty store with a generous timeout, so the result is decided by
        // the Enqueue signal — not by the timeout elapsing.
        var waitTask = store.WaitForPendingAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        // Enqueue while the wait is outstanding; the outstanding wait must complete true.
        store.Enqueue(MakeAnnotation(1));

        var signaled = await waitTask;

        Assert.True(signaled);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private static Annotation MakeAnnotation(int index) => new(
        Id: "ann-" + index,
        Kind: (AnnotationKind)(index % 3), // cycles Element / TextRange / DiagramNode
        AnchorId: "anchor-" + index,
        Note: "note " + index);
}
