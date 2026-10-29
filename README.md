# ClipRing

[![Build](https://github.com/michpese/ClipRing/actions/workflows/build.yml/badge.svg)](https://github.com/michpese/ClipRing/actions/workflows/build.yml)

Instant-Replay für Windows im Stil von ShadowPlay, AMD ReLive und dem OBS-Replay-Puffer. ClipRing nimmt im Hintergrund ständig die letzten Minuten in den **Arbeitsspeicher** auf. Ein Hotkey schreibt sie als MP4 auf die Platte, sortiert nach Spiel. Funktioniert mit NVIDIA-, AMD- und Intel-Grafik und nutzt deren Hardware-Encoder.

> **Status: frühe Entwicklungsversion (v0.1).** Die Kernlogik (Aufnahme, RAM-Puffer, A/V-Sync, Clip-Zusammenbau) wurde gegen echtes FFmpeg getestet, die App wird bei jedem Push automatisch gebaut. Auf echter Spiele-Hardware ist sie noch kaum erprobt. Fehler bitte als [Issue](https://github.com/michpese/ClipRing/issues) melden und das Protokoll anhängen (siehe [Dateien](#dateien)).

## Download

Fertige Versionen liegen unter [Releases](https://github.com/michpese/ClipRing/releases): Zip entpacken, `ClipRing.exe` starten. Die .NET-Runtime und FFmpeg sind in der einen Datei enthalten, ein separates Programm ist nicht nötig. Den aktuellen Entwicklungsstand gibt es bei jedem erfolgreichen [Build](https://github.com/michpese/ClipRing/actions/workflows/build.yml) als Artefakt (FFmpeg eingebaut, braucht die .NET 10 Desktop Runtime).

## Funktionen

| Anforderung | Umsetzung |
|---|---|
| Zwischenspeicher im RAM | Fragmentiertes MP4 (ein Fragment je Keyframe) in einem Ringpuffer aus wiederverwendeten 256-KB-Blöcken. Kein Schreiben auf die SSD, bis du speicherst. |
| Moderne Codecs | H.264, HEVC/H.265, AV1 |
| Hardware-Encoder, GPU-unabhängig | NVIDIA NVENC, AMD AMF, Intel Quick Sync, Direct3D 12 (herstellerunabhängig), dazu Software (x264/x265/SVT-AV1). „Automatisch“ testet beim Start, was auf deinem PC wirklich funktioniert. |
| Einstellbarer Zeitraum | 15 s bis 10 min im Regler (bis 20 min über `settings.json`), mit RAM-Schätzung |
| Bitrate und Voreinstellungen | Niedrig/Mittel/Hoch/Sehr hoch (skaliert mit Auflösung, Bildrate und Codec) oder eigene Bitrate; VBR oder CBR; Encoder-Tempo |
| FPS | 30 bis 240 |
| Auflösung | Nativ, 2160p, 1440p, 1080p, 720p (nur Verkleinern, Seitenverhältnis bleibt) |
| Ausgabeordner | frei wählbar |
| Spielerkennung + Unterordner | Vordergrundfenster → eingebaute Liste, Steam-/Epic-/GOG-Bibliotheken, Vollbild-Erkennung, eigene Namen und Ausnahmen |
| Dateiname mit Zeitstempel und Spiel | Vorlage, Standard `{game}_{date}_{time}`, z. B. `Counter-Strike 2_2026-10-20_21-14-03.mp4` |
| Läuft im Hintergrund | Symbol im Infobereich, Autostart mit Windows, Schließen minimiert |
| Hotkeys einstellbar | Replay speichern, kurzen Clip speichern, Pausieren; global per `RegisterHotKey`, auch im Spiel |
| Mikrofon einstellbar | an/aus, Gerät, Lautstärke |
| PC- und Mikrofon-Lautstärke | getrennte Regler (0–200 %) mit Pegelanzeige, wirken sofort |
| Highlights CS2 | Valves offizielle Game State Integration: Kills, Multi-Kills, Ace; VAC-sicher |
| Moderne UI | WPF mit Fluent-Dark-Theme (.NET 10), eigene Navigation, Puffer-Anzeige |

## Voraussetzungen

- Windows 10 (2004) oder Windows 11, 64 Bit
- **Zum Bauen:** [.NET 10 SDK](https://dotnet.microsoft.com/download) – `winget install Microsoft.DotNet.SDK.10`
- **Zum Ausführen:** .NET 10 Desktop Runtime (entfällt mit `-SelfContained`)
- **FFmpeg** ist in `ClipRing.exe` eingebaut. Das Build-Skript lädt dafür einmal einen Windows-Build (BtbN, GPL, Version 8.1 oder neuer) nach `.cache\ffmpeg`.
- Aktueller Grafiktreiber (für NVENC/AMF/QSV)

## Bauen

```powershell
# einmalig
winget install Microsoft.DotNet.SDK.10

# im Projektordner
.\build.ps1                 # Release nach dist\ClipRing\ClipRing.exe, FFmpeg eingebaut
.\build.ps1 -SelfContained  # bringt zusätzlich die .NET-Runtime mit, läuft auf jedem Windows-PC
.\build.ps1 -UpdateFfmpeg   # neueste FFmpeg-Version laden statt der zwischengespeicherten
.\build.ps1 -SkipFfmpeg     # ohne eingebautes FFmpeg (eigene ffmpeg.exe verwenden)
```

Falls PowerShell Skripte blockiert: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Danach `dist\ClipRing\ClipRing.exe` starten. Neben der exe liegt nur noch `FFMPEG-LICENSE.txt`.

**Wie FFmpeg eingebaut ist:** Das Skript komprimiert `ffmpeg.exe` und bettet es als Ressource in `ClipRing.exe` ein, zusammen mit seiner SHA-256-Prüfsumme. Beim ersten Start entpackt ClipRing es nach `%LOCALAPPDATA%\ClipRing\ffmpeg\` und startet es von dort als Hintergrundprozess; bei jedem Start wird die Prüfsumme kontrolliert und eine beschädigte oder veränderte Datei ersetzt. Alte Versionen werden aufgeräumt.

**Ohne Skript / zum Entwickeln:**

```powershell
dotnet build src\ClipRing\ClipRing.csproj
dotnet run --project src\ClipRing
```

`ClipRing.sln` öffnet sich in Visual Studio 2022/2026 oder Rider. Ohne `build.ps1` ist kein FFmpeg eingebaut: für `dotnet run` muss `ffmpeg.exe` im PATH liegen (z. B. `winget install Gyan.FFmpeg`) oder in den Einstellungen unter „Allgemein“ gewählt werden. Alternativ einmal `.\build.ps1` ausführen und `dotnet build` die gleichen Eigenschaften mitgeben: `-p:EmbedFfmpeg=<Pfad>\.cache\ffmpeg\ffmpeg.exe.gz`.

**Anderes FFmpeg:** Eine eigene `ffmpeg.exe` (ab 8.1) unter „Allgemein → Eigener Pfad“ wählen; sie hat Vorrang vor dem eingebauten.

## Erste Schritte

1. ClipRing starten. Beim ersten Start entpackt es das eingebaute FFmpeg, erkennt die Monitore und testet die Encoder (dauert ein paar Sekunden, Status unter „Übersicht“).
2. Spielen. Mit **Strg+Alt+F10** werden die letzten 2 Minuten gespeichert, mit **Strg+Alt+F9** die letzten 30 Sekunden. **Strg+Alt+F11** pausiert. (Die Tasten lassen sich unter „Hotkeys“ ändern. Alt+F9/F10 sind bewusst nicht belegt, weil die NVIDIA App sie nutzt.)
3. Clips landen in `Videos\ClipRing\<Spiel>\`.

## So funktioniert es

```
gfxcapture / ddagrab (GPU)
   └─ [GPU-Skalierung oder CPU-Konvertierung]
        └─ Encoder (NVENC / AMF / QSV / D3D12 / Software)
WASAPI PC-Ton ┐
WASAPI Mikro  ┴─ Mixer (Lautstärke, 48 kHz) ─ stdin ─┘
                                                     └─ fragmentiertes MP4 ─ stdout ─▶ RAM-Ringpuffer
Hotkey ─▶ Schnappschuss der letzten N s ─▶ ffmpeg -c copy (ohne Neukodierung) ─▶ MP4 im Spielordner
```

- **RAM-Puffer:** FFmpeg schreibt ein Fragment pro Keyframe (Standard: jede Sekunde). ClipRing hält davon so viele, wie der Zeitraum verlangt, und gibt ältere Blöcke zur Wiederverwendung frei. Beim Speichern wird nur kopiert, nicht neu kodiert; die Aufnahme läuft dabei weiter.
- **Synchronität:** Video bekommt beim Erfassen die Systemzeit als Zeitstempel, der Ton ist ein lückenloser Strom, dessen erste Probe ebenfalls an der Systemzeit verankert ist. Im Test mit FFmpeg (Testquelle anstelle von `ddagrab`) lag der Versatz unter einer Millisekunde. Falls es auf deinem System doch abweicht, gibt es unter „Audio“ einen Ton-Versatz.
- **Bildschirmaufnahme:** Über Windows Graphics Capture (`gfxcapture`, FFmpeg 8.1+, Standard) oder Desktop Duplication (`ddagrab`). Beide liefern nur dann ein Bild, wenn Windows ein neues zusammengesetzt hat, und werden mit deutlich mehr als der Ziel-Bildrate betrieben: Graphics Capture verwirft jedes Bild, das früher als die eingestellte Mindestpause kommt, und `ddagrab` legt sich zwischen zwei Bildern mit `Sleep()` schlafen, das Windows für FFmpeg auf 15,6 ms rundet (bei 120 fps kamen so nur ~80 an). Mit `dup_frames=0` und einer Rate über der doppelten Bildwiederholrate schläft `ddagrab` nie und wartet stattdessen auf das nächste Bild. Ein `select`-Filter behält danach das erste Bild jedes 1/fps-Zeitfensters (ohne Duplikate, stehende Bilder erzeugen keine Lücke voller Kopien); die Zeitbasis 1/fps sagt dem Encoder die Bildrate für die Bitratenregelung. Mehr Bilder, als der Monitor Hz hat, gibt es mit keiner der beiden Methoden.
- **Spielfenster-Aufnahme:** Als Aufnahmemethode „Spielfenster“ nimmt ClipRing per Graphics Capture nur das Fenster des erkannten Spiels auf (`gfxcapture=hwnd=…`, auf Monitorgröße skaliert). Die Bilder kommen dann direkt aus der Bildausgabe des Spiels statt aus dem zusammengesetzten Desktop. Das hilft bei Spielen, die per Independent Flip oder Overlay-Ebene (MPO) direkt ausgegeben werden: dort kann der zusammengesetzte Desktop noch ein altes Bild des Fensters enthalten (in CS2 flackerte die Bildschirmaufnahme zum Ladebildschirm zurück). Lässt sich das Fenster nicht aufnehmen, nimmt ClipRing den Monitor auf.
- **Encoder-Auswahl:** Für jeden Kandidaten läuft ein kurzer Testlauf. Bevorzugt werden Wege, bei denen das Bild die Grafikkarte nicht verlässt (NVENC/AMF direkt, `scale_d3d11`, `vpp_amf`, `vpp_qsv`). Klappt das nicht (z. B. Laptop mit zwei GPUs), geht es über die CPU-Konvertierung. Das Ergebnis wird zwischengespeichert.
- **Clip-Schnitt:** Clips beginnen auf einem Keyframe. Gespeichert werden mindestens N Sekunden vor dem Tastendruck bis zum Ende des Puffers, ClipRing wartet dafür kurz, bis der Encoder den Moment des Tastendrucks ausgegeben hat.

## Highlights einrichten

**Counter-Strike 2**

1. Unter „Highlights“ auf **Einrichten** klicken. Das legt `gamestate_integration_cliprings.cfg` in `…\Counter-Strike Global Offensive\game\csgo\cfg\` ab.
2. CS2 neu starten. Der Status zeigt „Verbunden“, sobald Daten kommen.
3. Wählen, ab wie vielen Kills ein Clip entsteht und in welchen Spielmodi (Standard: nur Wettkampf und Premier). Kills kurz hintereinander werden zusammengefasst (Dateiname z. B. `…_3K.mp4`, `…_Ace.mp4`). Automatische Highlights spielen keinen Signalton.

## Dateien

| Was | Wo |
|---|---|
| Einstellungen | `%APPDATA%\ClipRing\settings.json` |
| Protokoll (mit allen FFmpeg-Befehlen) | `%APPDATA%\ClipRing\logs\` |
| Entpacktes FFmpeg | `%LOCALAPPDATA%\ClipRing\ffmpeg\` |
| Clips | `%USERPROFILE%\Videos\ClipRing\` (einstellbar) |

## Entwicklung

- **Automatischer Build:** [`.github/workflows/build.yml`](.github/workflows/build.yml) baut jeden Push auf `main` und jeden Pull Request auf einem Windows-Runner von GitHub und stellt das Ergebnis als Artefakt bereit.
- **Release:** Ein Tag wie `v0.2.0` erzeugt automatisch ein GitHub-Release mit fertigem Zip (`ClipRing.exe` inklusive .NET-Runtime und FFmpeg).

  ```powershell
  git tag v0.2.0
  git push origin v0.2.0
  ```
- **SDK-Version:** [`global.json`](global.json) legt .NET 10 fest (neuere 10.x-Versionen werden automatisch verwendet).

## Projektstruktur

```
src/ClipRing/
  Capture/     eingebautes FFmpeg, FFmpeg-Suche, Monitor-Erkennung, Encoder-Pipelines und -Test, MP4-Parser, RAM-Puffer, Recorder, Clip-Speicherung
  Audio/       WASAPI-Loopback und Mikrofon, Mixer mit Lautstärke und Uhr-Verankerung
  Games/       Spielerkennung (Vordergrund, Bibliotheken, Vollbild), Liste bekannter Spiele
  Highlights/  CS2-GSI-Empfänger und -Installer, Zusammenfassung zu Clips
  Hotkeys/     globale Hotkeys
  Services/    ReplayService (steuert alles), Autostart, Signaltöne
  UI/          Theme, ViewModel, eigene Steuerelemente (Puffer-Band, Pegel, Hotkey-Feld), Infobereich
```

## Bekannte Grenzen in v0.1

- Eine gemeinsame Tonspur (PC + Mikrofon gemischt), noch keine getrennten Spuren.
- Wechselt ein Spiel im exklusiven Vollbild die Auflösung, startet die Aufnahme neu und der Puffer beginnt von vorn.
- Fensteraufnahme gibt es nur für das erkannte Spiel (Aufnahmemethode „Spielfenster“), nicht für beliebige Fenster.
- Die Dateien haben eine variable Bildrate (exakte Aufnahmezeiten). Einige Schnittprogramme wie Premiere wandeln das beim Import um.
- HDR wird als SDR aufgenommen; DRM-geschützte Inhalte erscheinen schwarz.
- Bei voller GPU-Last kann die Desktop-Duplizierung Bilder auslassen (FFmpeg läuft mit erhöhter CPU-Priorität, eine GPU-Priorität setzt es nicht).
- Schafft der Encoder die eingestellte Bildrate nicht, zeigt die Übersicht eine Warnung. Dann hilft ein schnelleres Encoder-Tempo. Läuft gleichzeitig NVIDIA Instant Replay, teilen sich beide den Encoder.

## Ideen für die nächste Iteration

- Getrennte Tonspuren (Spiel, Mikrofon) per Named Pipes
- Kleineres eingebautes FFmpeg (eigener Build nur mit den benötigten Teilen statt des vollen BtbN-Builds)
- Kleines Overlay beim Speichern, einfacher Trimm-Editor in der Clip-Liste
- Optionale Umwandlung in konstante Bildrate beim Speichern
- Weitere Spiele mit offizieller Ereignis-Schnittstelle (z. B. Dota 2 GSI, League of Legends Live Client Data API)

## Lizenz

ClipRing steht unter der [GNU General Public License v3.0](LICENSE). FFmpeg (GPL, Build von [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), Quellcode unter [ffmpeg.org](https://ffmpeg.org/download.html)) ist unverändert in `ClipRing.exe` eingebettet und läuft als eigener Prozess; seine Lizenz liegt neben der exe als `FFMPEG-LICENSE.txt`. [NAudio](https://github.com/naudio/NAudio) steht unter der MIT-Lizenz.
