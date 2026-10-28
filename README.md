# ReplayClip

Schlanker Replay-Buffer für Windows 11 – wie NVIDIA Instant Replay, aber mit **getrennten Tonspuren**,
**Bibliothek mit Tags** und **Schnitt-Editor**. Braucht deutlich weniger RAM als OBS oder die NVIDIA App.

## Funktionen

- **Drei Modi** (oben umschaltbar):
  - **Rewind** – behält immer die letzten 1–20 Minuten, **Alt+F10** speichert den Clip
  - **Aufnahme** – dauerhaft aufnehmen (z.B. Turniere), **Alt+F9** Start/Stopp, eigener Speicherort
  - **Screenshot** – **Alt+F12**, eigener Speicherort und Ordnerstruktur, Bild-Editor (zuschneiden, Größe, Format)
- Bild über Desktop Duplication + Grafikkarten-Encoder: **NVIDIA** (NVENC), **AMD** (AMF), **Intel/Onboard** (Quick Sync) – oder ohne Grafikkarte über den Prozessor
- Bis zu 6 **getrennte Tonspuren**, z.B. Discord / restlicher PC-Sound / Mikrofon, Lautstärke je Spur bis 500 %
- **Bibliothek**: Ordner je Spiel, Kachel- und Listenansicht, Tags mit eigenen Farben, Schnell-Tags 1–9, Favoriten, Drag & Drop nach Discord
- **Editor**: kürzen, mehrere Abschnitte zusammenschneiden, Spuren stummschalten, Lautstärke, Export als Mix und/oder einzelne Spuren – rendert im Hintergrund
- **Für Discord**: Clip mit einem Klick unter das Upload-Limit rechnen und in die Zwischenablage legen
- **Hover-Vorschau** in der Bibliothek, **Controller-Hotkey**, **Rauschunterdrückung** fürs Mikro
- Sounds und Einblendung beim Speichern, eigene Akzentfarben, **automatische Updates** über GitHub Releases

## Installation

1. Unter [Releases](https://github.com/lucanetzer18-collab/ReplayClip/releases/latest) die neueste `ReplayClip-x.y.z.zip` herunterladen.
2. In einen eigenen Ordner entpacken (z.B. `C:\Tools\ReplayClip`) und `ReplayClip.exe` starten.
3. Fehlt FFmpeg, bietet ReplayClip den Download direkt an.

Voraussetzungen: Windows 10 (ab 20348) / 11, .NET Framework 4.8 (bei Windows 11 dabei). Grafikkarte empfohlen (NVIDIA, AMD oder Intel mit aktuellem Treiber), geht aber auch ohne.
Details stehen in [LIESMICH.txt](LIESMICH.txt).

## Updates

ReplayClip prüft beim Start und alle 6 Stunden, ob es ein neueres Release gibt, und aktualisiert sich auf Wunsch selbst
(Einstellungen › Updates). Bei einer neuen Version gibt es einen Hinweis im Fenster und eine Windows-Mitteilung.

## Selbst bauen

`build.bat` doppelklicken – nutzt den C#-Compiler, der in Windows schon eingebaut ist (kein Visual Studio nötig).
Der Quellcode liegt in `src/` (C# 5, WinForms, .NET Framework 4.8).

## Neue Version veröffentlichen

1. Version in `src/Program.cs` erhöhen (`AssemblyVersion` und `AssemblyFileVersion`, z.B. `1.5.1.0`).
2. Oben in `CHANGELOG.md` einen Abschnitt `## 1.5.1` mit den Neuerungen einfügen.
3. Committen und auf `main` hochladen.
4. GitHub Actions merkt die neue Versionsnummer, baut die EXE, packt `ReplayClip-1.5.1.zip` und veröffentlicht
   das Release (inkl. Tag `v1.5.1`). Alle installierten ReplayClips bieten das Update danach automatisch an.
   Commits ohne neue Versionsnummer erzeugen kein Release.

## Drittanbieter

- **FFmpeg** wird nicht mitgeliefert, sondern beim ersten Start heruntergeladen ([BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)).
- Schrift **Inter** – © The Inter Project Authors, SIL Open Font License 1.1 (`src/fonts/LICENSE-Inter.txt`).

## Lizenz

© Luca. Alle Rechte vorbehalten.
