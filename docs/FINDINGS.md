# FINDINGS – Aion 2 Hidden Cube Radar

Laufendes Protokoll aller Erkenntnisse. Ziel: Nach einem Patch kann eine spätere Session hier nahtlos weitermachen.

## Konventionen

- **Opcodes** werden als die zwei Bytes geschrieben, wie sie auf der Leitung stehen: `41 36` (in JSON/Code: `"4136"`).
  Zahlen in Bodies sind little-endian. `varint` = LEB128 (max. 5 Byte).
- **Status jeder Aussage:**
  - **BELEGT**: an eigenen Mitschnitten nachgewiesen. Mitschnitt, Datum und Client-Build sind angegeben.
  - **BEOBACHTET**: in eigenen Mitschnitten gesehen, aber noch nicht gezielt geprüft (z. B. ohne Marker oder nur einmal).
  - **COMMUNITY**: in mehreren Referenz-Repos übereinstimmend, an eigenen Daten noch nicht geprüft.
  - **HYPOTHESE**: nur eine Quelle, oder es fehlt jeder eigene Beleg.
- Mitschnitte liegen in `captures/<yyyyMMdd_HHmmss>/` (nicht im Git, enthalten Spielerdaten). Daraus geschnittene
  Fixtures liegen in `fixtures/` (im Git) und laufen als Tests.

## Umgebung und Client-Version

| Was | Wert | Stand |
|---|---|---|
| Client | Steam, AppID 3393110, **buildid 25800038**, `LastUpdated` 1791530351 (≈ 2026-10-09) | 2026-10-13 |
| Pfad | `<Steam-Bibliothek>\steamapps\common\AION2\Aion2\Binaries\Win64\AION2.exe` | 2026-10-13 |
| Prozesse | zwei `AION2.exe` (Spiel ~5,6 GB RAM, Helfer ~66 MB), untereinander verbunden über `127.0.0.1:51484 ↔ 51485` | 2026-10-13 |
| Spielverbindung | `<LAN-IP>:<Port> → 193.202.112.106:13328`, direkt über den WLAN-Adapter, **kein** Loopback/VPN | 2026-10-13 |
| Npcap | 1.84, `AdminOnly=0` (läuft ohne Admin), `LoopbackSupport=1` | 2026-10-13 |

## BELEGT

Quelle für alles in diesem Abschnitt: `captures/20261013_112703` (24,8 s live, Charakter im Kampf, Map unbekannt,
Build 25800038). Der Mitschnitt begann mitten im Stream, ohne SYN.

### B1. Transport und Richtung (2026-10-13)
- Der Server sendet von TCP-Port **13328**. Der Server→Client-Stream ist unverschlüsselt und direkt dekodierbar.
- Die Flow-Erkennung über Heartbeats (≥ 3× `0E 00 36` auf Port 13328) hat den Stream sofort erkannt.

### B2. Framing (2026-10-13)
`varint len, [Flag 0xF0..0xFE], payload` mit Größe auf der Leitung = `len + varint-Bytes − 4`.
- Beleg: 1.845 Frames, **0 Resyncs**, 0 kaputte Bundles, 0 TCP-Lücken.
- Zwei unabhängige Decoder liefern **Frame für Frame identische** Ergebnisse (Opcode, Herkunft, Pfad, Flag, Body):
  C# (`FrameDecoder`, auf Basis von cyberbadger) und Python (`tools/a2wire.py`, eigenständig geschrieben).
  Prüfen mit `tools/crosscheck.py`.
- **1-Byte-Varint** (Heartbeat): `0e 00 36 b9 38 96 34 a1 01 00 00` → 14 + 1 − 4 = 11 Byte.
- **2-Byte-Varint** (LZ4-Bundle): `bd 03 ff ff ba 02 00 00 e0 0f …` → len 445, 445 + 2 − 4 = 443 Byte
  = 2 (Varint) + 2 (`FF FF`) + 4 (raw_size 698) + 435 (LZ4-Block). Normale Frames mit 2-Byte-Varint kamen in diesem
  Mitschnitt nicht vor (größter Frame: 106 Byte).
- Flag-Byte `F0..FE`: kam in diesem Mitschnitt **nicht** vor. Der Decoder unterstützt es trotzdem.
- `00`-Padding zwischen Frames: wird übersprungen.
- Beim Einstieg mitten im Stream wurden genau 15 Byte bis zum ersten Heartbeat verworfen. Das ist erwartet.

