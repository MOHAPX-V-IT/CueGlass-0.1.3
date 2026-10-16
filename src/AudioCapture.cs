using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CueGlass;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class AudioCapture : IAsyncDisposable
{
    private readonly List<WasapiCapture> captures = new();
    private readonly List<MMDevice> devices = new();
    private readonly List<BufferedWaveProvider> buffers = new();
    private readonly object bufferGate = new();
    private bool paused;
    private readonly CancellationTokenSource stop = new();
    private Task? pump;
    public event Action<byte[]>? Data;
    public event Action<float>? Level;
    public event Action<string>? Failed;

    public void SetPaused(bool value)
    {
        lock (bufferGate)
        {
            paused = value;
            foreach (var buffer in buffers) buffer.ClearBuffer();
        }
    }

    public static List<AudioDevice> ListDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDevice> { new("", "По умолчанию при запуске") };
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            result.Add(new(device.ID, device.FriendlyName));
            device.Dispose();
        }
        return result;
    }

    public void Start(bool mic, bool system, string micId, string outputId)
    {
        if (!mic && !system) return;
        var sources = new List<ISampleProvider>();
        if (mic) sources.Add(CreateSource(DataFlow.Capture, micId, false));
        if (system) sources.Add(CreateSource(DataFlow.Render, outputId, true));
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(24000, 1)) { ReadFully = true };
        foreach (var source in sources) mixer.AddMixerInput(source);
        var pcm = new SampleToWaveProvider16(mixer);
        foreach (var capture in captures) capture.StartRecording();
        pump = Task.Run(async () =>
        {
            try
            {
                // A fixed clock fills loopback silence with zeros, rather than concatenating
                // microphone and speaker packets (which would distort timing and sample rate).
                var clock = Stopwatch.StartNew();
                long next = 0;
                while (!stop.IsCancellationRequested)
                {
                    var bytes = new byte[960]; // 20 ms, 24 kHz, mono PCM16.
                    pcm.Read(bytes, 0, bytes.Length);
                    float peak = 0;
                    for (int i = 0; i < bytes.Length; i += 2)
                        peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(bytes, i) / 32768f));
                    Level?.Invoke(peak);
                    Data?.Invoke(bytes);
                    next += 20;
                    int delay = (int)(next - clock.ElapsedMilliseconds);
                    if (delay > 0) await Task.Delay(delay, stop.Token);
                    else if (delay < -200) next = clock.ElapsedMilliseconds;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!stop.IsCancellationRequested) Failed?.Invoke(e.Message); }
        });
    }

    private ISampleProvider CreateSource(DataFlow flow, string id, bool loopback)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(id) ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia) : enumerator.GetDevice(id);
        devices.Add(device);
        WasapiCapture capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        captures.Add(capture);
        var buffered = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        buffers.Add(buffered);
        capture.DataAvailable += (_, e) =>
        {
            lock (bufferGate) { if (!paused) buffered.AddSamples(e.Buffer, 0, e.BytesRecorded); }
        };
        capture.RecordingStopped += (_, e) => { if (e.Exception != null && !stop.IsCancellationRequested) Failed?.Invoke(e.Exception.Message); };
        ISampleProvider mono = new AverageChannels(buffered.ToSampleProvider());
        if (mono.WaveFormat.SampleRate != 24000) mono = new WdlResamplingSampleProvider(mono, 24000);
        return new VolumeSampleProvider(mono) { Volume = 0.65f };
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var c in captures) { try { c.StopRecording(); } catch { } }
        if (pump != null) await pump;
        foreach (var c in captures) c.Dispose();
        foreach (var d in devices) d.Dispose();
        stop.Dispose();
    }
}

public sealed class AverageChannels : ISampleProvider
{
    private readonly ISampleProvider source;
    private float[] buffer = Array.Empty<float>();
    public WaveFormat WaveFormat { get; }
    public AverageChannels(ISampleProvider source)
    {
        this.source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }
    public int Read(float[] output, int offset, int count)
    {
        int channels = source.WaveFormat.Channels;
        int needed = count * channels;
        if (buffer.Length < needed) buffer = new float[needed];
        int read = source.Read(buffer, 0, needed);
        int frames = read / channels;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += buffer[i * channels + c];
            output[offset + i] = sum / channels;
        }
        return frames;
    }
}
