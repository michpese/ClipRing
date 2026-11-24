# ClipRing – Engineering & Maintenance Guide

ClipRing is a Windows 10/11 (x64) instant-replay recorder built with .NET 10, WPF, NAudio (WASAPI), and an embedded GPL FFmpeg binary (`BtbN/FFmpeg-Builds`).

## Architecture Map

| Folder | Responsibility |
|---|---|
| `src/ClipRing/Core/` | Settings model (`AppSettings`), atomic JSON persistence (`SettingsStore`), paths (`AppPaths`), daily rolling file logger (`Log`), `ObservableObject`/`RelayCommand`, enums. |
| `src/ClipRing/Capture/` | FFmpeg discovery & embedded extraction (`FfmpegLocator`, `EmbeddedFfmpeg`), monitor & hardware encoder probing (`DisplayProbe`, `EncoderProbe`), FFmpeg command generation & caching (`PipelineBuilder`, `PipelineSelector`), long-running FFmpeg process & fMP4 stream reader (`ReplayRecorder`), ISO-BMFF box parser (`Mp4Parser`), pooled RAM ring buffer (`ReplayBuffer`, `SegmentedBuffer`/`BlockPool`), and `-c copy` faststart remuxing (`ClipSaver`). |
| `src/ClipRing/Audio/` | Clock-driven 48 kHz float32 mixer (`AudioEngine`) with WASAPI loopback + microphone (`CaptureSource`), speaker-position surround downmix (`DownmixToStereoSampleProvider`), and device enumeration (`AudioDevices`). |
| `src/ClipRing/Games/` | Event-driven (`SetWinEventHook`) + polling foreground game detector (`GameDetector`), launcher manifest/registry scanner (`GameLibrary` for Steam/Epic/GOG), and embedded executable list (`known_games.json`). |
| `src/ClipRing/Highlights/` | Local `127.0.0.1` HTTP/1.1 Counter-Strike 2 Game State Integration server (`Cs2GameStateServer`, `Cs2GsiInstaller`) and round-based kill aggregator (`HighlightManager`). |
| `src/ClipRing/Hotkeys/` | Global hotkeys via `RegisterHotKey` on an `HWND_MESSAGE` window (`HotkeyManager`, `HotkeyBinding`). |
| `src/ClipRing/Localization/` | Embedded `de.json` and `en.json` string tables, live-updating `{l:T Key}` XAML markup extension, and `Loc.T`/`Loc.F` helpers. |
| `src/ClipRing/Native/` | Win32 P/Invoke (`Win32`) and `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` wrapper (`ChildProcessJob`) so FFmpeg never outlives ClipRing. |
| `src/ClipRing/Services/` | Main orchestrator (`ReplayService`) and Windows shell/autostart/sound helpers (`SystemIntegration`). |
| `src/ClipRing/UI/` | `MainViewModel`, Fluent dark theme (`Theme.xaml`), in-place `LiveOptions<T>`, custom controls (`SettingRow`, `HotkeyBox`, `BufferTape`, `LevelMeter`), value converters, and WinForms `NotifyIcon` (`TrayIcon`). |
| `tests/ClipRing.Tests/` | xUnit unit tests covering MP4 parsing, replay buffer, CS2 GSI/highlights, filenames, hotkeys, surround downmix, pipeline building, and localization/data integrity. |

## Build, Test, and CI Commands

### On Windows (native)
```powershell
.\build.ps1                           # Release single-file build with embedded FFmpeg -> dist\ClipRing\
.\build.ps1 -SelfContained            # Release build including .NET runtime (matches GitHub Releases)
.\build.ps1 -SkipFfmpeg               # Fast local build without embedding FFmpeg
dotnet test tests/ClipRing.Tests -c Release
dist\ClipRing\ClipRing.exe --selftest-ui report-ui.txt
dist\ClipRing\ClipRing.exe --selftest-ffmpeg report-ff.txt
```