### B3. Heartbeat `00 36` (2026-10-13)
- Body: u64 Server-Unix-ms. Beispiel `b9 38 96 34 a1 01 00 00` = 1791883622585 = 2026-10-13 09:27:02.585 UTC.
- Rate **20,00/s**, also ein Heartbeat alle 50 ms. Die Spanne der Serveruhr (24,8 s) entspricht der Spanne der
  Capture-Uhr (24,8 s).

### B4. LZ4-Bundles (2026-10-13)
- Payload: `FF FF, u32 raw_size, LZ4-Block` im Block-Format, ohne Frame-Header. Dekodiert ergibt sich exakt `raw_size`
  (698 Byte), darin 31 innere Frames (u. a. `84 56`, `04 38`, `a6 ff`, `1e 56`, `04 8d`). Verschachtelungstiefe 1.
- Bundles kommen selten: 1 Bundle in 24,8 s. Eingebettete Bundles in Packet-Bodies: 0.

## BEOBACHTET (strukturell, noch nicht gezielt geprüft)

Gleicher Mitschnitt `captures/20261013_112703`.

### O1. Objekt-Spawn `34 36` passt zum Layout aus Cube Watch
15 Frames, alle mit `id varint, 2 Bytes, template u32, x y z f32` und plausiblen Koordinaten. Beispiel (Wire-Hex):
```
3d 34 36 e1 e4 02 04 00 89 e3 16 00 19 05 47 46 42 ea 96 c7 00 c0 a2 44 00 00 00 00 00 c0 28 43
01 01 01 a8 9e 8e 34 a1 01 00 00 00 00 00 00 00 00 00 00 00 93 5b 72 31 07 00 00
```
- `3d` → 58 Byte, id `e1 e4 02` = 45665, `04 00`, Template `89 e3 16 00` = 1500041,
  x/y/z = 12737,3 / −77268,5 / 1302,0, dann f32 0,0 und f32 168,75 (Heading?).
- Danach `01 01 01` und ein **u64, der ein Unix-ms-Zeitstempel ist** (`a8 9e 8e 34 a1 01 00 00` = 1791883124392,
  499 s vor dem Empfang). Ob das der Erzeugungszeitpunkt ist, bleibt eine HYPOTHESE. Dafür spricht:
  - Alle statischen Templates (1200611, 1200860, 1100001) tragen denselben Zeitpunkt, 98,28 h vor dem Empfang
    (≈ 1791529839 s). Das passt zum Steam-Update / Serverstart am 2026-10-09.
  - Die Templates 1500000/1500012/1500041 haben Zeitpunkte zwischen 90 s und 2 h vor dem Empfang, also wohl
    respawnende Objekte (Sammelpunkte?).
  - **Falls das für Cubes gilt, liefert das Paket das Alter eines Cubes.**
- 2-Byte-Feld nach der id: meist `04 00`, einmal `0c 00` (Template 1200860), einmal `01 00` (1100001).
- Gesehene Templates: 1200611 (7×), 1500041 (3×), 1500000 (2×), 1500012, 1200860, 1100001. Das Cube-Template
  1301199 kam **nicht** vor (Standort vermutlich keine Cube-Zone).

### O2. Objekt-Entfernung `35 36`
`0c 35 36 97 ff 03 01 01 00` → id 65431, Grund `01`, dann `01 00`. 14× Grund `01`, 1× Grund `04 03 00`.
- Objekte mit Grund `01` waren zuvor per `34 36` gekommen. Das passt zu „verlässt Sichtbereich“, während der Charakter
  sich bewegt.

### O3. Interaktion `38 36` → Entfernung mit Grund `04`
`0c 38 36 a3 d8 01 d6 19 01` → Objekt 27683, Akteur 3286, `01`. Dasselbe Objekt 27683 wurde später per `35 36` mit
Grund **`04`** entfernt. Das passt zu Cube Watchs Lesart „geöffnet/zerstört“. Das Objekt selbst kam vor
Mitschnittbeginn (Template unbekannt). Akteur 3286 ist vermutlich der eigene Charakter, aber unbestätigt, weil
`33 36` im Mitschnitt fehlte.

