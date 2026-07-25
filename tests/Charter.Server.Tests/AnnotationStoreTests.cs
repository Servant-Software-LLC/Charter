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
/// The in-page review panel (#42) adds pre-drain management — <see cref="AnnotationStore.Update"/> and
/// <see cref="AnnotationStore.Remove"/> — which makes the store's wake-signal INVARIANT load-bearing:
/// <b>the signal is completed iff the pending buffer is non-empty</b>. Deleting the only pending annotation
/// must re-arm the signal, or every later <c>poll --wait</c> returns instantly with <c>[]</c> — a permanent
/// hot loop. <see cref="Remove_EmptyingTheBuffer_ReArmsTheWakeSignal_SoWaitBlocks"/> is that guard.
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

    [Fact]
    public async Task ConcurrentEnqueueDrainUpdateAndRemove_LosesAndDuplicatesNoAnnotations()
    {
        const int count = 500;
        var store = new AnnotationStore();
        var toEnqueue = Enumerable.Range(0, count).Select(MakeAnnotation).ToArray();

        var observed = new ConcurrentBag<Annotation>();
        var removed = new ConcurrentBag<string>();
        var updated = new ConcurrentBag<string>();

        // The #42 panel means the reviewer's edit/delete now races the agent's poll. Hammer all four
        // operations at once: N enqueues, N drains, N updates, N removes. Every annotation must end up in
        // EXACTLY ONE terminal place — drained to the agent, or removed by the reviewer — never both, never
        // twice, never lost. (Update never removes; a torn Update would surface as a drained note that is
        // neither the original nor the edit.)
        var enqueueTasks = toEnqueue.Select(a => Task.Run(() => store.Enqueue(a)));
        var drainTasks = Enumerable.Range(0, count).Select(_ => Task.Run(() =>
        {
            foreach (var a in store.Drain())
            {
                observed.Add(a);
            }
        }));
        var updateTasks = toEnqueue.Select(a => Task.Run(() =>
        {
            if (store.Update(a.Id, "edited " + a.Id))
            {
                updated.Add(a.Id);
            }
        }));
        var removeTasks = toEnqueue.Where(a => a.Id.EndsWith('7')).Select(a => Task.Run(() =>
        {
            if (store.Remove(a.Id))
            {
                removed.Add(a.Id);
            }
        }));

        await Task.WhenAll(enqueueTasks.Concat(drainTasks).Concat(updateTasks).Concat(removeTasks));

        // A final Drain sweeps up anything the interleaved Drains had not yet observed.
        foreach (var a in store.Drain())
        {
            observed.Add(a);
        }

        var observedIds = observed.Select(a => a.Id).ToList();
        var removedIds = removed.ToList();

        // Disjoint and exhaustive: drained ⊎ removed == the enqueued set.
        Assert.Equal(count, observedIds.Count + removedIds.Count);
        Assert.Equal(observedIds.Count, observedIds.Distinct().Count());
        Assert.Equal(removedIds.Count, removedIds.Distinct().Count());
        Assert.Empty(observedIds.Intersect(removedIds, StringComparer.Ordinal));
        Assert.Equal(
            toEnqueue.Select(a => a.Id).OrderBy(id => id, StringComparer.Ordinal),
            observedIds.Concat(removedIds).OrderBy(id => id, StringComparer.Ordinal));

        // No torn edit: a drained annotation whose Update reported success carries the edited note, and one
        // whose Update never landed carries the original — never a mix, never a half-written record.
        var updatedIds = updated.ToHashSet(StringComparer.Ordinal);
        foreach (var a in observed)
        {
            var expected = updatedIds.Contains(a.Id) ? "edited " + a.Id : "note " + a.Id["ann-".Length..];
            Assert.Equal(expected, a.Note);
        }

        // The wake-signal invariant survives the storm: the buffer is empty, so a wait must BLOCK.
        Assert.False(await store.WaitForPendingAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None));
    }

    // ---- Pre-drain management: Update / Remove (the #42 panel's edit + delete) ---------------------------

    [Fact]
    public void Update_ByIdChangesTheNoteInPlace_PreservingIdentityAndPosition()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));
        store.Enqueue(MakeAnnotation(2));
        store.Enqueue(MakeAnnotation(3));

        Assert.True(store.Update("ann-2", "the reviewer reworded this note"));

        var drained = store.Drain();
        Assert.Equal(3, drained.Count);
        // Position is preserved (the edited record replaces the old one in place, it is not re-appended)...
        Assert.Equal(new[] { "ann-1", "ann-2", "ann-3" }, drained.Select(a => a.Id));
        // ...and only the Note changed; every other field of the edited record is carried through untouched.
        var edited = drained[1];
        Assert.Equal("the reviewer reworded this note", edited.Note);
        Assert.Equal(MakeAnnotation(2).AnchorId, edited.AnchorId);
        Assert.Equal(MakeAnnotation(2).Kind, edited.Kind);
        // The untouched neighbours keep their original notes.
        Assert.Equal("note 1", drained[0].Note);
        Assert.Equal("note 3", drained[2].Note);
    }

    [Fact]
    public void Update_UnknownId_ReturnsFalse_AndChangesNothing()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));

        Assert.False(store.Update("ann-not-here", "edit for a note that is not pending"));

        var drained = Assert.Single(store.Drain());
        Assert.Equal("note 1", drained.Note);
    }

    [Fact]
    public void Update_AfterDrain_ReturnsFalse_TheAnnotationIsAlreadyHandedOff()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));
        store.Drain();

        // Once drained, the annotation belongs to the agent — the reviewer can no longer edit it. "false"
        // is what the API surfaces as a friendly 404 ("already handed off"), never an error.
        Assert.False(store.Update("ann-1", "too late"));
    }

    [Fact]
    public void Remove_ByIdTakesTheAnnotationOutOfThePendingBuffer()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));
        store.Enqueue(MakeAnnotation(2));

        Assert.True(store.Remove("ann-1"));

        var drained = Assert.Single(store.Drain());
        Assert.Equal("ann-2", drained.Id);
    }

    [Fact]
    public void Remove_UnknownOrAlreadyDrainedId_ReturnsFalse()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));
        store.Drain();

        Assert.False(store.Remove("ann-1"));      // already handed off to the agent
        Assert.False(store.Remove("ann-999"));    // never existed
    }

    [Fact]
    public void Snapshot_IsNonDestructive_SoAListDoesNotConsumeTheQueue()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));

        Assert.Single(store.Snapshot());
        Assert.Single(store.Snapshot());   // a second list returns the same annotation...
        Assert.Single(store.Drain());      // ...and the drain still delivers it to the agent.
    }

    // ---- The wake-signal invariant: completed IFF the pending buffer is non-empty (BLOCKER 3) ------------

    [Fact]
    public async Task Remove_EmptyingTheBuffer_ReArmsTheWakeSignal_SoWaitBlocks()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));      // completes the wake signal
        Assert.True(store.Remove("ann-1"));    // ...and this empties the buffer again

        // Without re-arming the signal the already-completed TaskCompletionSource would make EVERY later
        // `poll --wait` return instantly with [] — a permanent hot loop that kills the agent's wait mode.
        var signaled = await store.WaitForPendingAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.False(signaled);
    }

    [Fact]
    public async Task Remove_LeavingOtherAnnotationsPending_KeepsTheWakeSignalArmed()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));
        store.Enqueue(MakeAnnotation(2));

        Assert.True(store.Remove("ann-1"));    // one remains pending, so the signal must stay completed

        var signaled = await store.WaitForPendingAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(signaled);
    }

    [Fact]
    public async Task Enqueue_AfterAnEmptyingRemove_ReArmsTheWakeSignalAgain()
    {
        var store = new AnnotationStore();
        store.Enqueue(MakeAnnotation(1));
        store.Remove("ann-1");

        // The re-armed (fresh) signal must still be completable by the NEXT enqueue — re-arming must not
        // strand the store in a state where no wake ever fires again.
        var waitTask = store.WaitForPendingAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        store.Enqueue(MakeAnnotation(2));

        Assert.True(await waitTask);
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
