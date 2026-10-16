using System;
using System.Threading.Tasks;

namespace CueGlass;

public sealed record ScreenFrame(int Monitor, string Uri, DateTimeOffset CapturedAt);

// Prepare one recent frame while speech is still arriving. The cache never
// transmits data, and discards frames across pause, disconnect and monitor change.
public sealed class ScreenFrameCache
{
    private readonly object gate = new();
    private readonly Func<int, Task<string>> capture;
    private readonly Func<DateTimeOffset> now;
    private ScreenFrame? cached;
    private Task<ScreenFrame>? pending;
    private int pendingMonitor, epoch;
    public ScreenFrameCache(Func<int, Task<string>> capture, Func<DateTimeOffset>? now = null)
    {
        this.capture = capture;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }
    public void Invalidate()
    {
        lock (gate) { epoch++; cached = null; pending = null; }
    }
    public Task<ScreenFrame> Get(int monitor, TimeSpan maxAge)
    {
        lock (gate)
        {
            if (maxAge > TimeSpan.Zero && cached?.Monitor == monitor && now() - cached.CapturedAt <= maxAge)
                return Task.FromResult(cached);
            if (pending is { IsCompleted: false } && pendingMonitor == monitor) return pending;
            pendingMonitor = monitor;
            int generation = ++epoch;
            pending = Capture(monitor, generation);
            return pending;
        }
    }
    private async Task<ScreenFrame> Capture(int monitor, int generation)
    {
        // Record start time so a slow capture is never treated as a fresh frame.
        var started = now();
        string uri = await capture(monitor);
        var frame = new ScreenFrame(monitor, uri, started);
        lock (gate) { if (epoch == generation) cached = frame; }
        return frame;
    }
}
