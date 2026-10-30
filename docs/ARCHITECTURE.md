# Architecture and reliability

Three passes: foundation (audio and durable storage), build (WinUI product), polish (interaction, accessibility and release validation).

`Wavely.Core` owns WASAPI capture, timestamp mixing, session storage, playback, settings, and file operations. `Wavely.App` owns WinUI, dialogs, waveform rendering and a small Win32 shell adapter. There is no webview, remote API, account, analytics SDK or updater. No service or startup task is installed.

## Capture timeline

NAudio 3's non-obsolete WasapiRecorder captures shared-mode IEEE float stereo at 44.1 or 48 kHz. Windows converts each endpoint to that common format. QPC timestamps in 100 ns units establish each source's initial position and its position after resume. Subsequent packets append contiguously instead of being independently rounded from noisy timestamps. Device positions are interpreted in the endpoint's native sample rate; gaps over 2 ms are preserved, allowing for resampler startup and fractional-frame differences. Empty callbacks do not change clock state. A four-second bounded ring per input limits memory. A 20 ms pump commits audio 200 ms behind the clock, allowing normal packet scheduling jitter. Missing loopback packets become silence. Microphone-only elapsed time and writing follow captured frames rather than racing the device with the wall clock.

Both-source recording averages the two inputs, providing 6 dB of headroom per input. Finite checks and a final clamp prevent integer wrap/clipping in PCM conversion. Pausing freezes the saved frame position; resumed packets map to a new clock anchor. Finalization stops capture and drains the timeline. Independent device clock drift is not adaptively resampled in combined mode, so multi-hour synchronization still needs validation. Headphones prevent acoustic speaker bleed into the microphone.

The capture callback and mixer share a short lock. Disk writes and durable flushes run outside it, so slow storage cannot block capture callbacks. Late frames are counted and reported after saving; a ring overflow stops recording with an explicit error. There is no live monitoring path.

## Durability

Before capture starts, the destination is probed for write access and an atomic JSON manifest is flushed under `%LOCALAPPDATA%\Wavely\Sessions`. Audio is appended as 16-bit stereo raw PCM in that directory. A durable flush runs every second. No WAV header is relied upon while capturing. Normal stop flushes PCM, writes a temporary WAV, durably flushes it and renames it into the library. Only then are the manifest and raw PCM retired.

If capture, disk writing or output finalization fails, the raw session stays on disk. Startup recovery creates a frame-aligned WAV, ignoring an incomplete trailing sample frame. Failed recovery remains retryable; Settings can redirect recovery to the current save folder. Existing finalized outputs are checked for expected size before retiring a journal, allowing interrupted cleanup to complete. No recovery file is overwritten. A separate app-instance mutex prevents two Wavely windows from recording/recovering the same sessions.

The most recent second of data, plus about 200 ms of mixer latency and OS/device buffering, can be lost on abrupt process termination; power loss may also defeat drive caches. Recovery cannot restore audio the device never delivered or storage never committed. Finalization temporarily needs space for both raw PCM and the WAV. WAV sessions stop at 3.8 GB to remain below RIFF's size limit.

## Files and operations

WAV files are the library's source of truth. Settings retain previously used directories. Rename validates Windows filename restrictions. Trim always writes a new copy. Export writes to a unique temporary file through Windows Media Foundation and replaces the chosen destination only on success. Playback is disposed before recording and file mutations. Device changes apply between recordings, never halfway through one. Device loss ends the session with an error and saves the captured portion; automatic endpoint switching is deliberately disabled.

## UI and shell

Native WinUI controls supply keyboard focus, high contrast and accessibility peers. Controls have accessible names, the waveform is descriptive rather than interactive, and the seek slider provides keyboard navigation. Mica supplies its own system fallback. The timer uses tabular numerals. Recording animation respects the Windows animation preference; waveform movement represents real incoming audio. Idle updates run at 4 Hz, active updates at 20 Hz. The bounded waveform has 144 bars and no per-frame bitmap allocations.

The Win32 adapter owns a window-procedure delegate, hotkey registration, icon and optional tray icon. Hotkey conflicts are surfaced. Minimize can hide to tray; clicking the tray restores the window. Closing the app finalizes recording and exits. A second launch activates the first window. Settings and error logs are local; choosing a sync-enabled folder can cause the user's own sync software to upload files independently of Wavely.

## Primary references

The project explicitly adds its application PRI and compiled XAML to publish output. This works around the unpackaged publishing omission tracked in [WindowsAppSDK #6720](https://github.com/microsoft/WindowsAppSDK/issues/6720); a successful build alone does not validate a publish folder.

- [Windows App SDK stable channel](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [Self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [NAudio WasapiRecorder](https://github.com/naudio/NAudio/blob/main/Docs/WasapiRecorder.md)
- [NAudio WasapiPlayer](https://github.com/naudio/NAudio/blob/main/Docs/WasapiPlayer.md)
- [Media Foundation export](https://github.com/naudio/NAudio/blob/main/Docs/MediaFoundationEncoder.md)
