# Validation record

## 1.0.1 microphone crackle correction

The user's microphone WAVs contained short runs of exact-zero samples between nonzero, same-polarity samples, recurring at capture-packet boundaries. Independently rounding each packet's QPC timestamp could leave holes or overwrite samples. A regression fixture reproduced 100 zero frames and a maximum 16223-unit step in a one-second continuous signal before the fix. Anchoring each segment once, then preserving contiguous packet samples, removes all zero holes in that fixture and keeps the maximum step below 250.

The revised suite passed 13 checks, adding timestamp-jitter continuity and native-rate/empty-packet/gap/pause clock checks to the original coverage. Real-device tests still passed microphone, loopback, combined capture, pause/resume, playback, seek and device switching. Two additional six-second captures of the user's selected fifine microphone at 48 kHz and 44.1 kHz reported zero late frames. Waveform analysis found zero short interior zero holes with same-polarity neighbors above 100 PCM units, compared with many such holes in the original microphone recordings. This is a signal-integrity check, not a claim of subjective listening verification or elimination of microphone/environment noise.

The callback no longer performs or waits on disk writes or durable flushes. Microphone-only writing follows captured sample frames. Empty WASAPI callbacks are ignored, native endpoint sample rates are accounted for, and invalid QPC timestamps fall back to callback timing for the initial anchor.

Date: 30 October 2026. Tested on the available Windows 11 x64 workstation with .NET SDK 10.0.403, Windows App SDK 2.6.4 and NAudio 3.3.0.

## Automated tests actually run

`dotnet run --project tests/Wavely.Tests -c Release -- --devices` completed with **11 checks passing**:

1. Timestamp alignment, silent gaps, mixing headroom, nonfinite input sanitation and late-frame accounting.
2. Terminated child process, durable journal recovery, invalid trailing frame truncation and exact recovered WAV duration.
3. Non-destructive trim, Windows filename rejection, library scanning and audio peak extraction.
4. Windows Media Foundation MP3 and FLAC encoding, followed by decoding and duration checks.
5. Rejection of an invalid output directory without starting capture.
6. Real default microphone recording, frozen pause timeline, resume, final WAV integrity, playback and seek.
7. Real system loopback recording with a generated 440 Hz tone, pause/resume, duration, playback and seek.
8. Real simultaneous microphone + loopback recording with the same checks.
9. Repeated capture sessions selecting all three available microphones at 44.1 kHz.
10. Unavailable endpoint rejection and return to idle.
11. Forced destination failure during finalization, intact PCM journal and successful recovery to another directory.

The tested capture endpoints were Web Camera, fifine Microphone and Mpow HC. Render endpoints available were NVIDIA G2721P, fifine speakers and Mpow HC speakers; loopback/playback used the default output. Short 48 kHz sessions measured roughly 2.1 seconds of saved audio after excluding a 400 ms pause, with zero late frames in these runs. Loopback captured the test tone at peak 0.200; both-source mode preserved mixed audio at reduced gain.

One terminated-process test initially encountered a brief file lock immediately after process exit. It now retries opening its fixture for up to two seconds; recovery and frame alignment passed on subsequent runs. This retry is in the test harness, not a claim that Windows always releases handles immediately.

## UI inspection actually performed

The native application was launched and inspected using accessibility trees and screenshots. Verified: empty state, source selector, library row after saving, live waveform, pause button changing to Resume, stop/save behavior, selected recording waveform, native Settings layout, scrollable device/quality/theme/hotkey controls, light and dark appearance, Ctrl+R recording, and the registered Ctrl+Shift+R global stop route. Inspection identified and prompted fixes for record hover colors, caption button contrast, keyboard focus and Space routing. After fixing keyboard routing with PreviewKeyDown, the published executable was used to verify Space pause, frozen elapsed time, Space resume and Ctrl+R stop/save.

## Published application

The first published-folder launch failed because Windows App SDK omitted the application PRI and XBF resources. The project now explicitly includes these files in publish output, and the packaging script checks for them. The corrected self-contained published executable launched successfully and was used for the final keyboard/recording inspection. This is a smoke test on the development workstation; a clean-machine test remains outstanding.

## Remaining manual coverage

Physical USB unplug/replug during capture, permission-denied microphone access, Windows sleep/resume, device driver restarts, sustained disk-full conditions, actual power failure, multi-hour drift/stability, multiple DPI scales, Narrator and high-contrast auditing, Windows N/KN codec availability, clean-machine deployment without any developer tools, tray behavior across Explorer restarts, and alternate architectures have not been fully verified. The x64 release is unsigned. A missing endpoint and a forced finalization failure are tested; they do not substitute for physical device-loss and power-loss testing.

The tests generate temporary recordings under `%TEMP%\Wavely.Tests`. Hardware tests play a short tone and capture available microphones; they should be run in an appropriate environment. The test suite does not disable devices or change Windows permission/security settings.
