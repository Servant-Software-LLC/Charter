using System;
using System.IO;
using System.Text;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// At-least-once drain tests for the poll and answers write paths. Both drains clear the store buffer under
/// lock BEFORE the synchronous body write, so a mid-write disconnect used to lose the drained batch forever
/// (in-memory only). These drive the transport-independent write-path seam
/// (<see cref="ReviewServer.WriteBodyOrRequeue(Stream, byte[], Action)"/>) against a stream that throws on
/// <c>Write</c> — deterministic, no flaky client abort — and assert a SUBSEQUENT <see cref="AnnotationStore.Drain"/>
/// / <see cref="AnswerStore.Drain"/> STILL returns the item, proving requeue rather than loss.
/// </summary>
[Trait("Category", "DrainRequeue")]
public class DrainRequeueTests
{
    [Fact]
    public void PollDrainWrite_WhenBodyWriteFails_RequeuesAnnotations_NotLost()
    {
        var store = new AnnotationStore();
        var annotation = new Annotation(
            Id: "ann-1", Kind: AnnotationKind.Element, AnchorId: "anchor-1", Note: "keep me", SourceLine: 7);
        store.Enqueue(annotation);

        // Drain removes it from the buffer (as HandlePollAsync does before writing). A second drain now is
        // empty — so if the write fails and nothing requeues, the annotation is gone forever.
        var drained = store.Drain();
        Assert.Single(drained);
        Assert.Empty(store.Drain());

        // Simulate the poll body write failing on a disconnected client: the seam must requeue the batch.
        var payload = Encoding.UTF8.GetBytes("[{\"id\":\"ann-1\"}]");
        Assert.Throws<IOException>(() =>
            ReviewServer.WriteBodyOrRequeue(new ThrowingStream(), payload, () => store.Requeue(drained)));

        // The write failed, but the annotation is NOT lost — a subsequent poll drain re-fetches it.
        var again = store.Drain();
        var recovered = Assert.Single(again);
        Assert.Equal(annotation, recovered);

        // And the pending signal was re-armed by Requeue, so an outstanding/next wait sees it immediately.
        // (Re-enqueue then re-drain leaves the buffer empty again.)
        Assert.Empty(store.Drain());
    }

    [Fact]
    public void AnswersDrainWrite_WhenBodyWriteFails_RequeuesAnswers_NotLost()
    {
        var store = new AnswerStore();
        var answer = new Answer(
            QuestionId: "q-1", Mode: "single-select", Values: new[] { "ship-it" }, Target: "human");
        store.Enqueue(answer);

        // Drain removes it from the buffer (as HandleAnswersDrain does before writing). A second drain is empty.
        var drained = store.Drain();
        Assert.Single(drained);
        Assert.Empty(store.Drain());

        // Simulate the answers-drain body write failing on a disconnected client: the seam must requeue.
        var payload = Encoding.UTF8.GetBytes("[{\"questionId\":\"q-1\"}]");
        Assert.Throws<IOException>(() =>
            ReviewServer.WriteBodyOrRequeue(new ThrowingStream(), payload, () => store.Requeue(drained)));

        // The write failed, but the answer is NOT lost — a subsequent GET /api/answers drain re-fetches it.
        var again = store.Drain();
        var recovered = Assert.Single(again);
        Assert.Equal(answer, recovered);
        Assert.Empty(store.Drain());
    }

    /// <summary>A write-only stream that throws on every <c>Write</c>, standing in for a disconnected client.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("simulated client disconnect");

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
