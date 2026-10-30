using NAudio.Wave;

namespace Wavely.Core;

public sealed class PlaybackService : IAsyncDisposable
{
    private WasapiPlayer? Player;
    private WaveFileReader? Reader;
    public bool IsPlaying => Player?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Duration => Reader?.TotalTime ?? TimeSpan.Zero;
    public TimeSpan Position => Reader?.CurrentTime ?? TimeSpan.Zero;
    public event Action<Exception>? Faulted;

    public async Task LoadAsync(string PathName, string? DeviceId)
    {
        await DisposeAsync();
        try
        {
            Reader = new(PathName);
            var Builder = new WasapiPlayerBuilder().WithLatency(80).WithMmcssThreadPriority("Audio");
            using var Enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var Device = string.IsNullOrEmpty(DeviceId) ? null : Enumerator.GetDevice(DeviceId);
            if (Device != null) Builder.WithDevice(Device);
            Player = Builder.Build();
            Player.PlaybackStopped += (_, Args) => { if (Args.Exception is { } Error) Faulted?.Invoke(Error); };
            Player.Init(Reader);
        }
        catch { await DisposeAsync(); throw; }
    }

    public void Toggle()
    {
        if (Player == null || Reader == null) return;
        if (IsPlaying) Player.Pause();
        else { if (Reader.Position >= Reader.Length) Reader.Position = 0; Player.Play(); }
    }

    public void Seek(TimeSpan Position)
    {
        if (Reader == null) return;
        var WasPlaying = IsPlaying;
        Player?.Stop();
        Reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(Position.TotalSeconds, 0, Reader.TotalTime.TotalSeconds));
        if (WasPlaying) Player?.Play();
    }

    public async ValueTask DisposeAsync()
    {
        if (Player != null) { await Player.DisposeAsync(); Player = null; }
        Reader?.Dispose();
        Reader = null;
    }
}
