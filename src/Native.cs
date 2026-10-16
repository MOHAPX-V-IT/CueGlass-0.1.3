using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CueGlass;

public static class Native
{
    public const uint ExcludeFromCapture = 0x11;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData
    {
        public int Attribute;
        public IntPtr Data;
        public uint Size;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool CompositionFunction(IntPtr hwnd, ref CompositionData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State, Flags; public uint Tint; public int Animation; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rectangle rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    public static void RoundWindow(IntPtr hwnd, int radius, bool maximized)
    {
        if (maximized) { SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
        if (!GetWindowRect(hwnd, out var bounds)) return;
        var region = CreateRoundRectRgn(0, 0, bounds.Right - bounds.Left + 1, bounds.Bottom - bounds.Top + 1, radius * 2, radius * 2);
        // Windows owns the region after a successful SetWindowRgn.
        if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    public static bool SetGlass(IntPtr hwnd, bool enabled)
    {
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        var margins = enabled ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new Margins();
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int backdrop = enabled ? 3 : 1; // DWMSBT_TRANSIENTWINDOW / DWMSBT_NONE.
            if (DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) >= 0) return true;
        }
        if (!NativeLibrary.TryLoad("user32.dll", typeof(Native).Assembly, DllImportSearchPath.System32, out var library)) return false;
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            if (!NativeLibrary.TryGetExport(library, "SetWindowCompositionAttribute", out var address)) return false;
            var setter = Marshal.GetDelegateForFunctionPointer<CompositionFunction>(address);
            var policy = new AccentPolicy { State = enabled ? 3 : 0, Flags = 2, Tint = 0x22201610 };
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new CompositionData { Attribute = 19, Data = pointer, Size = (uint)Marshal.SizeOf<AccentPolicy>() };
            return setter(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(pointer); NativeLibrary.Free(library); }
    }

    // Load the exports dynamically as required by Microsoft's API documentation.
    // BOOL is four bytes; UINT is four bytes on both x86 and x64.
    public static string? DdaExclusion(IntPtr hwnd, bool? set, out bool enabled)
    {
        enabled = false;
        if (!NativeLibrary.TryLoad("user32.dll", typeof(Native).Assembly, DllImportSearchPath.System32, out var library))
            return "user32.dll недоступна";
        IntPtr value = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            var data = new CompositionData { Attribute = 24, Data = value, Size = sizeof(int) };
            if (set.HasValue)
            {
                if (!NativeLibrary.TryGetExport(library, "SetWindowCompositionAttribute", out var address))
                    return "Режим совместимости не поддерживается этой Windows";
                Marshal.WriteInt32(value, set.Value ? 1 : 0);
                var setter = Marshal.GetDelegateForFunctionPointer<CompositionFunction>(address);
                if (!setter(hwnd, ref data)) return "Не удалось установить режим совместимости (" + Marshal.GetLastWin32Error() + ")";
            }
            if (!NativeLibrary.TryGetExport(library, "GetWindowCompositionAttribute", out var getterAddress))
                return "Windows не предоставляет проверку режима совместимости";
            Marshal.WriteInt32(value, 0);
            var getter = Marshal.GetDelegateForFunctionPointer<CompositionFunction>(getterAddress);
            if (!getter(hwnd, ref data)) return "Не удалось прочитать режим совместимости (" + Marshal.GetLastWin32Error() + ")";
            enabled = Marshal.ReadInt32(value) != 0;
            if (set.HasValue && enabled != set.Value) return "Windows не подтвердила режим совместимости";
            return null;
        }
        finally { Marshal.FreeHGlobal(value); NativeLibrary.Free(library); }
    }

    public static string? ApplyCaptureProtection(IntPtr hwnd, bool enabled)
    {
        if (enabled && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return "Нужна Windows 10 2004 или новее.";
        uint expected = enabled ? ExcludeFromCapture : 0;
        if (!SetWindowDisplayAffinity(hwnd, expected))
            return new Win32Exception(Marshal.GetLastWin32Error()).Message;
        if (!GetWindowDisplayAffinity(hwnd, out uint actual) || actual != expected)
            return "Windows не подтвердила исключение окна из захвата.";
        return null;
    }

    // Frames exist in memory only. We never save screenshots to disk.
    public static string CaptureScreen(int index)
    {
        var screens = Screen.AllScreens;
        if (index < 0 || index >= screens.Length) throw new InvalidOperationException("Выбранный монитор недоступен.");
        var bounds = screens[index].Bounds;
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        double scale = Math.Min(1.0, 1600.0 / Math.Max(bounds.Width, bounds.Height));
        using var resized = new Bitmap(Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale)));
        using (var g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, resized.Width, resized.Height);
        }
        using var stream = new MemoryStream();
        var encoder = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.MimeType == "image/jpeg")!;
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
        resized.Save(stream, encoder, quality);
        return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
    }
}
