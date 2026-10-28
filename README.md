# ReplayClip

Schlanker Replay-Buffer für Windows 11 – wie NVIDIA Instant Replay, aber mit **getrennten Tonspuren**,
**Bibliothek mit Tags** und **Schnitt-Editor**. Braucht deutlich weniger RAM als OBS oder die NVIDIA App.

## Funktionen

- Nimmt dauerhaft einen Monitor auf und behält die letzten 1–20 Minuten (Hotkey speichert, Standard **Alt+F10**)
- Bild über Desktop Duplication + **NVENC** (H.264 oder HEVC) – kaum CPU-Last
- Bis zu 6 **getrennte Tonspuren**, z.B. Discord / restlicher PC-Sound / Mikrofon, Lautstärke je Spur bis 500 %
- **Bibliothek**: Ordner je Spiel, Kachel- und Listenansicht, Tags mit eigenen Farben, Schnell-Tags 1–9, Favoriten, Drag & Drop nach Discord
- **Editor**: kürzen, Spuren stummschalten, Lautstärke, Export als Mix und/oder einzelne Spuren – rendert im Hintergrund
- Sounds und Einblendung beim Speichern, eigene Akzentfarben, **automatische Updates** über GitHub Releases

## Installation

1. Unter [Releases](https://github.com/lucanetzer18-collab/ReplayClip/releases/latest) die neueste `ReplayClip-x.y.z.zip` herunterladen.
2. In einen eigenen Ordner entpacken (z.B. `C:\Tools\ReplayClip`) und `ReplayClip.exe` starten.
3. Fehlt FFmpeg, bietet ReplayClip den Download direkt an.

Voraussetzungen: Windows 11, NVIDIA-Grafikkarte mit aktuellem Treiber, .NET Framework 4.8 (bei Windows 11 dabei).
Details stehen in [LIESMICH.txt](LIESMICH.txt).

## Updates

ReplayClip prüft beim Start und alle 6 Stunden, ob es ein neueres Release gibt, und aktualisiert sich auf Wunsch selbst
(Einstellungen › Allgemein › Updates).

## Selbst bauen

`build.bat` doppelklicken – nutzt den C#-Compiler, der in Windows schon eingebaut ist (kein Visual Studio nötig).
Der Quellcode liegt in `src/` (C# 5, WinForms, .NET Framework 4.8).

## Neue Version veröffentlichen

1. Version in `src/Program.cs` erhöhen (`AssemblyVersion` und `AssemblyFileVersion`, z.B. `1.5.1.0`).
2. Oben in `CHANGELOG.md` einen Abschnitt `## 1.5.1` mit den Neuerungen einfügen.
3. Committen, Tag setzen und hochladen:
   ```
   git commit -am "Version 1.5.1"
   git tag v1.5.1
   git push --follow-tags
   ```
4. GitHub Actions baut die EXE, packt `ReplayClip-1.5.1.zip` und veröffentlicht das Release.
   Alle installierten ReplayClips bieten das Update danach automatisch an.

## Drittanbieter

- **FFmpeg** wird nicht mitgeliefert, sondern beim ersten Start heruntergeladen ([BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)).
- Schrift **Inter** – © The Inter Project Authors, SIL Open Font License 1.1 (`src/fonts/LICENSE-Inter.txt`).

## Lizenz

© Luca. Alle Rechte vorbehalten.
