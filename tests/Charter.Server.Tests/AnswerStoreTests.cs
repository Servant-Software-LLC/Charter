using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Tests for the <see cref="AnswerStore"/> — the <c>:::question</c> answer buffer that mirrors
/// <see cref="AnnotationStore"/>'s serialize-all-access lock design but had ZERO unit coverage: the answer
/// API tests only drove it sequentially, so the concurrent submit-vs-drain race the lock protects (production
/// runs <see cref="AnswerStore.Enqueue(Answer)"/> and <see cref="AnswerStore.Drain"/> on separate
/// fire-and-forget request tasks) was never exercised. The load-bearing
/// <see cref="ConcurrentEnqueueAndDrain_LosesAndDuplicatesNoAnswers"/> proves a concurrent submit interleaved
/// with a drain loses no answer and duplicates none.
/// </summary>
[Trait("Category", "AnswerStore")]
public class AnswerStoreTests
{
    // ---- Enqueue + Drain --------------------------------------------------------------------------------

    [Fact]
    public void Enqueue_ThenDrain_ReturnsTheAnswer_PreservingItsFields()
    {
        var store = new AnswerStore();
        var answer = MakeAnswer(1);

        store.Enqueue(answer);
        var drained = store.Drain();

        var returned = Assert.Single(drained);
        // Drain preserves the answer's identity and every field (records compare by value).
        Assert.Equal(answer, returned);
        Assert.Equal(answer.QuestionId, returned.QuestionId);
        Assert.Equal(answer.Mode, returned.Mode);
        Assert.Equal(answer.Values, returned.Values);
        Assert.Equal(answer.Target, returned.Target);
    }

    [Fact]
    public void Drain_ClearsTheBuffer_SoASecondDrainIsEmpty()
    {
        var store = new AnswerStore();
        store.Enqueue(MakeAnswer(1));

        var first = store.Drain();
        var second = store.Drain();

        Assert.Single(first);   // the first Drain returns the enqueued answer...
        Assert.Empty(second);   // ...and clears it, so the second Drain (no new Enqueue) is empty.
    }

    // ---- Concurrency race (load-bearing: the lock the store exists for) ---------------------------------

    [Fact]
    public async Task ConcurrentEnqueueAndDrain_LosesAndDuplicatesNoAnswers()
    {
        const int count = 500;
        var store = new AnswerStore();
        var toEnqueue = Enumerable.Range(0, count).Select(MakeAnswer).ToArray();

        // Everything any Drain observes, across all threads, accumulates here.
        var observed = new ConcurrentBag<Answer>();

        // N concurrent Enqueues (the "answers" submits) interleaved with N concurrent Drains (the "GET
        // /api/answers" reads) — no Thread.Sleep timing hacks; the scheduler does the interleaving. A
        // single-writer / locked store must not tear or drop an answer under this contention.
        var enqueueTasks = toEnqueue.Select(a => Task.Run(() => store.Enqueue(a)));
        var drainTasks = Enumerable.Range(0, count).Select(_ => Task.Run(() =>
        {
            foreach (var a in store.Drain())
            {
                observed.Add(a);
            }
        }));

        await Task.WhenAll(enqueueTasks.Concat(drainTasks));

        // A final Drain sweeps up any answers the interleaved Drains had not yet observed.
        foreach (var a in store.Drain())
        {
            observed.Add(a);
        }

        // The union of everything drained is EXACTLY the enqueued set: none lost, none duplicated.
        var observedIds = observed.Select(a => a.QuestionId).ToList();
        Assert.Equal(count, observedIds.Count);                 // no losses and no duplicates change the total
        Assert.Equal(count, observedIds.Distinct().Count());    // every observed id is distinct (no duplicates)
        Assert.Equal(
            toEnqueue.Select(a => a.QuestionId).OrderBy(id => id, StringComparer.Ordinal),
            observedIds.OrderBy(id => id, StringComparer.Ordinal)); // exact set match (nothing lost)
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private static Answer MakeAnswer(int index) => new(
        QuestionId: "q-" + index,
        Mode: index % 2 == 0 ? "single-select" : "multi-select",
        Values: new List<string> { "value-" + index },
        Target: index % 2 == 0 ? "human" : "agent");
}
