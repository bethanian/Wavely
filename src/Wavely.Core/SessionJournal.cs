using System.Text.Json;
using NAudio.Wave;

namespace Wavely.Core;

public sealed class SessionJournal : IDisposable
{
    public SessionInfo Info { get; }
    public string RawPath { get; }
    public string MetadataPath { get; }
    private readonly FileStream Stream;
    public long Length => Stream.Length;

    public SessionJournal(string Destination, int SampleRate)
    {
        Directory.CreateDirectory(Destination);
        var Probe = Path.Combine(Destination, $".wavely-{Guid.NewGuid():N}.tmp");
        using (new FileStream(Probe, FileMode.CreateNew, FileAccess.Write)) { }
        File.Delete(Probe);
        Directory.CreateDirectory(AppPaths.Sessions);
        var Id = Guid.NewGuid().ToString("N");
        Info = new(Id, $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}", Path.GetFullPath(Destination), SampleRate, DateTime.Now);
        MetadataPath = Path.Combine(AppPaths.Sessions, Id + ".json");
        RawPath = Path.Combine(AppPaths.Sessions, Id + ".pcm");
        AppPaths.AtomicJson(MetadataPath, Info);
        Stream = new(RawPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
    }

    public void Write(ReadOnlySpan<byte> Data) => Stream.Write(Data);
    public void Flush() => Stream.Flush(true);
    public void Dispose() => Stream.Dispose();

    public void DiscardIfEmpty()
    {
        Dispose();
        if (File.Exists(RawPath) && new FileInfo(RawPath).Length == 0) { File.Delete(MetadataPath); File.Delete(RawPath); }
    }

    public string FinalizeRecording()
    {
        Flush();
        Dispose();
        return Recover(MetadataPath);
    }

    public static string Recover(string MetadataPath, string? Destination = null)
    {
        var Info = JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(MetadataPath)) ?? throw new InvalidDataException("Invalid session metadata.");
        if (Info.SampleRate is not (44100 or 48000)) throw new InvalidDataException("Invalid session sample rate.");
        if (Info.Id.Length != 32 || !Info.Id.All(Uri.IsHexDigit) || Path.GetFileNameWithoutExtension(MetadataPath) != Info.Id || string.IsNullOrWhiteSpace(Info.Name) || Info.Name != Path.GetFileName(Info.Name)) throw new InvalidDataException("Invalid session identity.");
        var RawPath = Path.ChangeExtension(MetadataPath, ".pcm");
        var OutputDirectory = Destination ?? Info.Destination;
        Directory.CreateDirectory(OutputDirectory);
        var Output = Path.Combine(OutputDirectory, $"{Info.Name} [{Info.Id[..8]}].wav");
        if (File.Exists(Output))
        {
            using var Existing = new WaveFileReader(Output);
            if (Existing.Length != new FileInfo(RawPath).Length / 4 * 4) throw new IOException("A conflicting recovery file already exists.");
        }
        else
        {
            var Temporary = Output + ".partial";
            using (var Input = File.OpenRead(RawPath))
            using (var Writer = new WaveFileWriter(Temporary, new WaveFormat(Info.SampleRate, 16, 2)))
            {
                var Remaining = Input.Length / 4 * 4;
                var Buffer = new byte[65536];
                while (Remaining > 0)
                {
                    var Count = Input.Read(Buffer, 0, (int)Math.Min(Buffer.Length, Remaining));
                    if (Count == 0) throw new EndOfStreamException();
                    Writer.Write(Buffer, 0, Count);
                    Remaining -= Count;
                }
            }
            using (var Durable = new FileStream(Temporary, FileMode.Open, FileAccess.ReadWrite)) Durable.Flush(true);
            File.Move(Temporary, Output);
        }
        File.Delete(MetadataPath);
        File.Delete(RawPath);
        return Output;
    }
}
