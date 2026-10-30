# ClipRing

[![Build](https://github.com/michpese/ClipRing/actions/workflows/build.yml/badge.svg)](https://github.com/michpese/ClipRing/actions/workflows/build.yml)

[English](README.md) · **Deutsch**

**Deine besten Szenen mit einem Tastendruck speichern.** ClipRing läuft unauffällig im Hintergrund und hält immer die letzten Minuten deines Spiels bereit. Drückst du den Hotkey, wird genau dieser Moment als Video gespeichert.

Es funktioniert wie **NVIDIA ShadowPlay (Instant Replay)** oder **AMD ReLive**, ist aber nicht an eine Grafikkartenmarke gebunden und quelloffen. ClipRing ist für **kompetitive Spiele** gemacht: In Counter-Strike 2 speichert es deine Multi-Kills automatisch.

> **Frühe Version (0.1).** ClipRing funktioniert, wurde bisher aber vor allem auf einem PC mit NVIDIA-Grafikkarte und Counter-Strike 2 getestet. Klappt etwas auf deinem System nicht, eröffne bitte ein [Issue](https://github.com/michpese/ClipRing/issues) und häng das Protokoll an (siehe [Tipps](#tipps)).

## Was es kann

- **Instant Replay:** hält die letzten 15 Sekunden bis 10 Minuten bereit. Ein Hotkey speichert alles, ein zweiter nur die letzten Sekunden.
- **Kostet kaum FPS:** Die Aufnahme läuft über den Video-Encoder deiner Grafikkarte (NVIDIA, AMD oder Intel), nicht über die CPU.
- **Scharf und flüssig:** bis 4K und auf schnellen Monitoren bis 480 fps, als H.264, HEVC oder AV1. Was dein PC nicht kann, ist ausgegraut.
- **4:3 gestreckt:** Spielst du gestreckt (z. B. 1280 × 960 in CS2), werden die Clips genauso gestreckt und sehen aus wie auf deinem Monitor.
- **Dual-Modus-Monitore:** Wechselt dein Monitor zum Beispiel zwischen 4K 240 Hz und 1080p 480 Hz, zieht ClipRing mit und merkt sich Bildrate und Auflösung für jeden Modus.
- **Automatische CS2-Highlights:** 2K, 3K, 4K und Aces werden von selbst gespeichert. Standardmäßig nur in Wettkampf und Premier, damit Deathmatch nicht deinen Ordner füllt.
- **Ordentliche Clips:** Jedes Spiel bekommt einen eigenen Ordner, die Dateien heißen nach Spiel, Datum und Uhrzeit.
- **Spielton und Mikrofon,** jeweils mit eigener Lautstärke.
- **Stört nicht:** sitzt im Infobereich, startet auf Wunsch mit Windows, und die Hotkeys funktionieren auch in Vollbild-Spielen.
- **Deutsch und Englisch.**
- **Eine Datei:** keine Installation, nichts weiter herunterzuladen.

## Loslegen

1. Unter [Releases](https://github.com/michpese/ClipRing/releases) die neueste `ClipRing-…-win-x64.zip` herunterladen und irgendwo entpacken.
2. `ClipRing.exe` starten. Das Programm ist noch nicht signiert, Windows zeigt deshalb eventuell „Der Computer wurde durch Windows geschützt“. Dann auf **Weitere Informationen → Trotzdem ausführen** klicken.
3. Beim ersten Start prüft ClipRing Grafikkarte und Monitore. Das dauert ein paar Sekunden.
4. Spielen. Dann drücken:

   | Tasten | Was passiert |
   |---|---|
   | **Strg+Alt+F10** | speichert die letzten 2 Minuten |
   | **Strg+Alt+F9** | speichert die letzten 30 Sekunden |
   | **Strg+Alt+F11** | pausiert die Aufnahme oder setzt sie fort |

   Alle Tasten und Längen lassen sich in der App ändern.
5. Deine Clips liegen unter `Videos\ClipRing\<Spiel>`.

Die Sprache richtet sich nach Windows. Ändern lässt sie sich unter **Allgemein → Sprache**.

## Automatische Highlights in Counter-Strike 2

1. In ClipRing **Highlights** öffnen und auf **Einrichten** klicken.
2. CS2 neu starten. Der Status wechselt auf „Verbunden“.
3. Auswählen, ab wie vielen Kills ein Clip entsteht und in welchen Modi.

Kills kurz hintereinander landen in einem Clip, der zum Beispiel `…_3K.mp4` oder `…_Ace.mp4` heißt.

## Ist es sicher mit VAC und FACEIT?

ClipRing greift nicht ins Spiel ein. Es schleust nichts in CS2 ein, liest nicht dessen Speicher und sendet keine Eingaben. Den Bildschirm nimmt es mit denselben Windows-Funktionen auf, die auch OBS und Discord verwenden. CS2-Highlights kommen über Valves offizielle Game State Integration, die genau für solche Programme gedacht ist.

Garantien können nur Valve und FACEIT geben, aber ClipRing tut nichts, wonach Anti-Cheat-Systeme suchen. Die Startoption `-allow_third_party_software` brauchst du **nicht**, lass sie weg.

## Was du brauchst

- Windows 10 oder 11 (64 Bit)
- Eine Grafikkarte oder integrierte Grafik von NVIDIA, AMD oder Intel. Ohne Video-Encoder nimmt ClipRing mit der CPU auf, das ist deutlich langsamer.
- Für AV1 eine neuere Grafikkarte: NVIDIA RTX 40/50, AMD Radeon RX 7000/9000 (oder eine passende APU) oder Intel Arc. ClipRing erkennt das selbst.
- Genug Arbeitsspeicher: Die App zeigt an, wie viel deine Einstellungen brauchen. Mit den voreingestellten 2 Minuten sind das meist ein paar hundert MB bis etwa 1,5 GB.

## Tipps

- **Clips ruckeln oder haben weniger Bilder als eingestellt:** Unter **Video → Encoder-Tempo** „Schnell“ wählen und andere Aufnahmeprogramme schließen.
- **Die Aufnahme flackert oder zeigt alte Bilder:** Grafiktreiber aktualisieren. Hilft das nicht, unter **Video → Aufnahmemethode** „Spielfenster“ probieren.
- **Ein Hotkey funktioniert nicht:** Wahrscheinlich nutzt ein anderes Programm dieselben Tasten. Eine andere Kombination wählen.
- **Etwas anderes stimmt nicht:** Unter **Allgemein → Protokollordner öffnen** liegt ein Protokoll, das bei der Fehlersuche hilft. Bitte an ein [Issue](https://github.com/michpese/ClipRing/issues) anhängen.

## So funktioniert es, kurz erklärt

ClipRing nimmt deinen Bildschirm ständig mit der Grafikkarte auf und hält die letzten Minuten im Arbeitsspeicher, statt dauernd auf die SSD zu schreiben. Drückst du den Hotkey, speichert es genau diesen Teil als MP4, ohne neu zu kodieren: Das geht schnell, und die Qualität bleibt gleich. Aufnahme und Kodierung übernimmt [FFmpeg](https://ffmpeg.org), das in ClipRing eingebaut ist.

Bekannte Grenzen: Spielton und Mikrofon landen auf einer gemeinsamen Tonspur, HDR wird als SDR aufgenommen, und kopiergeschützte Videos erscheinen schwarz.

## Für Entwickler

Nötig sind Windows und das [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`). Dann im Projektordner:

```powershell
.\build.ps1                 # baut dist\ClipRing\ClipRing.exe mit eingebautem FFmpeg
.\build.ps1 -SelfContained  # bringt zusätzlich .NET mit, läuft auf jedem Windows-PC (wie die Releases)
.\build.ps1 -UpdateFfmpeg   # neueste FFmpeg-Version holen
```

Falls PowerShell das Skript blockiert: `powershell -ExecutionPolicy Bypass -File .\build.ps1`.

- Jeder Push wird auf GitHub (Windows) gebaut. Der Build startet die App auch, öffnet jede Seite in beiden Sprachen und prüft das eingebaute FFmpeg.
- Ein Tag wie `v0.2.0` erzeugt automatisch ein Release.
- Die Texte liegen in `src/ClipRing/Localization/de.json` und `en.json`.
- Code-Überblick: `Capture/` (Bildschirmaufnahme, Encoder, Speicherpuffer, Speichern), `Audio/`, `Games/` (Spielerkennung), `Highlights/` (CS2), `UI/`, `Services/`.

## Lizenz

ClipRing steht unter der [GNU General Public License v3.0](LICENSE). Das eingebaute FFmpeg ist ebenfalls GPL und unverändert enthalten (Build von [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)). Jeder Download enthält seine Lizenz (`FFMPEG-LICENSE.txt`), und die Release-Notizen verlinken den genauen Quellcode der enthaltenen Version (ab 0.1.1 auch als `FFMPEG-SOURCE.txt` im Download). [NAudio](https://github.com/naudio/NAudio) steht unter der MIT-Lizenz.
