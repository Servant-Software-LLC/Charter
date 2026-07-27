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
/// Tests for the <see cref="AnswerStore"/> — the <c>:::question</c> answer buffer. Unlike the annotation
/// store's destructive drain, answers follow a <b>peek → apply → commit</b> discipline (§1.6): <see cref="AnswerStore.Peek"/>
/// reports without removing (so a plain poll can never strand an answer), and <see cref="AnswerStore.CommitFront(int)"/>
/// removes only the peeked-and-applied prefix. The load-bearing
/// <see cref="ConcurrentEnqueueAndCommit_LosesAndDuplicatesNoAnswers"/> proves a concurrent submit interleaved
/// with a commit loses no answer and duplicates none — the race the lock protects.
/// </summary>
[Trait("Category", "AnswerStore")]
public class AnswerStoreTests
{
    // ---- Peek is non-destructive (report-don't-remove) --------------------------------------------------

    [Fact]
    public void Enqueue_ThenPeek_ReturnsTheAnswer_PreservingItsFields()
    {
        var store = new AnswerStore();
        var answer = MakeAnswer(1);

        store.Enqueue(answer);
        var peeked = store.Peek();

        var returned = Assert.Single(peeked);
        // Peek preserves the answer's identity and every field (records compare by value).
        Assert.Equal(answer, returned);
        Assert.Equal(answer.QuestionId, returned.QuestionId);
        Assert.Equal(answer.Mode, returned.Mode);
        Assert.Equal(answer.Values, returned.Values);
        Assert.Equal(answer.Target, returned.Target);
    }

