# aion2-cube-radar

Passives Werkzeug für Aion 2 (Global). Es liest per Npcap den Server→Client-Traffic mit, wie Wireshark, und soll
live anzeigen, wo gerade Hidden Cubes stehen. Es betreibt kein Memory-Reading und keine Injection und sendet nichts.

Stand: **Phase 3 (Radar) gebaut, Abnahme im Spiel steht aus**. Der Hidden Cube ist in Verteron identifiziert (Template 1200039, Sende-Radius ≈ 105 m). Die eigene Position kommt aus vier ereignisgebundenen Quellen. Alle Erkenntnisse stehen in
[`docs/FINDINGS.md`](docs/FINDINGS.md).

## Aufbau

| Pfad | Inhalt |
|---|---|
| `src/CubeRadar.Core` | Capture (Npcap/SharpPcap), Flow-Erkennung, TCP-Reassembly, Framing/LZ4 (abgeleitet von cyberbadger6969/aion2-dps-meter, GPL-3.0), pcapng-Writer, JSONL-Dump, Census |
| `src/CubeRadar.Core/Radar` | `RadarEngine` (Cube-Status, eigene ID/Position, Ring-Fix, Template-Lernen), SQLite-Store, Spot-Index |
| `src/CubeRadar.Cli` | `cuberadar`: `radar`, `dump live`, `dump <datei>`, `census`, `devices`, `conns`; Marker per globalem Hotkey oder Konsole; Web-UI in `wwwroot/` |
| `tests/CubeRadar.Tests` | xunit: synthetische Framing-Tests, pcapng-Roundtrip, echte Stream-Fixtures |
| `tools/` | Python (nur Stdlib): `a2wire.py` (unabhängiger Decoder), `census.py`, `crosscheck.py`, `make_fixture.py`, `objects.py` (Objekt-Lebenszyklus je Template), `radius.py` (Sende-Radius), `selfscan.py` (f32-Triplet-Scan nach der eigenen Position), `selftrack.py` (eigene Positionsspur, CSV/SVG), `ringfix.py` (Fallback-Schätzung) |
| `config/opcodes.json` | Opcode-Namen inkl. Belegstatus, damit Patches ohne Code-Änderung abgefangen werden können |
| `config/cubes.json` | Cube-Signatur: Templates je Zone, Öffnungs-Item, gemessener Sende-Radius |
| `config/radar.json` | Opcodes des Radars, Selbstcheck, Nordachse, Haltezeiten, Pfad zur optionalen Spot-Liste |
| `data/radar.db` | eigene Spot-Datenbank (SQLite, nicht im Git) |
| `fixtures/` | echte Stream-Ausschnitte (Hex) mit erwarteten Frames |
| `captures/` | Aufnahmen (nicht im Git) |
| `ref/` | Referenz-Repos (nur gelesen, nicht im Git) |

## Bauen und testen

```powershell
dotnet build CubeRadar.slnx -c Release
dotnet test CubeRadar.slnx -c Release
```

Die ausführbare Datei liegt dann unter `src\CubeRadar.Cli\bin\Release\net10.0-windows\cuberadar.exe`. Kurz: `.\cuberadar.cmd`.

## Radar

```powershell
.\cuberadar.cmd radar
```

Öffnet `http://127.0.0.1:5313` im Browser.
- **Radar** zeigt die Umgebung um dich (Nord oben, Ringe, Sichtweite ≈ 105 m gestrichelt).
- **Karte** zeigt alle Cubes, deine Spur und, falls vorhanden, die möglichen Spots der Zone.
- **Liste:** Distanz, Kompassrichtung, Höhenunterschied, „gesehen vor“, „existiert seit“ und Status. Die Status sind:
  sichtbar, außer Sicht (steht vermutlich noch), wird geöffnet, geöffnet (von dir oder Spieler N), weg.
- **„Ton an“ und „Hinweise an“** im Browser geben einen Ton bzw. eine Desktop-Benachrichtigung bei jedem neuen Cube.
  Die Konsole piept zusätzlich (abschalten mit `--no-beep`).
- **Eigene Position:** Sie kommt nur bei Ereignissen, etwa wenn du Buffs wirkst oder angegriffen wirst. Die Kopfzeile
  zeigt, wie alt sie ist. Ohne frische Position zeigt das Radar eine Schätzung mit Unsicherheitskreis.