### O4. NPC-Spawn `41 36` passt zum Layout von TheLoop
```
6d 41 36 96 86 02 0c 20 00 35 0b 20 00 00 02 00 99 40 46 33 51 a6 c7 00 00 ee 43 71 4f 93 43
82 d1 01 b0 bd 10 b0 bd 10 64 00 00 00 …
```
- id 33558, Kind `0c` (NPC), npc u32 `35 0b 20 00` = 2100021, m1 `00`, m2 `02`. Der Community-Marker `00 (00|40) 02`
  besteht also aus dem höchsten Byte des npc-u32 plus m1 und m2.
- x/y/z = 12326,3 / −85154,4 / 476,0, Heading 294,6, dann 2 Byte, dann HP `b0 bd 10` = 270000, zweimal (cur/max).
- NPCs und Objekte liegen im selben Koordinatenraum (x 5000…31000, y −77000…−107000, z 300…4000).

### O5. Häufige Opcodes, die noch keiner benennt
`1d 37` (14/s, 10–12 Byte) und `1c 37` (3/s, 21 Byte) sind laut HamMeter-Doku Bewegung. `1b 56`, `84 56`, `3b 38`,
`3d 38` (2 Byte), `01 38` (7 Byte), `2c 38`, `35 38` sind unbekannt. Census: `captures/20261013_112703/census.txt`.

## COMMUNITY / HYPOTHESEN (Stand 2026-10-13, Quellen unten)

| Opcode | Bedeutung | Layout | Quelle | Status |
|---|---|---|---|---|
| `34 36` | Objekt-Spawn, **Hidden Cube = Template `0x0013DACF` (1301199)** | id varint, 2 B, template u32, x y z f32, … | Cube Watch (README, `proto/proto.v`, Testvektoren aus echten Captures) | Layout beobachtet (O1), Cube-Template HYPOTHESE |
| `35 36` | Objekt entfernt: `04` zerstört/geöffnet, `01` außer Sicht | id varint, reason u8 | Cube Watch | beobachtet (O2/O3) |
| `38 36` / `3a 36` | Öffnen beginnt / ist fertig | objekt-id varint, spieler-id varint, … | Cube Watch | `38 36` beobachtet (O3) |
| `1e 56` | Loot aus geöffnetem Objekt | …, objekt-id u32, objekt-xyz, drop-xyz | Cube Watch | gesehen, Layout ungeprüft |
| `2a 38` / `2b 38` | eigene Position (bei anderen Repos „Buff“): id, …, id, `01 00`/`01 02`, x y z | | Cube Watch; NOIA2/Kuroukihime nennen es Buff | HYPOTHESE |
| `1a 37` | Entity-Position: id, 2 B, x y z | | HamMeter `main`, Cube Watch | HYPOTHESE |
| `1b 37` | Bewegung: id, flags u8, x y z | | HamMeter `main` | HYPOTHESE |
| `02 38` kind 2 | Cast mit Heading + Caster-xyz | | TheLoop (Fixture `wb-cast-kind2-position`) | COMMUNITY |
| `21 36` | Map-Load: count u32, map u32 (+ instanz, 0, x y z heading laut TheLoop-Encoder) | | cyberbadger, chen-zilei, TheLoop | COMMUNITY |
| `41 36` | NPC-Spawn, kind `0c/0d` NPC, `04/05` „andere Objekte“, `1c/1d/1f/5d/5f` Summons | | alle Meter; Kinds laut HamMeter-Doku | Layout beobachtet (O4) |
| `42 36` | Tod/Entfernt: id, 0, flag (1 tot geladen/entfernt, 3 im Kampf gestorben, 7 aus der Welt) | | cyberbadger, HamMeter-Doku | COMMUNITY |

**Sichtweite (Interest-Radius):** Cube Watch: Cubes werden ab „etwa 15.000 Einheiten“ angekündigt. Bei 1 Einheit = 1 cm
wären das 150 m. Das ist eine HYPOTHESE und wird in Phase 1 gemessen.

**Kartenachsen (Cube Watch):** Norden = Welt −Y, Osten = Welt +X. Ebenfalls eine HYPOTHESE.

**Spielmechanik laut Recherche:**
- Zahlen von metabot.gg (nach eigener Angabe aus den Global-Client-Tabellen, Build 0.0.4387.0): 206 Cube-Gruppen mit
  3.722 möglichen Spots.
  - Verteron: 95 Gruppen, 1.440 Spots
  - Altgard: 59 Gruppen, 1.371 Spots
  - Eltnen: 26 Gruppen, 456 Spots
  - Morheim: 26 Gruppen, 455 Spots