    [Fact]
    public void Peek_IsNonDestructive_ASecondPeekStillReturnsTheAnswer()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));

        var first = store.Peek();
        var second = store.Peek();

        // A plain poll peeks but does not remove — the answer stays recoverable for the next peek/apply.
        Assert.Single(first);
        Assert.Single(second);
    }

    // ---- CommitFront removes only the applied prefix ----------------------------------------------------

    [Fact]
    public void CommitFront_RemovesThePeekedPrefix_LeavingLaterArrivalsQueued()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));
        store.Enqueue(MakeAnswer(2));

        // Peek the two queued answers, then a third arrives AFTER the peek (appended to the back).
        var peeked = store.Peek();
        Assert.Equal(2, peeked.Count);
        store.Enqueue(MakeAnswer(3));

        // Commit exactly the peeked count: the two peeked answers are removed; the later arrival survives.
        var committed = store.CommitFront(peeked.Count);
        Assert.Equal(new[] { "q-1", "q-2" }, committed.Select(a => a.QuestionId));

        var remaining = store.Peek();
        Assert.Equal(new[] { "q-3" }, remaining.Select(a => a.QuestionId));
    }

    [Fact]
    public void CommitFront_BeyondBufferSize_RemovesOnlyWhatIsPresent()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));

        var committed = store.CommitFront(5);

        Assert.Single(committed);          // only the one present answer is removed...
        Assert.Empty(store.Peek());        // ...and the buffer is now empty.
    }

    [Fact]
    public void CommitFront_ZeroOrNegative_RemovesNothing()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));

        Assert.Empty(store.CommitFront(0));
        Assert.Single(store.Peek()); // an apply that resolved nothing must not remove a queued answer
    }

    // ---- The wake-signal invariant: completed IFF the pending buffer is non-empty (Charter #62) ----------
    // The annotation store learned this the hard way (#42): stating the signal as a PREDICATE over the buffer,
    // re-established under the lock after EVERY mutation, is what keeps the ack path honest. For answers the
    // dangerous mutation is CommitFront — the ack. Miss it and a drained-then-acked queue leaves a stale
    // completed signal, so every later `poll --wait` returns instantly: a permanent hot loop.

    [Fact]
    public async Task WaitForPendingAsync_OnEmptyStore_ReturnsFalseAfterTimeout()
    {
        var store = new AnswerStore();

        var signaled = await store.WaitForPendingAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(signaled); // nothing was ever enqueued, so the wait times out to false
    }

    [Fact]
    public async Task WaitForPendingAsync_WhenAnAnswerArrivesDuringTheWait_ReturnsTrue()
    {
        var store = new AnswerStore();

        // Begin waiting on an (initially) empty store with a generous timeout, so the result is decided by the
        // Enqueue signal — not by the timeout elapsing. This is Charter #62 in miniature: before the fix the
        // answer store had NO signal at all, so an outstanding wait could only ever end on the timeout.
        var waitTask = store.WaitForPendingAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        store.Enqueue(MakeAnswer(1));

        Assert.True(await waitTask);
    }

    [Fact]
    public async Task CommitFront_EmptyingTheBuffer_ReArmsTheWakeSignal_SoWaitBlocks()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));                 // completes the wake signal
        Assert.Single(store.CommitFront(1));          // ...and the ack empties the buffer again

        // THE regression guard. Without re-arming under the lock in CommitFront, the already-completed
        // TaskCompletionSource makes every later `poll --wait` return instantly with nothing queued.
        var signaled = await store.WaitForPendingAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.False(signaled);
    }

    [Fact]
    public async Task CommitFront_LeavingLaterAnswersQueued_KeepsTheWakeSignalArmed()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));
        store.Enqueue(MakeAnswer(2));

        Assert.Single(store.CommitFront(1));   // one answer remains, so the signal must stay completed

        Assert.True(await store.WaitForPendingAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Enqueue_AfterAnEmptyingCommit_ReArmsTheWakeSignalAgain()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));
        store.CommitFront(1);

        // The re-armed (fresh) signal must still be completable by the NEXT enqueue — re-arming must not strand
        // the store in a state where no wake ever fires again.
        var waitTask = store.WaitForPendingAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        store.Enqueue(MakeAnswer(2));

        Assert.True(await waitTask);
    }

    [Fact]
    public async Task Peek_DoesNotDisturbTheWakeSignal()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));

        // Peek is non-destructive, so the queued answer still pends and the signal must stay completed...
        Assert.Single(store.Peek());
        Assert.True(await store.WaitForPendingAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

        // ...and peeking an EMPTY store must not complete it either.
        store.CommitFront(1);
        Assert.Empty(store.Peek());
        Assert.False(await store.WaitForPendingAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None));
    }

    // ---- Concurrency race (load-bearing: the lock the store exists for) ---------------------------------

    [Fact]
    public async Task ConcurrentEnqueueAndCommit_LosesAndDuplicatesNoAnswers()
    {
        const int count = 500;
        var store = new AnswerStore();
        var toEnqueue = Enumerable.Range(0, count).Select(MakeAnswer).ToArray();

        // Everything any CommitFront removes, across all threads, accumulates here.
        var observed = new ConcurrentBag<Answer>();

        // N concurrent Enqueues (the "answers" submits) interleaved with N concurrent CommitFront(1)s (the
        // apply commits) — no Thread.Sleep timing hacks; the scheduler does the interleaving. A single-writer
        // / locked store must not tear or drop an answer under this contention.
        var enqueueTasks = toEnqueue.Select(a => Task.Run(() => store.Enqueue(a)));
        var commitTasks = Enumerable.Range(0, count).Select(_ => Task.Run(() =>
        {
            foreach (var a in store.CommitFront(1))
            {
                observed.Add(a);
            }
        }));

        await Task.WhenAll(enqueueTasks.Concat(commitTasks));

        // A final commit sweeps up any answers the interleaved commits had not yet removed.
        foreach (var a in store.CommitFront(count))
        {
            observed.Add(a);
        }

        // The union of everything committed is EXACTLY the enqueued set: none lost, none duplicated.
        var observedIds = observed.Select(a => a.QuestionId).ToList();
        Assert.Equal(count, observedIds.Count);                 // no losses and no duplicates change the total
        Assert.Equal(count, observedIds.Distinct().Count());    // every observed id is distinct (no duplicates)
        Assert.Equal(
            toEnqueue.Select(a => a.QuestionId).OrderBy(id => id, StringComparer.Ordinal),
            observedIds.OrderBy(id => id, StringComparer.Ordinal)); // exact set match (nothing lost)

        // The wake-signal invariant survives the storm: everything was committed, so the buffer is empty and a
        // wait must BLOCK. A single missed re-arm anywhere in that interleaving leaves it spinning instead.
        Assert.False(await store.WaitForPendingAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private static Answer MakeAnswer(int index) => new(
        QuestionId: "q-" + index,
        Mode: index % 2 == 0 ? "single-select" : "multi-select",
        Values: new List<string> { "value-" + index },
        Target: index % 2 == 0 ? "human" : "agent");
}
