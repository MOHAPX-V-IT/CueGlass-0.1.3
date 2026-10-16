using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CueGlass;

public sealed class Preferences
{
    public string Model { get; set; } = "gpt-realtime-2.1";
    public string MicId { get; set; } = "";
    public string OutputId { get; set; } = "";
    public string Context { get; set; } = "";
    public bool Microphone { get; set; } = true;
    public bool SystemAudio { get; set; } = true;
    public bool Screen { get; set; } = true;
    public bool AutoHints { get; set; } = true;
    public bool PeriodicScreen { get; set; }
    public bool ProtectWindow { get; set; } = true;
    public bool CaptureCompatibility { get; set; } = true;
    public int SpeechPauseMs { get; set; } = 600;
    public bool CodeMode { get; set; }
    public double WindowOpacity { get; set; } = 1;
    public bool GlassEffect { get; set; } = true;
    public double GlassDensity { get; set; } = 0.68;
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CueGlass");
    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Folder, "settings.json"))) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class KeyVault
{
    private static string FilePath => Path.Combine(Preferences.Folder, "api-key.dpapi");
    public static byte[] Protect(string value) => ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
    public static string Unprotect(byte[] value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser));
    public static string Read()
    {
        try { return File.Exists(FilePath) ? Unprotect(File.ReadAllBytes(FilePath)) : ""; }
        catch { return ""; }
    }
    public static void Save(string key)
    {
        Directory.CreateDirectory(Preferences.Folder);
        File.WriteAllBytes(FilePath, Protect(key));
    }
    public static void Delete() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}
