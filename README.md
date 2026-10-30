# Wavely

A small, native Windows 11 sound recorder. C# / .NET 10, WinUI 3, Windows App SDK and NAudio. Offline, no accounts, no telemetry, no browser wrapper. MIT licensed.

[Download the latest Windows release](https://github.com/bethanian/Wavely/releases/latest) · [Source code](https://github.com/bethanian/Wavely)

## Run

Extract the **entire** `Wavely-1.0.1-win-x64.zip` into a writable local folder and open **Wavely.exe**. Keep its accompanying DLLs and runtime files together. The x64 portable release includes .NET and the Windows App SDK; no separate runtime installation is required. Windows 11 build 22000 or later, x64. The release is unsigned, so Windows may identify it as an unrecognized publisher.

Choose Microphone, System audio, or Microphone + system audio, then press the red record button. Pause/resume with the adjacent control. Press stop to save a WAV. Select a library item to play or seek. The `…` menu provides rename, trim a copy, MP3/FLAC export, copy path, open location and delete.

Settings provides endpoint selection, 44.1/48 kHz WAV recording, MP3 quality, save folder, light/dark/system appearance, optional tray behavior and configurable global hotkeys. Default recordings go to `%USERPROFILE%\Music\Wavely`. Settings, recovery journals and local diagnostic errors live in `%LOCALAPPDATA%\Wavely`. Previously used recording folders remain in the library. Delete is permanent and asks for confirmation.

| Shortcut | Action |
| --- | --- |
| Ctrl+R | Start / stop recording |
| Space | Pause / resume recording, or play / pause selected audio |
| F2 | Rename selected recording |
| Ctrl+Shift+R | Global start / stop, configurable in Settings |
| Tab / arrow keys | Native control and seek navigation |

Minimize-to-tray is optional. Click the tray icon to restore. Closing always saves the current recording and exits. The default audio device is resolved when each session starts; changes take effect on the next recording. System audio means the selected output endpoint, including all applications playing through it.

## Build from source

Install the **.NET SDK 10.0.403 x64** on Windows 11. `scripts/Install-Sdk.ps1` installs that version locally under `%LOCALAPPDATA%\Wavely\dotnet`, without changing PATH. NuGet downloads require internet access during restore; the app itself has no network dependencies. WinUI's XAML build tools are supplied by NuGet. Visual Studio is optional for the C# command-line build verified here.

```powershell
./scripts/Install-Sdk.ps1       # if the SDK is missing
./scripts/Build.ps1 -Test       # Release build + deterministic/core/codec tests
./scripts/Build.ps1 -Devices    # also records real endpoints and plays a short test tone
./scripts/Build.ps1 -Package    # self-contained x64 folder + ZIP + SHA-256
```

Or, with the pinned SDK on PATH:

```powershell
dotnet build src/Wavely.App -c Release -p:Platform=x64
dotnet run --project tests/Wavely.Tests -c Release
dotnet publish src/Wavely.App -c Release -p:Platform=x64 --self-contained true -o artifacts/Wavely
```

Artifacts appear under `artifacts/`. The portable distribution is a folder-based executable, not a single-file EXE or MSIX. The repository uses a single NuGet source and pinned direct dependency versions. Runtime package lockfiles are committed for repeatable restore.

## Recovery and practical limits

Wavely journals PCM audio locally and flushes it every second. On next launch, interrupted sessions are recovered automatically. If their original destination is unavailable, choose a writable folder in Settings, save it, then use **Recover recordings to current save folder**. Journals remain intact when recovery fails. Never delete `%LOCALAPPDATA%\Wavely\Sessions` when you need an interrupted recording.

- Abrupt termination can lose roughly the last second plus device/mixer buffering. Recovery cannot reconstruct audio that never reached disk.
- Recording stays bound to its chosen devices. A disconnected device stops the session and reports an error. It does not silently switch microphones.
- Both-source mode uses equal 50% gains to preserve headroom. Use headphones to avoid acoustic speaker bleed. This is a utility, not studio-grade multi-device clock synchronization.
- Sessions stop near 3.8 GB (about 5.5 hours at 48 kHz stereo). Enough space is needed for the journal and final WAV during saving.
- MP3/FLAC use built-in Windows Media Foundation encoders. Windows N/KN installations may need Microsoft's Media Feature Pack; encoder failure leaves the original WAV untouched.
- No audio monitoring, effects, transcription, background service, autostart, automatic updater, or cloud integration. Folder sync is controlled by your own Windows/sync configuration.
- x64 is the verified distribution target. ARM64/x86 builds and signing/MSIX deployment are not supplied.

See [architecture](docs/ARCHITECTURE.md), [validation](docs/VALIDATION.md) and [third-party notices](THIRD-PARTY-NOTICES.md). Hardware unplug, denied microphone permission, power-loss and extended multi-hour stability require further manual validation; do not interpret the included tests as certification for every device or driver.