- Pro Gruppe ist zur selben Zeit höchstens ein Cube aktiv (th.gl). Ein geöffneter Cube spawnt neu, ohne festen Timer.
  Das KR-Patch vom 2026-01-14 hat eine Spawn-Verzögerung eingeführt.
- Schlüssel werden beim Öffnen verbraucht. Cubes erscheinen nicht auf der Minimap. Es gibt einen Schimmer-Sound.
- Viele Spots liegen unter Wasser.

## Kernfragen: Status

| # | Frage | Stand | Nächster Schritt |
|---|---|---|---|
| 1 | Kommen Cubes als Entity? | HYPOTHESE `34 36` Template 1301199. Das `34 36`-Layout ist an eigenen Daten bestätigt. | Phase 1: Aufnahme in Verteron/Altgard mit Markern |
| 2 | Sende-Radius | HYPOTHESE ~15.000 Einheiten. Objekte kommen und gehen beim Laufen (O2). | Phase 1: Distanz bei erstem `34 36` bzw. bei `35 36` Grund `01` gegen die Eigenposition |
| 3 | Despawn/Geöffnet | `35 36` Grund `04` nach `38 36` beobachtet (O3) | Phase 1: Marker „geöffnet“ |
| 4 | Eigene Position | Kandidaten `2a 38`/`2b 38`, `1a 37`, `1b 37`, `02 38` kind 2, `21 36`. Alle außer `21 36` kamen im Mitschnitt vor. | Phase 2 |
| 5 | Zonenweiter Hinweis | Nichts gefunden (Recherche) | In Phase 1 mitprüfen |

**Abbruchrisiko (Phase 1):** Gering. Cube Watch meldet Cubes aus ~15.000 Einheiten Entfernung, weit jenseits der
Interaktionsreichweite. Das ist an unseren Daten aber noch nicht belegt.

## Quellen

- Referenz-Repos in `ref/` (nur gelesen, Commit-Stand vom 2026-10-13):
  - cyberbadger6969/aion2-dps-meter `414de5c`: Basis unseres Decoders, GPL-3.0
  - chen-zilei/aion2-meter `6123e44`
  - TheLoop705/aion2dps `97cc198`: einziges Repo mit echten Spawn-Fixtures inkl. xyz
  - domel1998/Aion2DPSMeter `20ba3c3`: `opcodes.json`
  - ZDYoung0519/NOIA2 `00fa903`; rrew22802-del/aion2-dps `d9f1d98` (Fork von NOIA2)
  - Kuroukihime/AIon2-Dps-Meter `42b0ce1`
  - NennMichSchinken/HamMeter-Aion2 `bda574d`: `main` ist neuer als PR #1 und dokumentiert `1a 37`/`1b 37`, `1c 92` und die Spawn-Kinds
  - **ayi-afk/aion2cubedetector („Cube Watch“) `a699221`**: passiver Cube-Detektor in V. **Keine Lizenz**, daher
    übernehmen wir keinen Code, nur Protokoll-Fakten als Hypothesen. Testvektoren in `proto/proto_test.v`.
- Spot-Daten: th.gl-Node-Dateien, von der Recherche dekodiert zu `data/external/hidden_cube_spots_thgl.csv`.
  - 3.722 Spots, Achsen bereits in Paket-Reihenfolge.
  - Lizenz unklar, deshalb nicht im Git.
  - Ein Cube-Watch-Vektor (−70398,0 / −93147,4) trifft den th.gl-Spot `hidden_cube@-93147:-70398` exakt.
- Mechanik: metabot.gg/en/aion-2/hidden-cubes; aion2.th.gl/guides/Hidden Cube; Destructoid (2026-10-06); games.gg
  (2026-10-07); KR-Patchnotes vom 2026-01-14 (über ruliweb).
- ToS: The Completionist (Kloggzer) hat in v2.0.0 (2026-10-08) Radar und Live-Cubes entfernt. Grund laut Autor:
  NCSOFT-Support habe Software, die Spieldaten abfängt oder analysiert, als ToS-Verstoß bezeichnet. Das ist eine
  Angabe des Autors, keine NCSOFT-Veröffentlichung.

## Changelog

- **2026-10-13 (Phase 0):** Setup, Recherche, `cuberadar dump` (live/pcapng), unabhängiger Python-Decoder, erste
  Live-Aufnahme `20261013_112703`. Belege B1–B4, Beobachtungen O1–O5, Fixtures `2026-10-13_bundle_objects` und
  `2026-10-13_spawns`.
