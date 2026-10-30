using NAudio.Wave;

namespace Wavely.Core;

public sealed class LibraryService
{
    public IReadOnlyList<Recording> Scan(IEnumerable<string> Directories)
    {
        List<Recording> Items = [];
        foreach (var DirectoryPath in Directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(DirectoryPath)) continue;
                foreach (var PathName in Directory.EnumerateFiles(DirectoryPath, "*.wav"))
                {
                    try
                    {
                        using var Reader = new WaveFileReader(PathName);
                        var Info = new FileInfo(PathName);
                        var Name = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(PathName), @" \[[0-9a-f]{8}\]$", "");
                        Items.Add(new(PathName, Name, Info.CreationTime, Reader.TotalTime, Info.Length));
                    }
                    catch (Exception Error) when (Error is IOException or FormatException or InvalidDataException) { AppPaths.Log(Error); }
                }
            }
            catch (Exception Error) when (Error is IOException or UnauthorizedAccessException) { AppPaths.Log(Error); }
        }
        return Items.OrderByDescending(Item => Item.Created).ToArray();
    }

    public static string Rename(Recording Item, string Name)
    {
        Name = Name.Trim();
        if (string.IsNullOrEmpty(Name) || Name.Length > 120 || Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Name.EndsWith('.') || Name is "." or "..")
            throw new ArgumentException("Use a name of 1–120 characters without file path symbols or a trailing period.");
        var Stem = Name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(Stem)) throw new ArgumentException("That name is reserved by Windows.");
        var Destination = Path.Combine(Path.GetDirectoryName(Item.Path)!, Name + ".wav");
        if (!string.Equals(Item.Path, Destination, StringComparison.OrdinalIgnoreCase)) File.Move(Item.Path, Destination);
        return Destination;
    }

    public static void Export(string Source, string Destination, int Bitrate)
    {
        var Extension = Path.GetExtension(Destination).ToLowerInvariant();
        var Temporary = Path.Combine(Path.GetDirectoryName(Destination)!, $".wavely-{Guid.NewGuid():N}{Extension}");
        try
        {
            using (var Reader = new WaveFileReader(Source))
            {
                if (Extension == ".mp3") MediaFoundationEncoder.EncodeToMp3(Reader, Temporary, Bitrate);
                else if (Extension == ".flac") MediaFoundationEncoder.EncodeToFlac(Reader, Temporary);
                else throw new ArgumentException("Choose MP3 or FLAC.");
            }
            using (var Stream = new FileStream(Temporary, FileMode.Open, FileAccess.ReadWrite)) Stream.Flush(true);
            File.Move(Temporary, Destination, true);
        }
        finally { if (File.Exists(Temporary)) File.Delete(Temporary); }
    }

    public static string Trim(Recording Item, TimeSpan Start, TimeSpan End)
    {
        using var Reader = new WaveFileReader(Item.Path);
        if (Start < TimeSpan.Zero || End > Reader.TotalTime || End <= Start) throw new ArgumentException("Choose a valid start and end within the recording.");
        var Output = Path.Combine(Path.GetDirectoryName(Item.Path)!, $"{Item.Name} (trimmed {DateTime.Now:HHmmss}) {Guid.NewGuid().ToString("N")[..4]}.wav");
        var Temporary = Output + ".partial";
        try
        {
            using (var Writer = new WaveFileWriter(Temporary, Reader.WaveFormat))
            {
                var Alignment = Reader.WaveFormat.BlockAlign;
                var StartByte = (long)(Start.TotalSeconds * Reader.WaveFormat.AverageBytesPerSecond) / Alignment * Alignment;
                var EndByte = (long)(End.TotalSeconds * Reader.WaveFormat.AverageBytesPerSecond) / Alignment * Alignment;
                Reader.Position = StartByte;
                var Buffer = new byte[65536 / Alignment * Alignment];
                while (Reader.Position < EndByte)
                {
                    var Count = Reader.Read(Buffer, 0, (int)Math.Min(Buffer.Length, EndByte - Reader.Position));
                    if (Count == 0) break;
                    Writer.Write(Buffer, 0, Count);
                }
            }
            File.Move(Temporary, Output);
            return Output;
        }
        finally { if (File.Exists(Temporary)) File.Delete(Temporary); }
    }

    public static float[] ReadPeaks(string PathName, int Count = 180)
    {
        using var Reader = new WaveFileReader(PathName);
        var Peaks = new float[Count];
        var Provider = Reader.ToSampleProvider();
        var Buffer = new float[8192];
        var TotalSamples = Reader.Length / Math.Max(1, Reader.WaveFormat.BitsPerSample / 8);
        long Offset = 0;
        int Read;
        while ((Read = Provider.Read(Buffer)) > 0)
        {
            for (var Index = 0; Index < Read; Index++)
            {
                var Bucket = (int)Math.Min(Count - 1, (Offset + Index) * Count / Math.Max(1, TotalSamples));
                if (float.IsFinite(Buffer[Index])) Peaks[Bucket] = Math.Max(Peaks[Bucket], Math.Clamp(Math.Abs(Buffer[Index]), 0, 1));
            }
            Offset += Read;
        }
        return Peaks;
    }
}