- **Nordrichtung:** Die Konfiguration nimmt Welt −Y an (unbestätigt). Stimmt die Richtung nicht mit der Minimap
  überein, stellst du sie oben bei „Norden“ um. Die Auswahl wird gespeichert.
- **Aufzeichnung:** Jede Sitzung schreibt `captures
adar_<datum_zeit>
aw.pcapng` und `markers.jsonl`. Die Hotkeys
  aus dem nächsten Abschnitt funktionieren auch hier.
- **Datenbank:** Jede Sichtung landet in `data
adar.db`. Export über die Links „CSV“ und „JSON“ in der Liste.
- **Selbstcheck:** Bleiben erwartete Pakete aus (etwa nach einem Patch), erscheint ein roter Hinweis. Die Opcodes
  stehen in `config
adar.json`.
- **Andere Zonen:** Wird ein Objekt mit dem Cube-Key geöffnet oder steht es exakt auf einem bekannten Spot, lernt das
  Radar dessen Template als Cube-Template.

Replay zum Testen: `.\cuberadar.cmd radar --replay captures\<sitzung>
aw.pcapng --speed 5`. Mit `--stop-at 200`
hält der Replay bei Sekunde 200 des Mitschnitts an.

## Aufnehmen

```powershell
.\cuberadar.cmd dump live              # bis "q" + Enter oder Strg+C
.\cuberadar.cmd dump live --seconds 120
```

Jede Aufnahme landet in `captures\<datum_zeit>\`:
- `raw.pcapng`: rohe Pakete für Replays und Wireshark
- `frames.jsonl`: jeder dekodierte Frame
- `markers.jsonl`: die gesetzten Marker
- `census.json` und `census.txt`: Opcode-Census und Stream-Gesundheit

**Marker** (Bestätigungston pro Taste):

| Hotkey (global, auch im Spiel) | Marker |
|---|---|
| Strg+Umschalt+F5 | `cube_visible` (Cube sichtbar) |
| Strg+Umschalt+F6 | `cube_opened` (Cube geöffnet) |
| Strg+Umschalt+F7 | `cube_out_of_sight` (Cube außer Sicht) |
| Strg+Umschalt+F8 | `cube_back` (wieder in Sicht/zurück) |
| Strg+Umschalt+F9 | `note` (allgemeiner Marker) |

In der Konsole setzt Enter einen Marker, optional mit Text. `1`–`5` entsprechen den Hotkeys, `q` beendet die Aufnahme.
Andere Tasten belegst du mit `--hotkeys "ctrl+alt+F1=cube_visible,…"`.

## Auswerten

```powershell
.\cuberadar.cmd dump captures\<sitzung>\raw.pcapng     # Replay -> raw.replay.jsonl (+ markers.jsonl wird eingemischt)
.\cuberadar.cmd census captures\<sitzung>\raw.pcapng
python tools\census.py captures\<sitzung>\frames.jsonl
python tools\crosscheck.py captures\<sitzung>\raw.pcapng captures\<sitzung>\frames.jsonl
```

### frames.jsonl

Pro Zeile ein JSON-Objekt. `t` ist Unix-ms, bei Paketen die Capture-Uhr, bei Markern die Wanduhr.

- `frame`: `n` (laufende Nummer), `t`, `flow`, `src` (`top` | `lz4` | `emb`), `depth`, `path`, `op` (Wire-Bytes als
  Hex), `name`, `len` (Body), `wire` (Frame inkl. Varint), `flag` (optional) und `hex` (Body ohne Opcode).
  - `path` beschreibt die Verschachtelung: `B3.0/B4.2` ist der dritte Frame in Bundle 4, und Bundle 4 ist selbst der
    erste Frame von Bundle 3. `E7.0` steht für ein Bundle, das im Body eines anderen Pakets eingebettet war.
- `bundle`: `id`, `src`, `depth`, `path`, `host`, `raw`, `zlen`, `out`, `ok`
- `marker`: `t`, `local`, `label`, `text`, `via`
- außerdem `session`, `flow`, `gap`, `stats`

## Lizenz

GPL-3.0 (siehe `LICENSE`), weil Teile von cyberbadger6969/aion2-dps-meter übernommen sind. Abgeleitete Dateien sind im
Kopfkommentar gekennzeichnet. Das Projekt ist nur für den privaten Gebrauch gedacht. Die Nutzung verstößt
möglicherweise gegen die AGB von NCSOFT, das Risiko trägt der Nutzer.
