# Änderungen

Neue Einträge immer oben einfügen. Der Abschnitt mit der passenden Versionsnummer wird beim Release
automatisch als Beschreibung übernommen und in der App beim Update angezeigt.

## 1.6.0
- **Für Discord**: Clip mit einem Klick unter Discords Upload-Grenze rechnen (10 / 50 / 500 MB einstellbar) – landet direkt in der Zwischenablage, in Discord Strg+V.
- **Hover-Vorschau** in der Bibliothek: Maus über eine Kachel halten und links/rechts bewegen = durch den Clip spulen.
- **Mehrere Abschnitte schneiden**: im Editor Auswahl setzen und **A** drücken (oder „+ Abschnitt“) – alle Abschnitte werden beim Speichern aneinandergehängt.
- **Controller-Hotkey**: Clip speichern per Tastenkombination am Controller (Einstellungen › Tastenkürzel).
- **Rauschunterdrückung** je Tonspur (filtert Tastatur, Lüfter, Rauschen – ideal fürs Mikro).
- Weniger RAM und CPU: Im Infobereich werden Vorschaubilder freigegeben und der Arbeitsspeicher an Windows zurückgegeben; die Kopfleiste zeichnet nicht mehr, solange das Fenster versteckt ist; kleinere Bild-Caches.

## 1.5.1
- Läuft jetzt auch mit **AMD-Grafikkarten** (AMF), **Intel-/Onboard-Grafik** (Quick Sync) und **ganz ohne Grafikkarte** (Prozessor, x264).
  Einstellungen › Aufnahme › Encoder: „Automatisch erkennen“ testet, was auf dem PC funktioniert, und zeigt das Ergebnis an.
- Editor-Export nutzt ebenfalls den passenden Encoder (mit Prozessor als Rückfallebene).
- Eigene Einstellungsseite **Updates**: installierte Version, letzte Suche, Neuerungen der neuen Version und „Jetzt aktualisieren“.
- **Mitteilung bei neuer Version**: Hinweis im Fenster und Windows-Mitteilung, wenn ReplayClip im Hintergrund läuft.

## 1.5.0
- Updates direkt in der App: ReplayClip prüft GitHub auf neue Versionen, zeigt die Neuerungen und aktualisiert sich auf Wunsch selbst (Einstellungen › Allgemein › Updates).
- Projekt auf GitHub: automatischer Build und Release, sobald eine neue Versionsnummer hochgeladen wird.

## 1.4.9
- Kopfleiste (Pufferbalken, Timer, Pegel) läuft beim Hovern in der Bibliothek flüssig weiter.

## 1.4.8
- Tag-Farben selbst wählen (Rechtsklick auf einen Tag).
- Tastenkürzel „Buffer an/aus“ entfernt.
- Version steht im Namen der Zip-Datei.

## 1.4.5
- Pegel und Statuszeile werden auch während Animationen aktualisiert.

## 1.4.4
- FFmpeg wird zusammen mit ReplayClip beendet (auch bei „Task beenden“).
- „ReplayClip starten.bat“ beendet alte Versionen und öffnet das Fenster direkt.

## 1.4.3
- Lautstärke je Spur bis 500 % mit weichem Begrenzer.
- Tiefer Ton beim Drücken des Hotkeys, höherer Ton wenn der Clip gespeichert ist.
- Editor schließt beim Speichern sofort, gerendert wird im Hintergrund mit Fortschritt auf der Kachel.

## 1.4.2
- Deutlich flüssigeres Hovern in der Bibliothek (an die Bildwiederholrate gekoppelt, nur betroffene Kacheln neu zeichnen).

## 1.4.1
- „Spur bearbeiten“ in den Einstellungen repariert.

## 1.4
- Neues Design mit Schrift Inter, eigene Primär-/Sekundärfarbe, Animationen.
- Sound bei „Clip gespeichert“ wählbar (eingebaut oder eigene Datei), Einblendung über dem Spiel.
- Puffer wird nach dem Speichern geleert (abschaltbar).
- Flüssigerer Editor.

## Frühere Versionen (bis 1.3)
- Replay-Buffer mit NVENC, getrennte Tonspuren (z.B. Discord / PC / Mikrofon), Hotkey.
- Pegelanzeige und Test je Tonspur.
- Schnitt-Editor: kürzen, Spuren stummschalten, Lautstärke, Export als Mix und/oder einzelne Spuren.
- Dunkle Oberfläche, Bibliothek mit Ordnern je Spiel, Tags, Schnell-Tags 1–9, Favoriten.
