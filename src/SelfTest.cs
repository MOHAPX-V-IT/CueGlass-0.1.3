using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CueGlass;

public static class SelfTest
{
    public static async Task<int> Run(string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var results = new List<object>();
        int failures = 0;
        async Task Test(string name, Func<Task> body)
        {
            try { await body(); results.Add(new { name, passed = true }); }
            catch (Exception e) { failures++; results.Add(new { name, passed = false, error = e.ToString() }); }
        }
        await Test("DPAPI round-trip; ciphertext contains no plaintext", () =>
        {
            const string test = "synthetic-test-secret-only";
            var encrypted = KeyVault.Protect(test);
            Require(KeyVault.Unprotect(encrypted) == test, "DPAPI round-trip failed");
            Require(!Encoding.UTF8.GetString(encrypted).Contains(test), "Plaintext in encrypted output");
            return Task.CompletedTask;
        });
        await Test("Stereo to mono and PCM16 resampling: 48 kHz -> 24 kHz", () =>
        {
            var signal = new SignalGenerator(48000, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.25 };
            var mono = new AverageChannels(signal);
            var resampled = new WdlResamplingSampleProvider(mono, 24000);
            var pcm = new SampleToWaveProvider16(resampled);
            var bytes = new byte[48000];
            Require(pcm.Read(bytes, 0, bytes.Length) == 48000, "Expected one second of PCM16");
            double energy = 0;
            for (int i = 0; i < bytes.Length; i += 2) { double v = BitConverter.ToInt16(bytes, i) / 32768.0; energy += v * v; }
            double rms = Math.Sqrt(energy / 24000);
            Require(rms > 0.15 && rms < 0.20, "Unexpected signal RMS: " + rms);
            return Task.CompletedTask;
        });
        await Test("Latency settings preserve reasoning and response budget; code mode has a larger budget", () =>
        {
            using var doc = JsonDocument.Parse(Protocol.Json(Protocol.Session("context", 600)));
            var settings = doc.RootElement.GetProperty("session");
            Require(!settings.TryGetProperty("reasoning", out _), "Latency tuning must not lower reasoning effort");
            Require(settings.GetProperty("max_output_tokens").GetInt32() == 1200, "Conversation output budget changed");
            Require(settings.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").GetProperty("silence_duration_ms").GetInt32() == 600, "Requested speech pause was not applied");
            using var legacy = JsonDocument.Parse(Protocol.Json(Protocol.Session("context", 900)));
            Require(legacy.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("turn_detection").GetProperty("silence_duration_ms").GetInt32() == 900, "Cannot restore previous speech boundary");
            using var code = JsonDocument.Parse(Protocol.Json(Protocol.Session("context", 600, codeMode: true)));
            Require(!code.RootElement.GetProperty("session").TryGetProperty("reasoning", out _), "Code mode changed reasoning effort");
            Require(code.RootElement.GetProperty("session").GetProperty("max_output_tokens").GetInt32() == 4096, "Code budget must allow longer snippets");
            return Task.CompletedTask;
        });
        await Test("Screenshot prefetch shares in-flight capture; refreshes stale/other-monitor frames; clears on pause", TestFrameCache);
        await Test("Realtime handshake, frame ordering, fragmented UTF8, pause, server close (local fake server)", FakeRealtime);
        await Test("WPF renders, WDA exclusion and DDA compatibility flags round-trip, and window closes", async () =>
        {
            var window = new MainWindow(diagnostic: true);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -15000; window.Top = -15000;
            window.Show(); window.PreviewContent(); window.UpdateLayout();
            await Task.Delay(250);
            Require(window.WindowStyle == WindowStyle.None, "System title bar must be removed");
            Require(!window.AllowsTransparency && window.Opacity == 1, "Text must not use whole-window/per-pixel transparency");
            Require(SystemParameters.HighContrast || window.GlassAvailable, "Native glass was not applied on this Windows");
            var handle = new WindowInteropHelper(window).Handle;
            Require(Native.ApplyCaptureProtection(handle, true) == null, "Could not apply capture exclusion");
            Require(Native.GetWindowDisplayAffinity(handle, out uint flag) && flag == 0x11, "Capture flag mismatch");
            Require(Native.DdaExclusion(handle, true, out bool ddaOn) == null && ddaOn, "DDA exclusion could not be enabled");
            Require(Native.DdaExclusion(handle, null, out bool ddaRead) == null && ddaRead, "DDA exclusion read-back failed");
            Require(Native.DdaExclusion(handle, false, out bool ddaOff) == null && !ddaOff, "DDA exclusion could not be disabled");
            Require(Native.DdaExclusion(handle, true, out ddaOn) == null && ddaOn, "DDA exclusion could not be restored");
            Require(Native.GetWindowDisplayAffinity(handle, out flag) && flag == 0x11, "DDA changes altered WDA flag");
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string previewPath = Path.Combine(Path.GetDirectoryName(destination)!, "preview.png");
            using (var stream = File.Create(previewPath)) encoder.Save(stream);
            window.PreviewSettings(); window.UpdateLayout();
            var settingsImage = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            settingsImage.Render(window);
            var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsImage));
            using (var stream = File.Create(Path.Combine(Path.GetDirectoryName(destination)!, "settings-preview.png"))) settingsEncoder.Save(stream);
            window.Width = 500; window.Height = 680; window.UpdateLayout();
            var compactImage = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            compactImage.Render(window);
            var compactEncoder = new PngBitmapEncoder(); compactEncoder.Frames.Add(BitmapFrame.Create(compactImage));
            using (var stream = File.Create(Path.Combine(Path.GetDirectoryName(destination)!, "compact-preview.png"))) compactEncoder.Save(stream);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new
        {
            testedAt = DateTimeOffset.Now, failures, results,
            liveOpenAiTested = false, microphoneOrCallRecorded = false, discordCaptureVerified = false,
            note = "Local synthetic checks only; real provider access and exclusion in meeting apps require a user test."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }

    private static async Task TestFrameCache()
    {
        var clock = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        int captures = 0;
        var cache = new ScreenFrameCache(index => Task.FromResult("synthetic_" + index + "_" + ++captures), () => clock);
        var first = await cache.Get(0, TimeSpan.FromMilliseconds(1500));
        Require(ReferenceEquals(first, await cache.Get(0, TimeSpan.FromMilliseconds(1500))) && captures == 1, "Recent frame was recaptured");
        clock += TimeSpan.FromSeconds(2);
        var expired = await cache.Get(0, TimeSpan.FromMilliseconds(1500));
        Require(expired.Uri != first.Uri && captures == 2, "Expired frame reused");
        Require((await cache.Get(1, TimeSpan.FromMilliseconds(1500))).Monitor == 1 && captures == 3, "Wrong monitor reused");
        await cache.Get(1, TimeSpan.Zero);
        Require(captures == 4, "Manual fresh-frame request used cache");

        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrentCaptures = 0;
        var shared = new ScreenFrameCache(_ => { concurrentCaptures++; return pending.Task; }, () => clock);
        var a = shared.Get(0, TimeSpan.FromMilliseconds(1500));
        var b = shared.Get(0, TimeSpan.FromMilliseconds(1500));
        Require(ReferenceEquals(a, b) && concurrentCaptures == 1, "Concurrent hint duplicated or lost the in-flight frame");
        shared.Invalidate(); // Pause/disconnect while capture is still running.
        pending.SetResult("old-session-frame");
        await Task.WhenAll(a, b);
        await shared.Get(0, TimeSpan.FromMilliseconds(1500));
        Require(concurrentCaptures == 2, "Late capture repopulated invalidated cache");

        bool fail = true;
        var retry = new ScreenFrameCache(_ => fail ? Task.FromException<string>(new IOException("synthetic failure")) : Task.FromResult("recovered"));
        try { await retry.Get(0, TimeSpan.FromSeconds(1)); throw new Exception("Expected capture failure"); }
        catch (IOException) { }
        fail = false;
        Require((await retry.Get(0, TimeSpan.FromSeconds(1))).Uri == "recovered", "Failed capture blocked future hints");
    }

    private static async Task FakeRealtime()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var phase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string answer = "";
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = tcp.GetStream();
            var header = new StringBuilder(); var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Require(await stream.ReadAsync(one, timeout.Token) == 1, "Handshake EOF");
                header.Append((char)one[0]);
                Require(header.Length < 16384, "Oversized handshake");
            }
            string secKey = "";
            foreach (string line in header.ToString().Split("\r\n"))
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) secKey = line.Split(':', 2)[1].Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), timeout.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
            async Task<JsonElement> Read()
            {
                var buffer = new byte[65536];
                using var data = new MemoryStream(); WebSocketReceiveResult result;
                do { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); data.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(data.ToArray()); return document.RootElement.Clone();
            }
            async Task Send(object data, bool fragmented = false)
            {
                var bytes = Encoding.UTF8.GetBytes(Protocol.Json(data));
                if (fragmented)
                {
                    int split = bytes.Length - 4;
                    await socket.SendAsync(new ArraySegment<byte>(bytes, 0, split), WebSocketMessageType.Text, false, timeout.Token);
                    await socket.SendAsync(new ArraySegment<byte>(bytes, split, bytes.Length - split), WebSocketMessageType.Text, true, timeout.Token);
                }
                else await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
            }
            var config = await Read();
            Require(config.GetProperty("type").GetString() == "session.update", "Session must be first");
            var settings = config.GetProperty("session");
            Require(settings.GetProperty("output_modalities")[0].GetString() == "text", "Must not speak in call");
            Require(settings.GetProperty("audio").GetProperty("input").GetProperty("format").GetProperty("rate").GetInt32() == 24000, "Audio rate mismatch");
            Require(!settings.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").GetProperty("create_response").GetBoolean(), "Client must own response scheduling");
            Require(settings.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").GetProperty("silence_duration_ms").GetInt32() == 600, "Speech-pause setting lost in transport");
            Require(!settings.TryGetProperty("reasoning", out _), "Transport altered reasoning settings");
            await Send(new { type = "session.updated" }, true);
            var audio = await Read(); Require(audio.GetProperty("type").GetString() == "input_audio_buffer.append", "Audio event missing");
            Require(Convert.FromBase64String(audio.GetProperty("audio").GetString()!).Length == 960, "Audio frame size wrong");
            var text = await Read(); Require(text.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString() == "тест", "Unicode text changed");
            var image = await Read(); Require(image.GetProperty("item").GetProperty("content")[0].GetProperty("type").GetString() == "input_image", "Image missing");
            var response = await Read(); Require(response.GetProperty("type").GetString() == "response.create", "Response order wrong");
            await Send(new { type = "response.created" });
            await Send(new { type = "response.output_text.delta", delta = "Привет, мир" }, true);
            await Send(new { type = "response.done", response = new { status = "completed" } });
            await phase.Task.WaitAsync(timeout.Token);
            var afterPause = await Read(); Require(afterPause.GetProperty("type").GetString() == "input_audio_buffer.clear", "Sensitive event escaped pause gate");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
        }, timeout.Token);
        await using var client = new RealtimeClient();
        client.Event += (type, data) =>
        {
            if (type == "response.output_text.delta") answer += data.GetProperty("delta").GetString();
            if (type == "response.done") finished.TrySetResult();
        };
        client.Failed += _ => closed.TrySetResult();
        try
        {
            await client.Connect("synthetic-key", "synthetic-model", "synthetic-context", new Uri($"ws://127.0.0.1:{port}/"));
            client.AppendAudio(new byte[960]); client.Queue(Protocol.Text("тест")); client.Queue(Protocol.Image("data:image/jpeg;base64,AA=="));
            client.Queue(new { type = "response.create" });
            await finished.Task.WaitAsync(timeout.Token);
            Require(answer == "Привет, мир", "Fragmented UTF8 response corrupted");
            client.SetPaused(true); client.AppendAudio(new byte[960]); client.Queue(Protocol.Image("data:image/jpeg;base64,AA=="));
            client.Queue(new { type = "input_audio_buffer.clear" }); phase.TrySetResult();
            await server.WaitAsync(timeout.Token);
            await closed.Task.WaitAsync(timeout.Token);
            Require(!client.IsReady, "Server closure must clear readiness");
        }
        finally { listener.Stop(); }
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
