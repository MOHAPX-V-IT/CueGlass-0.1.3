using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CueGlass;

// Only self-owned synthetic windows are captured; no microphone or API is used.
public static class VisualCheck
{
    public static async Task<int> Run(string destination)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        var board = new Window
        {
            WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 30, Top = 20, Width = 760, Height = 960, ResizeMode = ResizeMode.NoResize,
            Background = new LinearGradientBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#1A8E85"), (System.Windows.Media.Color)ColorConverter.ConvertFromString("#6C478D"), 30),
            ShowInTaskbar = false, Topmost = true
        };
        var fixture = new Canvas();
        for (int i = 0; i < 18; i++)
        {
            var stripe = new System.Windows.Shapes.Rectangle { Width = 16, Height = 960, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 245, 255, 255)) };
            Canvas.SetLeft(stripe, i * 45); fixture.Children.Add(stripe);
        }
        fixture.Children.Add(new Ellipse { Width = 320, Height = 320, Fill = new RadialGradientBrush(System.Windows.Media.Color.FromArgb(220, 40, 225, 186), Colors.Transparent), Margin = new Thickness(0, 170, 0, 0) });
        board.Content = fixture; board.Show();
        var window = new MainWindow(diagnostic: true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = 90, Top = 65 };
        bool success = false;
        try
        {
            // Record a point covered by the future window, before it is shown.
            var sampleLocation = board.PointToScreen(new System.Windows.Point(80, 330));
            await Task.Delay(180);
            var expected = Pixel((int)sampleLocation.X, (int)sampleLocation.Y);
            window.Show(); window.PreviewContent(); window.UpdateLayout();
            await Task.Delay(300);
            var handle = new WindowInteropHelper(window).Handle;
            Native.ApplyCaptureProtection(handle, false); Native.DdaExclusion(handle, false, out _);
            await Task.Delay(250);
            var origin = window.PointToScreen(new System.Windows.Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(window);
            int width = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX), height = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
            using (var image = new Bitmap(width, height))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(width, height));
                image.Save(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, "glass-preview.png"), ImageFormat.Png);
            }
            Native.ApplyCaptureProtection(handle, true); Native.DdaExclusion(handle, true, out bool dda);
            await Task.Delay(250);
            var actual = Pixel((int)sampleLocation.X, (int)sampleLocation.Y);
            bool excluded = Math.Abs(expected.R - actual.R) < 3 && Math.Abs(expected.G - actual.G) < 3 && Math.Abs(expected.B - actual.B) < 3;
            success = window.GlassAvailable && excluded && window.WindowStyle == WindowStyle.None && !window.AllowsTransparency && dda;
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new
            {
                version = "0.1.3", success, nativeGlassApplied = window.GlassAvailable,
                systemTitleBarRemoved = window.WindowStyle == WindowStyle.None,
                gdiCaptureRestoresSyntheticBackground = excluded, ddaFlag = dda,
                expectedBackground = new int[] { expected.R, expected.G, expected.B },
                actualProtectedPixel = new int[] { actual.R, actual.G, actual.B },
                liveOpenAiTested = false, discordCaptureVerified = false,
                note = "Actual on-screen UI over a synthetic backdrop; GDI check does not establish Discord compatibility."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult(); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); board.Close();
        }
        return success ? 0 : 1;
    }
    private static System.Drawing.Color Pixel(int x, int y)
    {
        using var image = new Bitmap(1, 1);
        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(1, 1));
        return image.GetPixel(0, 0);
    }
}
