using System.Text.Json;

namespace Wavely.Core;

public enum AudioSource { Microphone, SystemAudio, Both }
public enum RecordingState { Idle, Recording, Paused, Finalizing }
public sealed record AudioDevice(string Id, string Name) { public override string ToString() => Name; }
public sealed record Recording(string Path, string Name, DateTime Created, TimeSpan Duration, long Bytes)
{
    public string Detail => $"{Created:g}  ·  {Duration:hh\\:mm\\:ss}  ·  {Bytes / 1048576d:0.0} MB";
    public override string ToString() => $"{Name}, {Detail}";
}
public sealed record SessionInfo(string Id, string Name, string Destination, int SampleRate, DateTime Created);

public sealed class AppSettings
{
    public string SaveDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "Wavely");
    public string? MicrophoneId { get; set; }
    public string? OutputId { get; set; }
    public AudioSource Source { get; set; }
    public int SampleRate { get; set; } = 48000;
    public int Mp3Bitrate { get; set; } = 192000;
    public string Theme { get; set; } = "Default";
    public bool MinimizeToTray { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = 6;
    public uint HotkeyKey { get; set; } = 0x52;
    public List<string> LibraryDirectories { get; set; } = [];
}

public static class AppPaths
{
    public static string Data { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavely");
    public static string Sessions => Path.Combine(Data, "Sessions");
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true };

    public static void AtomicJson<T>(string PathName, T Value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var Temporary = PathName + ".tmp";
        using (var Stream = new FileStream(Temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(Stream, Value, Json);
            Stream.Flush(true);
        }
        File.Move(Temporary, PathName, true);
    }

    public static void Log(Exception Error)
    {
        try { Directory.CreateDirectory(Data); File.AppendAllText(Path.Combine(Data, "errors.log"), $"{DateTimeOffset.Now:O} {Error}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class SettingsStore
{
    private readonly string SettingsPath = Path.Combine(AppPaths.Data, "settings.json");
    public AppSettings Load()
    {
        try
        {
            var Settings = File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new() : new();
            Settings.SampleRate = Settings.SampleRate == 44100 ? 44100 : 48000;
            Settings.Mp3Bitrate = Settings.Mp3Bitrate is 128000 or 192000 or 320000 ? Settings.Mp3Bitrate : 192000;
            Settings.LibraryDirectories ??= [];
            if (string.IsNullOrWhiteSpace(Settings.SaveDirectory)) Settings.SaveDirectory = new AppSettings().SaveDirectory;
            return Settings;
        }
        catch (Exception Error) when (Error is IOException or JsonException or UnauthorizedAccessException) { AppPaths.Log(Error); return new(); }
    }
    public void Save(AppSettings Settings) => AppPaths.AtomicJson(SettingsPath, Settings);
}