### On Linux (cross-compile verification)
```bash
dotnet build ClipRing.sln -c Release -p:EnableWindowsTargeting=true
```
Note: `ClipRing.csproj` and `ClipRing.Tests.csproj` target `net10.0-windows` (`UseWPF=true`), so `dotnet build` compiles both projects and runs all C#/XAML analyzers on Linux, while executing `dotnet test` and the `--selftest-*` binaries requires Windows (run automatically on every push/PR via `.github/workflows/build.yml` on `windows-latest`).

## Non-Negotiable Design Invariants

1. **Anti-cheat safety (VAC / FACEIT):**
   - Never inject DLLs into games, install low-level keyboard/mouse hooks (`WH_KEYBOARD_LL`), read game process memory, or send inputs.
   - Foreground tracking uses `WINEVENT_OUTOFCONTEXT` (`SetWinEventHook`), process liveness uses `PROCESS_QUERY_LIMITED_INFORMATION`, hotkeys use `RegisterHotKey`, capture uses WGC (`gfxcapture`) / DXGI (`ddagrab`), and CS2 highlights use official Valve GSI on `127.0.0.1`.
2. **A/V synchronization model:**
   - `AudioEngine` streams gap-free wall-clock-paced 48 kHz float32 PCM (`stereo` or 6-channel `5.1` for separate tracks) to FFmpeg `stdin` (`pipe:0`).
   - Video uses `-use_wallclock_as_timestamps 1` shifted by `-itsoffset -audioStartUnix` with `-copyts -fps_mode passthrough`. Do not put `-itsoffset` on the raw PCM input (stalls FFmpeg 7+).
3. **Thread safety & UI marshalling:**
   - `LiveOptions<T>` and `ObservableCollection<T>` (`RecentClips`, option lists) must only be mutated on the WPF UI `Dispatcher`. Use `LiveOptions.Update(...)` in place so `ComboBox` selections survive label refreshes and language switches.
   - `ReplayService` serializes environment detection, redetection, and recorder restarts with `_life` (`SemaphoreSlim(1, 1)`) and cancels in-flight probes on shutdown via `_disposeCts`.
   - `SettingsStore.Save` reads `AppSettings` from a timer thread; replace collection properties (copy-on-write) rather than mutating dictionaries/lists in place.
   - Never invoke external events (`SyncLost`, etc.) while holding internal audio/buffer locks.
4. **Localization parity:**
   - Every UI string must exist in both `src/ClipRing/Localization/de.json` and `en.json` with identical keys (kept alphabetically sorted) and matching format placeholders (`{0}`, `{1}`, ...).
   - Code comments are in English; runtime log messages (`Log.Info`/`Warn`/`Error`) are in German; user-facing UI text uses `Loc`.
5. **GPL compliance for bundled FFmpeg:**
   - `build.ps1` verifies the BtbN archive against `checksums.sha256` and generates `FFMPEG-SOURCE.txt` alongside `FFMPEG-LICENSE.txt`. The release workflow appends `FFMPEG-SOURCE.txt` to the GitHub Release notes. Keep this intact.
6. **Pipeline cache invalidation:**
   - If you change candidate pipelines or filter chains in `PipelineBuilder.Candidates`, bump `PipelineSelector.CandidateVersion` so existing users re-probe instead of reusing stale cached IDs.

## Release Checklist

1. Bump `<Version>` in `src/ClipRing/ClipRing.csproj` (`StampManifestVersion` updates `app.manifest` automatically from `$(AssemblyVersion)`).
2. Add `.github/release-notes/vX.Y.Z.md` (English only; the CI release job appends the FFmpeg source section).
3. Verify CI passes on `main` (build, 0 warnings, unit tests, `--selftest-ui`, `--selftest-ffmpeg`).
4. Push tag `vX.Y.Z` to trigger the self-contained build, release self-tests, ZIP packaging, and GitHub Release creation.
