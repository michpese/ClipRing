# ClipRing

[![Build](https://github.com/michpese/ClipRing/actions/workflows/build.yml/badge.svg)](https://github.com/michpese/ClipRing/actions/workflows/build.yml)

**English** · [Deutsch](README.de.md)

**Save your best plays with one key press.** ClipRing runs quietly in the background and always keeps the last minutes of your game ready. Press the hotkey and that moment is saved as a video.

It works like **NVIDIA ShadowPlay (Instant Replay)** or **AMD ReLive**, but it isn't tied to one graphics card brand and it's open source. ClipRing is made for **competitive games**: in Counter-Strike 2 it saves your multi-kills automatically.

> **Early version (0.1).** ClipRing works, but so far it has mainly been tested on one PC with an NVIDIA graphics card and Counter-Strike 2. If something doesn't work on your system, please open an [issue](https://github.com/michpese/ClipRing/issues) and attach the log (see [Tips](#tips)).

## What it does

- **Instant replay:** keeps the last 15 seconds to 10 minutes. One hotkey saves everything, another saves only the last seconds.
- **Barely costs FPS:** recording runs on your graphics card's video encoder (NVIDIA, AMD or Intel), not on the CPU.
- **Sharp and smooth:** up to 4K and 240 fps, as H.264, HEVC or AV1. Options your PC can't handle are greyed out.
- **Automatic CS2 highlights:** 2K, 3K, 4K and aces are saved on their own. By default only in Competitive and Premier, so deathmatch doesn't flood your folder.
- **Tidy clips:** every game gets its own folder, and files are named after the game, date and time.
- **Game and microphone audio,** each with its own volume.
- **Stays out of the way:** sits in the notification area, can start with Windows, and the hotkeys also work in fullscreen games.
- **English and German.**
- **One file:** no installation, nothing else to download.

## Getting started

1. Download the latest `ClipRing-…-win-x64.zip` from [Releases](https://github.com/michpese/ClipRing/releases) and unzip it anywhere.
2. Start `ClipRing.exe`. The program isn't signed yet, so Windows may show "Windows protected your PC". Click **More info → Run anyway**.
3. On the first start, ClipRing checks your graphics card and monitors. This takes a few seconds.
4. Play. Then press:

   | Keys | What happens |
   |---|---|
   | **Ctrl+Alt+F10** | saves the last 2 minutes |
   | **Ctrl+Alt+F9** | saves the last 30 seconds |
   | **Ctrl+Alt+F11** | pauses or resumes recording |

   You can change all keys and lengths in the app.
5. Your clips are in `Videos\ClipRing\<game>`.

The language follows Windows. You can change it under **General → Language**.

## Automatic highlights in Counter-Strike 2

1. In ClipRing, open **Highlights** and click **Set up**.
2. Restart CS2. The status changes to "Connected".
3. Choose how many kills make a clip and in which modes.

Kills in quick succession end up in one clip, named for example `…_3K.mp4` or `…_Ace.mp4`.

## Is it safe with VAC and FACEIT?

ClipRing doesn't touch the game. It doesn't inject anything into CS2, doesn't read its memory and sends no inputs. It records the screen with the same Windows functions that OBS and Discord use. CS2 highlights come through Valve's official Game State Integration, which exists precisely for tools like this.

Only Valve and FACEIT can make guarantees, but ClipRing does nothing that anti-cheat systems look for. You **don't** need the launch option `-allow_third_party_software`, so leave it out.

## What you need

- Windows 10 or 11 (64-bit)
- An NVIDIA, AMD or Intel graphics card or integrated graphics. Without a video encoder, ClipRing records with the CPU, which is noticeably slower.
- AV1 needs a newer graphics card: NVIDIA RTX 40/50, AMD Radeon RX 7000/9000 (or a matching APU) or Intel Arc. ClipRing detects this itself.
- Enough memory: the app shows how much your settings need. With the default 2 minutes that is usually a few hundred MB to about 1.5 GB.

## Tips

- **Clips stutter or have fewer frames than set:** set **Video → Encoder speed** to "Fast" and close other recording programs.
- **The recording flickers or shows old pictures:** update your graphics driver. If that doesn't help, try **Video → Capture method → Game window**.
- **A hotkey doesn't work:** another program is probably using the same keys. Pick a different combination.
- **Something else is wrong:** **General → Open log folder** contains a log that helps find the cause. Please attach it to an [issue](https://github.com/michpese/ClipRing/issues).

## How it works, in short

ClipRing continuously records your screen with your graphics card and keeps the last minutes in memory instead of writing to your SSD all the time. When you press the hotkey, it saves exactly that part as an MP4, without re-encoding, so it's quick and the quality stays the same. Recording and encoding are done by [FFmpeg](https://ffmpeg.org), which is built into ClipRing.

Known limits: game audio and microphone end up on one audio track, HDR is recorded as SDR, and copy-protected video shows up black.

## For developers

You need Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`). Then, in the project folder:

```powershell
.\build.ps1                 # builds dist\ClipRing\ClipRing.exe with FFmpeg built in
.\build.ps1 -SelfContained  # also includes .NET, runs on any Windows PC (like the releases)
.\build.ps1 -UpdateFfmpeg   # fetch the newest FFmpeg
```

If PowerShell blocks the script: `powershell -ExecutionPolicy Bypass -File .\build.ps1`.

- Every push is built on GitHub (Windows). The build also starts the app, opens every page in both languages and checks the built-in FFmpeg.
- A tag such as `v0.2.0` automatically creates a release.
- Texts live in `src/ClipRing/Localization/de.json` and `en.json`.
- Code overview: `Capture/` (screen capture, encoders, memory buffer, saving), `Audio/`, `Games/` (game detection), `Highlights/` (CS2), `UI/`, `Services/`.

## License

ClipRing is licensed under the [GNU General Public License v3.0](LICENSE). The built-in FFmpeg is also GPL and is included unchanged (build by [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)). Every download contains its license (`FFMPEG-LICENSE.txt`), and the release notes link to the exact source code of the included version (from 0.1.1 on also as `FFMPEG-SOURCE.txt` in the download). [NAudio](https://github.com/naudio/NAudio) is MIT-licensed.
