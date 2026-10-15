# Anleitung: Cube Radar für Aion 2

Cube Radar zeigt in einem kleinen Mini-Radar über dem Spiel, wo gerade Hidden Cubes stehen: Entfernung, Richtung
und Höhe. Es liest den Netzwerkverkehr von Aion 2 nur passiv mit (wie Wireshark). Es liest keinen Speicher,
verändert nichts am Spiel und sendet nichts an das Spiel. Ins Internet geht es nur für die Suche nach Updates bei
GitHub, und die lässt sich abschalten (siehe [Updates](#8-updates)).

> **Achtung:** Laut einem anderen Tool-Autor stuft der NCSOFT-Support Software, die Spieldaten mitliest, als Verstoß
> gegen die Nutzungsbedingungen ein. Die Nutzung erfolgt auf eigenes Risiko.

## 1. Voraussetzungen

- Windows 10 oder 11 (64 Bit)
- **Npcap**, kostenlos von [npcap.com](https://npcap.com). Den Installer mit den Standardoptionen ausführen.
- Aion 2 **Global** im **Fenster- oder randlosen Vollbildmodus**. Über exklusivem Vollbild kann Windows nichts
  einblenden.

## 2. Starten und beenden

1. Unter [Releases](https://github.com/dennisreichenberg/aion2-cube-radar/releases/latest) `Cube.Radar.exe`
   herunterladen und irgendwohin legen, z. B. auf den Desktop. Eine Installation ist nicht nötig.
2. Doppelklick auf die exe. Meldet Windows SmartScreen eine unbekannte App: „Weitere Informationen“ →
   „Trotzdem ausführen“.
3. Unten rechts in der Taskleiste erscheint ein Radar-Symbol (evtl. hinter dem kleinen ^-Pfeil). Ein Fenster gibt
   es nicht, das Radar läuft im Hintergrund.
4. Aion 2 starten und in die Welt gehen. Das Mini-Radar erscheint, sobald Aion 2 das aktive Fenster ist.

**Beenden:** ⚙ im Mini-Radar → „Radar beenden“, oder Rechtsklick auf das Radar-Symbol in der Taskleiste →
„Beenden“.

Es läuft immer nur ein Radar; ein zweiter Doppelklick meldet „Cube Radar läuft bereits“.

## 3. Das Mini-Radar lesen

```
┌ CUBE RADAR   ● Pos. 2 s   120 m ⚙ ┐   Kopfzeile: Alter deiner Position, Zoom, Einstellungen
│                N                  │   Norden ist oben
│          ·  ·  ·  ·  ·            │
│       ·     60 m        ·         │   Ringe mit Meterangabe
│      ·        ▲ 86 m ▲38   ·      │   Cube: 86 m entfernt, 38 m über dir
│      ·         ●            ·     │   ● = du, dünne Linie = deine letzte Laufspur
│       ·                   ·    »  │   » = Cube außerhalb des Zooms, in diese Richtung
│          ·  ·  ·  ·  ·            │
└───────────────────────────────────┘
```

**Du:** Der blaue Punkt in der Mitte bist du. Die dünne blaue Linie ist deine letzte Laufspur. Sie zeigt, woher
du kommst, denn das Spiel sendet keine Blickrichtung. Der gestrichelte Kreis ist die Sichtweite von etwa 100 m:
Nur Cubes in diesem Umkreis schickt der Server überhaupt.

**Cubes:**

- Symbol nach Höhe: **▲** = über dir, **▼** = unter dir, **■** = etwa auf deiner Höhe (± 3 m).
- Beschriftung: `86 m ▲38` heißt 86 m entfernt und 38 m über dir; `≈` steht für gleiche Höhe.
- Farbe nach Zustand:
  - **grün, gefüllt:** gerade sichtbar
  - **orange, hohl:** außer Sicht, steht vermutlich noch da
  - **gelb:** wird gerade geöffnet
- Geöffnete oder verschwundene Cubes verschwinden vom Radar.
- Bei jedem neuen Cube ertönt ein Piepton.

**Eigene Position (Kopfzeile, mit farbigem Punkt):** Das Spiel schickt die eigene Position nicht laufend, sondern
nur bei Ereignissen, etwa wenn du einen Buff wirkst oder angegriffen wirst.

- `Pos. 2 s` in **grün**: frisch (bis 5 s)
- `Pos. 20 s` in **orange**: etwas älter (bis 30 s)
- `Pos. 90 s` in **rot**: veraltet; Entfernungen können deutlich daneben liegen
- `± 40 m`: Die Position ist nur geschätzt; der gepunktete Kreis um dich zeigt die Unsicherheit.
- `Pos. ?` bzw. „eigene Position unbekannt“: **einmal einen Buff auf dich selbst wirken**, dann ist sie da.

## 4. Bedienen

- **Verschieben:** `Strg+Umschalt+F11` öffnet die Einstellungen. Solange sie offen sind, bleibt das Radar sichtbar
  und lässt sich mit der Maus ziehen. Die Stelle wird gespeichert. Noch einmal `Strg+Umschalt+F11` schließt die
  Einstellungen wieder.
- **Zoom:** `Strg+Umschalt+F12`, oder das Mausrad über dem Radar (hoch = näher), solange die Einstellungen offen
  sind. Stufen: 60 / 120 / 250 m.
- **Ganz aus/an:** `Strg+Umschalt+F10`.
- **Automatisch ausblenden:** Das Radar ist nur sichtbar, wenn Aion 2 das aktive Fenster ist. In Menüs (solange
  der Mauszeiger sichtbar ist) blendet es sich ebenfalls aus, auch wenn der Zeiger an seiner Stelle steht. So
  verdeckt es nie ein Menü. Beides lässt sich in den Einstellungen abschalten.
- Das Radar liegt immer im Vordergrund, nimmt dem Spiel aber nie den Fokus.

**Rechtsklick auf das Radar-Symbol in der Taskleiste:**
Einstellungen (auch per Doppelklick auf das Symbol), Overlay ein/aus, Zoom wechseln, Web-Ansicht öffnen,
Aufnahmen-Ordner öffnen, Beenden.

## 5. Einstellungen

Öffnen mit `Strg+Umschalt+F11` (auch im Spiel), per Doppelklick auf das Radar-Symbol oder über ⚙ oben rechts im
Mini-Radar. Änderungen wirken sofort und werden gespeichert. Solange das Fenster offen ist, bleibt das Mini-Radar
sichtbar, damit du es verschieben und zoomen kannst.

- **Ton bei neuem Cube:** an/aus, „Testen“, Lautstärke (beim Verstellen hörst du den Ton) und optional eine
  eigene `.wav`-Datei statt des Standard-Pieptons. Im Windows-Lautstärkemixer steht „Cube Radar“ als eigene App
  und lässt sich dort zusätzlich regeln oder stummschalten.
- **Overlay:** Zoom, Größe (75–150 %), Deckkraft.
- **Norden:** Zeigt der Cube in eine andere Richtung als auf der Minimap im Spiel (gespiegelt oder um 90° gedreht),
  hier umstellen, bis es passt. Standard ist „−Y“.
- **Wann ist das Overlay sichtbar?** „Nur wenn Aion 2 im Vordergrund ist“ und „In Menüs ausblenden“.
- **Updates:** automatische Suche an/aus, „Jetzt prüfen“, „Jetzt aktualisieren“, „Versionen auf GitHub“ (siehe
  [Updates](#8-updates)).
- **Sonstiges:** Position zurücksetzen (falls das Radar außerhalb des Bildschirms gelandet ist), Web-Ansicht öffnen,
  Aufnahmen-Ordner öffnen, Radar beenden.

## 6. Web-Ansicht (optional)

Über „Web-Ansicht öffnen“ öffnet sich im Browser eine große Ansicht (`http://127.0.0.1:5313`, nur auf deinem PC
erreichbar):

- großes Radar mit Höhenleiste
- Karte mit allen Cubes der Sitzung und deiner Spur
- Liste mit Entfernung, Kompassrichtung, Höhe, „gesehen vor“, „existiert seit“ und Status, Export als CSV/JSON
- „Ton an“ und „Hinweise an“ für Ton bzw. Desktop-Benachrichtigung bei jedem neuen Cube

## 7. Wo funktioniert es?

Der Hidden Cube ist in **Verteron** bestätigt. In anderen Gebieten, z. B. **Altgard**, ist die Cube-Kennung noch
nicht an eigenen Daten bestätigt. Das Radar lernt sie, sobald dort in deiner Nähe ein Cube mit Schlüssel geöffnet
wird.

## 8. Updates

Das Radar sucht beim Start und danach alle 6 Stunden bei GitHub nach einer neuen Version.

- Gibt es eine, erscheint unten rechts ein Hinweis, und im Menü des Radar-Symbols steht „Update auf … installieren“.
  Ein Klick darauf (oder Einstellungen → „Jetzt aktualisieren“) fragt nach, lädt die neue Version, prüft ihre
  Prüfsumme, beendet das Radar und startet es nach ein paar Sekunden in der neuen Version neu. Einstellungen und Daten
  bleiben erhalten. Ohne deine Zustimmung wird nichts installiert.
- Dabei taucht neben der exe kurz eine `Cube Radar.update.exe` auf; sie verschwindet nach dem Neustart von selbst.
- Was GitHub dabei sieht: eine Anfrage nach der neuesten Version mit deiner IP-Adresse und der Kennung
  `CubeRadar/<Version>`. Sonst nichts, insbesondere keine Spieldaten.
- **Abschalten:** Einstellungen → „Automatisch nach Updates suchen“. Dann verbindet sich das Radar gar nicht mehr mit
  dem Internet; „Jetzt prüfen“ fragt nur, wenn du es anklickst.
- Liegt die exe in einem Ordner ohne Schreibrecht (z. B. unter „Programme“), kann sie sich nicht selbst ersetzen. Dann
  die neue Version von der [Release-Seite](https://github.com/dennisreichenberg/aion2-cube-radar/releases/latest)
  herunterladen.

## 9. Probleme lösen

- **Radar erscheint nicht:** Ist Aion 2 das aktive Fenster? Läuft das Spiel im Fenster- oder randlosen
  Vollbildmodus? Mit `Strg+Umschalt+F10` versehentlich ausgeschaltet? Notfalls Einstellungen → „Position
  zurücksetzen“.
- **„wartet auf Daten …“ bleibt stehen:** Bist du im Spiel in der Welt? Ist Npcap installiert? Wurde bei der
  Npcap-Installation „Restrict Npcap driver's access to Administrators only“ angehakt, die exe per Rechtsklick
  „Als Administrator ausführen“.
- **`Pos. ?` oder „eigene Position unbekannt“:** einen Buff auf dich selbst wirken.
- **Richtung stimmt nicht:** Einstellungen → „Norden“ umstellen.
- **Kein Ton:** Einstellungen → „Ton abspielen“ an? „Testen“ drücken. Im Windows-Lautstärkemixer „Cube Radar“
  prüfen.
- **Nach einem Spiel-Patch keine Cubes mehr:** Das Spiel hat vermutlich seine Paketnummern geändert. In der
  Web-Ansicht erscheint dann ein roter Hinweis. Bitte auf eine neue Version warten.

## 10. Daten und Deinstallation

- Alles, was das Radar speichert, liegt in `%LOCALAPPDATA%\CubeRadar` (Explorer-Adressleiste): Aufnahmen,
  Cube-Datenbank, Einstellungen, Log.
- Die Aufnahmen enthalten deinen mitgeschnittenen Spielverkehr, **inklusive Charakternamen und Chat**. Lade sie
  nirgends öffentlich hoch, auch nicht in ein GitHub-Issue.
- **Deinstallieren:** Radar beenden, `Cube.Radar.exe` löschen und den Ordner `%LOCALAPPDATA%\CubeRadar` löschen.
  Npcap lässt sich bei Bedarf über „Apps“ in den Windows-Einstellungen entfernen.

## Markierungen (optional)

Wer beim Verbessern helfen will, kann beim Spielen Markierungen in die Aufnahme setzen. Sie landen nur lokal in
der Aufnahme:

- `Strg+Umschalt+F5`: Cube im Spiel gesehen
- `Strg+Umschalt+F6`: Cube geöffnet
- `Strg+Umschalt+F7`: Cube außer Sicht
- `Strg+Umschalt+F8`: Cube wieder in Sicht
- `Strg+Umschalt+F9`: Notiz
