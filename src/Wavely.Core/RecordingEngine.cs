using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Wavely.Core;

public sealed class RecordingEngine
{
    private readonly object Gate = new();
    private readonly List<WasapiRecorder> Captures = [];
    private readonly List<MMDevice> Devices = [];
    private SessionJournal? Journal;
    private TimelineMixer? Mixer;
    private CancellationTokenSource? Cancellation;
    private Task? Pump;
    private int SampleRate;
    private long AnchorQpc;
    private long SegmentFrame;
    private long PausedFrame;
    private Exception? CaptureError;
    private RecordingState CurrentState;
    private float CurrentPeak;
    private bool AcceptFinalPackets;
    private bool MicrophoneOnly;
    private long CapturedFrames;
    public RecordingState State { get { lock (Gate) return CurrentState; } }
    public float Peak { get { lock (Gate) return CurrentState == RecordingState.Recording ? CurrentPeak : 0; } }
    public TimeSpan Elapsed { get { lock (Gate) return TimeSpan.FromSeconds(CurrentFrames() / (double)Math.Max(1, SampleRate)); } }
    public long LateFrames { get { lock (Gate) return Mixer?.LateFrames ?? 0; } }
    public event Action<Exception>? Faulted;
    public static long QpcNow => (long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency));

    public static IReadOnlyList<AudioDevice> Enumerate(DataFlow Flow)
    {
        using var Enumerator = new MMDeviceEnumerator();
        return Enumerator.EnumerateAudioEndPoints(Flow, DeviceState.Active).Select(Device =>
        {
            using (Device) return new AudioDevice(Device.ID, Device.FriendlyName);
        }).ToArray();
    }

    public async Task StartAsync(AppSettings Settings)
    {
        if (State != RecordingState.Idle) throw new InvalidOperationException("A recording is already active.");
        try
        {
            SampleRate = Settings.SampleRate;
            MicrophoneOnly = Settings.Source == AudioSource.Microphone;
            CapturedFrames = 0;
            CaptureError = null;
            Journal = new(Settings.SaveDirectory, SampleRate);
            Mixer = new(SampleRate, Settings.Source == AudioSource.Both ? 2 : 1);
            using var Enumerator = new MMDeviceEnumerator();
            if (Settings.Source != AudioSource.SystemAudio) CreateCapture(Enumerator, Settings.MicrophoneId, DataFlow.Capture, 0);
            if (Settings.Source != AudioSource.Microphone) CreateCapture(Enumerator, Settings.OutputId, DataFlow.Render, Captures.Count);
            lock (Gate)
            {
                SegmentFrame = PausedFrame = 0;
                AnchorQpc = QpcNow;
                CurrentState = RecordingState.Recording;
            }
            foreach (var Capture in Captures) Capture.StartRecording();
            Cancellation = new();
            Pump = Task.Run(() => PumpAsync(Cancellation.Token));
        }
        catch
        {
            lock (Gate) CurrentState = RecordingState.Finalizing;
            foreach (var Capture in Captures) await Capture.DisposeAsync();
            Captures.Clear();
            foreach (var Device in Devices) Device.Dispose();
            Devices.Clear();
            Journal?.Dispose();
            try { Journal?.DiscardIfEmpty(); } catch (Exception Error) when (Error is IOException or UnauthorizedAccessException) { AppPaths.Log(Error); }
            lock (Gate) CurrentState = RecordingState.Idle;
            throw;
        }
    }

    private void CreateCapture(MMDeviceEnumerator Enumerator, string? Id, DataFlow Flow, int Index)
    {
        var Device = string.IsNullOrEmpty(Id) ? Enumerator.GetDefaultAudioEndpoint(Flow, Role.Multimedia) : Enumerator.GetDevice(Id);
        Devices.Add(Device);
        var Builder = new WasapiRecorderBuilder().WithDevice(Device).WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)).WithBufferLength(40).WithMmcssThreadPriority("Audio");
        if (Flow == DataFlow.Render) Builder.WithLoopbackCapture();
        var Capture = Builder.Build();
        using var FormatClient = Device.CreateAudioClient();
        var Timeline = new CaptureTimeline(SampleRate, FormatClient.MixFormat.SampleRate);
        Capture.DataAvailable += (Buffer, Flags, DevicePosition, QpcPosition) =>
        {
            if (Buffer.IsEmpty) return;
            try
            {
                lock (Gate)
                {
                    if (CurrentState != RecordingState.Recording && !(CurrentState == RecordingState.Finalizing && AcceptFinalPackets)) return;
                    var PacketQpc = QpcPosition > 0 && (Flags & AudioClientBufferFlags.TimestampError) == 0 ? QpcPosition : QpcNow - Buffer.Length / 8 * 10_000_000L / SampleRate;
                    var Start = Timeline.Place(DevicePosition, PacketQpc, Buffer.Length / 8, SegmentFrame, AnchorQpc);
                    var Data = MemoryMarshal.Cast<byte, float>(Buffer);
                    var Skip = (int)Math.Min(Data.Length / 2, Math.Max(0, SegmentFrame - Start));
                    var Count = Data.Length / 2 - Skip;
                    if (CurrentState == RecordingState.Finalizing && !MicrophoneOnly) Count = (int)Math.Clamp(PausedFrame - Start - Skip, 0, Count);
                    Mixer!.Add(Index, Start + Skip, Data.Slice(Skip * 2, Count * 2));
                    CapturedFrames = Math.Max(CapturedFrames, Start + Skip + Count);
                }
            }
            catch (Exception Error) { ReportFault(Error); }
        };
        Capture.RecordingStopped += (_, Args) =>
        {
            if (Args.Exception is { } Error && State is RecordingState.Recording or RecordingState.Paused) ReportFault(Error);
        };
        Captures.Add(Capture);
    }

    private long CurrentFrames() => CurrentState == RecordingState.Recording
        ? MicrophoneOnly ? CapturedFrames : SegmentFrame + Math.Max(0, (QpcNow - AnchorQpc) * SampleRate / 10_000_000)
        : PausedFrame;

    public void TogglePause()
    {
        lock (Gate)
        {
            if (CurrentState == RecordingState.Recording)
            {
                PausedFrame = CurrentFrames();
                CurrentState = RecordingState.Paused;
                CurrentPeak = 0;
            }
            else if (CurrentState == RecordingState.Paused)
            {
                SegmentFrame = PausedFrame;
                AnchorQpc = QpcNow;
                CurrentState = RecordingState.Recording;
            }
        }
    }

    private async Task PumpAsync(CancellationToken Token)
    {
        var Buffer = new byte[SampleRate / 10 * 4];
        var LastFlush = Stopwatch.GetTimestamp();
        using var Timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await Timer.WaitForNextTickAsync(Token))
            {
                long Target;
                lock (Gate) Target = Math.Max(0, CurrentFrames() - SampleRate / 5);
                WriteUntil(Target, Buffer);
                if (Journal!.Length >= 3_800_000_000) throw new IOException("The WAV size limit was reached. Start a new recording to continue.");
                if (Stopwatch.GetElapsedTime(LastFlush).TotalSeconds >= 1) { Journal.Flush(); LastFlush = Stopwatch.GetTimestamp(); }
            }
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (Exception Error) { ReportFault(Error); }
    }

    private void WriteUntil(long Target, byte[] Buffer)
    {
        while (true)
        {
            int Count;
            lock (Gate)
            {
                if (Mixer!.WrittenFrames >= Target) return;
                Count = (int)Math.Min(Buffer.Length / 4, Target - Mixer.WrittenFrames) * 4;
                CurrentPeak = Mixer.Render(Buffer.AsSpan(0, Count));
            }
            Journal!.Write(Buffer.AsSpan(0, Count));
        }
    }

    private void ReportFault(Exception Error)
    {
        lock (Gate)
        {
            if (CaptureError != null) return;
            CaptureError = Error;
        }
        AppPaths.Log(Error);
        ThreadPool.QueueUserWorkItem(_ => Faulted?.Invoke(Error));
    }

    public async Task<string?> StopAsync()
    {
        lock (Gate)
        {
            if (CurrentState is RecordingState.Idle or RecordingState.Finalizing) return null;
            AcceptFinalPackets = CurrentState == RecordingState.Recording;
            PausedFrame = CurrentFrames();
            CurrentState = RecordingState.Finalizing;
        }
        try
        {
            foreach (var Capture in Captures) await Capture.DisposeAsync();
            lock (Gate) { if (MicrophoneOnly && AcceptFinalPackets) PausedFrame = CapturedFrames; }
            Cancellation?.Cancel();
            if (Pump != null) await Pump;
            WriteUntil(PausedFrame, new byte[SampleRate / 10 * 4]);
            return await Task.Run(() => Journal!.FinalizeRecording());
        }
        finally
        {
            Captures.Clear();
            foreach (var Device in Devices) Device.Dispose();
            Devices.Clear();
            Cancellation?.Dispose();
            Journal?.Dispose();
            lock (Gate) { CurrentState = RecordingState.Idle; CurrentPeak = 0; }
        }
    }
}
