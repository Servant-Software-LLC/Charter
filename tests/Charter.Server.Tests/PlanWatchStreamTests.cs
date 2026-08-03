using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Charter.Server;
using Xunit;

namespace Charter.Server.Tests;

/// <summary>
/// Charter #92 at the STREAM, not at the seam: the one thing <c>PlanWatchTests</c> cannot show is that a beat's
/// report actually reaches the browser as an <c>event: reload</c> frame. This drives a real server over a real
/// SSE connection and kills the plan watcher the way a branch switch does, so the only thing left that can
/// deliver the frame is the keep-alive beat.
/// </summary>
/// <remarks>
/// The beat is shortened through <see cref="ReviewServerOptions.EventStreamBeat"/> — an INTERNAL test seam, the
/// same shape as <c>ReviewServer.StartCore</c>'s injected port supplier — so this proves the wiring in under a
/// second instead of waiting three real 15-second beats. Nothing about the production cadence changes.
/// </remarks>
[Trait("Category", "PlanWatch")]
public class PlanWatchStreamTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(150);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "charter-plan-stream-" + Guid.NewGuid().ToString("N"));

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
            // A leftover temp directory is harmless.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The plan's folder is removed and restored under a live <c>/events</c> connection — a branch switch, or a
    /// <c>git checkout</c> of a different wave. The <see cref="FileSystemWatcher"/> armed at connect is bound BY
    /// HANDLE to the folder that went away and can never fire again, and the test waits for beats to land while
    /// it is gone so the handle is provably dropped rather than merely suspect. Everything after that point is
    /// the safety net or nothing: pre-#92 the reviewer sat on a stale render for the rest of the session.
    /// </summary>
    [Fact]
    public async Task AReloadFrameStillArrives_WhenThePlansFolderIsRemovedAndRestoredUnderALiveStream()
    {
        var directory = Path.Combine(_root, "wave-04");
        Directory.CreateDirectory(directory);
        var plan = Path.Combine(directory, "team.charter.md");
        await File.WriteAllTextAsync(plan, "# Plan\n\nthe revision the reviewer is reading\n");

        var session = ReviewSession.Create(plan);
        using var server = ReviewServer.Start(
            session,
            new ReviewServerOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = 0,
                EventStreamBeat = Beat,
            });

        using var overall = new CancellationTokenSource(Budget);
        using var client = new HttpClient();
        var eventsUri = new Uri(server.Address, "events?key=" + Uri.EscapeDataString(session.Key.Value));
        using var response =
            await client.GetAsync(eventsUri, HttpCompletionOption.ResponseHeadersRead, overall.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(overall.Token);
        var reader = new SseReader(stream);

        // The stream is live once the connect ping lands.
        Assert.True(await reader.WaitForAsync("event: ping", 1, overall.Token), "the stream must open");

        // Take the plan's whole folder away, then wait for beats to land while it is gone — the keep-alive
        // comments ARE the beat, so this is an observation, not a sleep. By the third one the watcher armed at
        // connect has been dropped on every platform, so nothing can notify this stream any more.
        Directory.Delete(directory, recursive: true);
        Assert.True(
            await reader.WaitForAsync(": keep-alive", 3, overall.Token),
            "the stream must keep beating while the plan is gone");

        // ...and the branch comes back, with the agent's revision on it.
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(plan, "# Plan\n\nthe revision the agent wrote while they read\n");

        Assert.True(
            await reader.WaitForAsync("event: reload", 1, overall.Token),
            "the keep-alive beat must deliver the reload the dead watcher never could — otherwise the reviewer "
                + "reads a stale render for the rest of the session (Charter #92)");
    }

    // Reads the open SSE body, counting occurrences of a marker. Bounded only by the caller's token, so a
    // failure surfaces as a clear assertion rather than a hang.
    private sealed class SseReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[2048];
        private readonly StringBuilder _received = new();

        public async Task<bool> WaitForAsync(string marker, int count, CancellationToken ct)
        {
            try
            {
                while (Occurrences(marker) < count)
                {
                    var n = await stream.ReadAsync(_buffer, ct);
                    if (n == 0)
                    {
                        return false; // the stream closed
                    }

                    _received.Append(Encoding.UTF8.GetString(_buffer, 0, n));
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false; // the bounded deadline elapsed; the caller's assertion reports it
            }
        }

        private int Occurrences(string marker)
        {
            var text = _received.ToString();
            var seen = 0;
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            while (at >= 0)
            {
                seen++;
                at = text.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            }

            return seen;
        }
    }
}
