using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Wavely.Core;

var TestDirectory = Path.Combine(Path.GetTempPath(), "Wavely.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(TestDirectory);
var Passed = 0;
void Assert(bool Condition, string Message) { if (!Condition) throw new Exception(Message); }
async Task Check(string Name, Func<Task> Test)
{
    try { await Test(); Console.WriteLine($"PASS {Name}"); Passed++; }
    catch (Exception Error) { Console.Error.WriteLine($"FAIL {Name}: {Error}"); Environment.ExitCode = 1; }
}
if (args.FirstOrDefault() == "--journal-child")
{
    using var Journal = new SessionJournal(args[1], 48000);
    var Data = new byte[48000 * 4];
    for (var Index = 0; Index < Data.Length / 2; Index++) BitConverter.TryWriteBytes(Data.AsSpan(Index * 2), (short)(Math.Sin(Index / 2 * Math.PI * 2 * 440 / 48000) * 10000));
    Journal.Write(Data); Journal.Flush();
    File.WriteAllText(Path.Combine(args[1], "child.ready"), Journal.MetadataPath);
    await Task.Delay(Timeout.Infinite); return;
}
if (args.Contains("--capture-trace"))
{
    foreach (var Loopback in new[] { false, true })
    {
        var Builder = new WasapiRecorderBuilder().WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        if (Loopback) Builder.WithLoopbackCapture();
        await using var Recorder = Builder.Build();
        var Packets = 0; long LastDevice = 0, LastQpc = 0;
        Recorder.DataAvailable += (Buffer, Flags, Device, Qpc) =>
        {
            if (Packets++ < 30) Console.WriteLine($"TRACE loop={Loopback} n={Buffer.Length / 8} pos={Device} delta={Device - LastDevice} qpcDelta={Qpc - LastQpc} flags={Flags}");
            LastDevice = Device; LastQpc = Qpc;
        };
        Recorder.StartRecording();
        await Task.Delay(600);
    }
    return;
}
if (args.Contains("--microphone-quality"))
{
    var Settings = new SettingsStore().Load();
    Settings.SaveDirectory = TestDirectory;
    Settings.Source = AudioSource.Microphone;
    foreach (var Rate in new[] { 48000, 44100 })
    {
        Settings.SampleRate = Rate;
        var Engine = new RecordingEngine();
        await Engine.StartAsync(Settings);
        await Task.Delay(6000);
        var Output = await Engine.StopAsync();
        Console.WriteLine($"QUALITY rate={Rate} lateFrames={Engine.LateFrames} file={Output}");
    }
    return;
}

await Check("Timestamp placement, silence, mix headroom, late-packet handling", () =>
{
    var Mixer = new TimelineMixer(48000, 2);
    Mixer.Add(0, 2, new float[] { 1, -1, 1, -1 });
    Mixer.Add(1, 2, new float[] { 1, 1, 1, 1 });
    var Output = new byte[16]; Mixer.Render(Output);
    Assert(BitConverter.ToInt16(Output, 0) == 0 && BitConverter.ToInt16(Output, 4) == 0, "Gap was not silent");
    Assert(BitConverter.ToInt16(Output, 8) == 32767 && BitConverter.ToInt16(Output, 10) == 0, "Mix clipped or had wrong alignment");
    Mixer.Add(0, 0, new float[] { 1, 1 }); Assert(Mixer.LateFrames == 1, "Late frame was not detected");
    var Invalid = new TimelineMixer(48000, 1); Invalid.Add(0, 0, new float[] { float.NaN, float.PositiveInfinity }); Invalid.Render(Output.AsSpan(0, 4));
    Assert(BitConverter.ToInt32(Output, 0) == 0, "Nonfinite audio was not sanitized");
    return Task.CompletedTask;
});

await Check("Microphone packet timestamp jitter cannot insert clicks", () =>
{
    const int Rate = 48000, PacketFrames = 480, PacketCount = 100;
    const long Anchor = 10_000_000;
    var Timeline = new CaptureTimeline(Rate);
    var Mixer = new TimelineMixer(Rate, 1);
    for (var Packet = 0; Packet < PacketCount; Packet++)
    {
        var Samples = new float[PacketFrames * 2];
        for (var Frame = 0; Frame < PacketFrames; Frame++)
            Samples[Frame * 2] = Samples[Frame * 2 + 1] = (float)(0.4 + 0.1 * Math.Sin((Packet * PacketFrames + Frame) * Math.PI * 2 * 440 / Rate));
        var Jitter = Packet == 0 ? 0 : Packet % 3 == 0 ? 400 : Packet % 3 == 1 ? 200 : -200;
        var Position = Timeline.Place(Packet * PacketFrames, Anchor + Packet * 100_000 + Jitter, PacketFrames, 0, Anchor);
        Mixer.Add(0, Position, Samples);
    }
    var Output = new byte[PacketFrames * PacketCount * 4]; Mixer.Render(Output);
    var Holes = 0; var LargestStep = 0;
    for (var Frame = 0; Frame < Output.Length / 4; Frame++)
    {
        var Sample = BitConverter.ToInt16(Output, Frame * 4);
        if (Sample == 0) Holes++;
        if (Frame > 0) LargestStep = Math.Max(LargestStep, Math.Abs(Sample - BitConverter.ToInt16(Output, (Frame - 1) * 4)));
    }
    Assert(Holes == 0 && LargestStep < 250, $"Packet seams introduced {Holes} zero frames; largest step {LargestStep}");
    return Task.CompletedTask;
});

await Check("Native device clocks, empty packets, real gaps and pause anchors", () =>
{
    const long Anchor = 10_000_000;
    var Timeline = new CaptureTimeline(48000, 44100);
    Assert(Timeline.Place(0, Anchor, 474, 0, Anchor) == 0, "Wrong initial position");
    Timeline.Place(0, 0, 0, 0, Anchor);
    Assert(Timeline.Place(441, Anchor + 100000, 480, 0, Anchor) == 474, "Resampler startup created a gap");
    Assert(Timeline.Place(882, Anchor + 200400, 480, 0, Anchor) == 954, "Native clock conversion changed continuity");
    Assert(Timeline.Place(1764, Anchor + 400000, 480, 0, Anchor) == 1914, "An actual missing packet was concealed");
    Assert(Timeline.Place(44100, Anchor + 10000000, 480, 2000, Anchor + 10000000) == 2000, "Resume retained the pre-pause clock anchor");
    return Task.CompletedTask;
});

await Check("Killed-process recovery and frame-aligned WAV integrity", async () =>
{
    var Executable = Environment.ProcessPath!;
    var Start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
    Start.ArgumentList.Add("--journal-child"); Start.ArgumentList.Add(TestDirectory);
    using var Child = Process.Start(Start)!;
    var Ready = Path.Combine(TestDirectory, "child.ready");
    try
    {
        var Deadline = Stopwatch.StartNew();
        while (!File.Exists(Ready) && Deadline.Elapsed.TotalSeconds < 15) await Task.Delay(50);
        Assert(File.Exists(Ready), "Child did not flush its journal");
        Child.Kill(); await Child.WaitForExitAsync();
        var Metadata = File.ReadAllText(Ready);
        var Raw = Path.ChangeExtension(Metadata, ".pcm");
        for (var Attempt = 0; ; Attempt++)
        {
            try { using var Tail = new FileStream(Raw, FileMode.Append); Tail.WriteByte(99); break; }
            catch (IOException) when (Attempt < 20) { await Task.Delay(100); }
        }
        var Output = SessionJournal.Recover(Metadata);
        using var Reader = new WaveFileReader(Output);
        Assert(Reader.Length == 48000 * 4 && Reader.TotalTime == TimeSpan.FromSeconds(1), "Recovered PCM length incorrect");
        Assert(!File.Exists(Metadata) && !File.Exists(Raw), "Recovered journal not retired");
    }
    finally { if (!Child.HasExited) { Child.Kill(); await Child.WaitForExitAsync(); } }
});

string Fixture = Path.Combine(TestDirectory, "Signal.wav");
WaveFileWriter.CreateWaveFile16(Fixture, new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.2, Type = SignalGeneratorType.Sin }.Take(TimeSpan.FromSeconds(2)));
await Check("Trim, rename validation, library scan, peak extraction", () =>
{
    var Service = new LibraryService(); var Item = Service.Scan(new[] { TestDirectory }).First(Item => Item.Path == Fixture);
    var Trimmed = LibraryService.Trim(Item, TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(1.25));
    using var Reader = new WaveFileReader(Trimmed); Assert(Math.Abs(Reader.TotalTime.TotalSeconds - 1) < 0.001, "Trim duration wrong");
    Assert(LibraryService.ReadPeaks(Fixture).Max() > 0.19, "Peaks missing");
    foreach (var Name in new[] { "..", "CON", "invalid/name", "trailing." })
    {
        var Rejected = false; try { LibraryService.Rename(Item, Name); } catch (ArgumentException) { Rejected = true; }
        Assert(Rejected, $"Invalid name accepted: {Name}");
    }
    return Task.CompletedTask;
});
await Check("MP3 and FLAC encode/decode with Windows Media Foundation", () =>
{
    foreach (var Extension in new[] { ".mp3", ".flac" })
    {
        var Output = Path.Combine(TestDirectory, "Export" + Extension);
        LibraryService.Export(Fixture, Output, 192000);
        using var Reader = new MediaFoundationReader(Output);
        Assert(Math.Abs(Reader.TotalTime.TotalSeconds - 2) < 0.2, $"{Extension} export duration wrong");
        var Buffer = new byte[8192]; Assert(Reader.Read(Buffer, 0, Buffer.Length) > 0, $"{Extension} has no decodable audio");
    }
    return Task.CompletedTask;
});
await Check("Invalid destination does not start capture", async () =>
{
    var BlockedPath = Path.Combine(TestDirectory, "NotAFolder"); File.WriteAllText(BlockedPath, "test");
    var Engine = new RecordingEngine(); var Rejected = false;
    try { await Engine.StartAsync(new AppSettings { SaveDirectory = BlockedPath }); } catch (IOException) { Rejected = true; }
    Assert(Rejected && Engine.State == RecordingState.Idle, "Invalid destination was accepted");
});

if (args.Contains("--devices"))
{
    foreach (var Flow in new[] { NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DataFlow.Render })
        foreach (var Device in RecordingEngine.Enumerate(Flow)) Console.WriteLine($"DEVICE {Flow}: {Device.Name} [{Device.Id}]");
    foreach (var Source in Enum.GetValues<AudioSource>())
        await Check($"WASAPI {Source}: record, pause, resume, finalize, playback, seek", async () =>
        {
            var Engine = new RecordingEngine(); Exception? Fault = null; Engine.Faulted += Error => Fault = Error;
            using var ToneReader = new WaveFileReader(Fixture);
            await using var Tone = new WasapiPlayerBuilder().Build(); Tone.Init(ToneReader);
            try
            {
                await Engine.StartAsync(new AppSettings { SaveDirectory = TestDirectory, Source = Source });
                Tone.Play(); await Task.Delay(1100);
                Engine.TogglePause(); var Before = Engine.Elapsed; await Task.Delay(400);
                Assert(Engine.Elapsed == Before, "Pause advanced the audio timeline");
                Engine.TogglePause(); await Task.Delay(1000);
                var Output = await Engine.StopAsync();
                Assert(Fault == null, $"Capture fault: {Fault}"); Assert(Output != null, "No recording finalized");
                using var Reader = new WaveFileReader(Output!);
                Assert(Math.Abs(Reader.TotalTime.TotalSeconds - 2.1) < 0.3, $"Bad duration {Reader.TotalTime}");
                if (Source != AudioSource.Microphone) Assert(LibraryService.ReadPeaks(Output!).Max() > 0.01, "Loopback did not capture the test tone");
                await using var Playback = new PlaybackService(); await Playback.LoadAsync(Output!, null);
                Playback.Toggle(); await Task.Delay(200); Assert(Playback.IsPlaying, "Playback failed to start");
                Playback.Toggle(); Playback.Seek(TimeSpan.FromSeconds(1)); Assert(Math.Abs(Playback.Position.TotalSeconds - 1) < 0.05, "Seek failed");
                Playback.Toggle(); await Task.Delay(150);
                Console.WriteLine($"  duration={Reader.TotalTime.TotalSeconds:0.000}s lateFrames={Engine.LateFrames} peak={LibraryService.ReadPeaks(Output!).Max():0.000}");
            }
            finally { if (Engine.State is RecordingState.Recording or RecordingState.Paused) await Engine.StopAsync(); }
        });
    await Check("Explicit device switching between sessions", async () =>
    {
        var Engine = new RecordingEngine();
        foreach (var Device in RecordingEngine.Enumerate(NAudio.CoreAudioApi.DataFlow.Capture))
        {
            await Engine.StartAsync(new AppSettings { SaveDirectory = TestDirectory, MicrophoneId = Device.Id, SampleRate = 44100 });
            await Task.Delay(350);
            var Output = await Engine.StopAsync();
            using var Reader = new WaveFileReader(Output!); Assert(Reader.WaveFormat.SampleRate == 44100 && Reader.Length > 0, "Device switch produced invalid WAV");
        }
    });
    await Check("Unavailable endpoint fails safely", async () =>
    {
        var Engine = new RecordingEngine(); var Rejected = false;
        try { await Engine.StartAsync(new AppSettings { SaveDirectory = TestDirectory, MicrophoneId = "{missing-device}" }); }
        catch { Rejected = true; }
        Assert(Rejected && Engine.State == RecordingState.Idle, "Missing device was not handled");
    });
    await Check("Finalization failure preserves recoverable audio", async () =>
    {
        var Destination = Path.Combine(TestDirectory, "InterruptedDestination");
        var Engine = new RecordingEngine();
        await Engine.StartAsync(new AppSettings { SaveDirectory = Destination, Source = AudioSource.SystemAudio });
        await Task.Delay(400);
        Directory.Move(Destination, Destination + ".moved"); File.WriteAllText(Destination, "blocked");
        var Rejected = false;
        try { await Engine.StopAsync(); } catch (IOException) { Rejected = true; }
        Assert(Rejected && Engine.State == RecordingState.Idle, "Finalization failure did not return to idle");
        var Metadata = Directory.GetFiles(AppPaths.Sessions, "*.json").First(PathName => JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(PathName))?.Destination == Destination);
        Assert(new FileInfo(Path.ChangeExtension(Metadata, ".pcm")).Length > 0, "Unfinalized audio was lost");
        var Output = SessionJournal.Recover(Metadata, TestDirectory);
        using var Reader = new WaveFileReader(Output); Assert(Reader.TotalTime.TotalSeconds > 0.3, "Saved recovery too short");
    });
}
Console.WriteLine($"{Passed} checks passed. Artifacts: {TestDirectory}");
