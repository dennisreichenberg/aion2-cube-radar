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

### B5. Hidden Cube = Objekt-Spawn `34 36` mit Template **1200039** (Verteron) (2026-10-13)
Quelle: `captures/20261013_114345` (317 s, Verteron, Build 25800038, 0 Resyncs/Lücken). Du hast zwei Cubes mit Key
geöffnet. Fixture: `fixtures/cubes/2026-10-13_verteron_two_cubes.json`, Test `CubeFixtureTests`.

```
cube 1: e5 ff 03 | 04 00 | a7 4f 12 00 | 00 1d 44 c7  80 a5 f6 47  00 64 71 46 | 00 00 00 00 | 00 00 00 00 | 01 01 01 |
        aa e4 a5 34 a1 01 00 00 | 00 x10 | 2c 4d 45 02 07 00 00
cube 2: 86 8d 02 | 04 00 | a7 4f 12 00 | a0 b4 47 c7  b2 6c 03 48  00 de 96 46 | 00 00 00 00 | 00 24 90 43 | 01 01 01 |
        4a b6 a7 34 a1 01 00 00 | 00 x10 | 2c 4d 45 02 07 00 00
```
(Body ohne Opcode.) Layout: `id varint | kind u16 (04 00) | template u32 | x y z f32 | f32 | heading f32 |
3 Byte | created u64 ms | …`.
- Cube 1: id 65509, (−50205,0 / 126283,0 / 15449,0), Heading 0. Cube 2: id 34438, (−51124,6 / 134578,8 / 19311,0),
  Heading 288,3.
- **Unabhängige Bestätigung:** Beide Positionen liegen in x/y **exakt** (0 Einheiten Abstand) auf th.gl-Cube-Spots in
  Verteron: `hidden_cube@126283:-50205` und `hidden_cube@134579:-51125` (z liegt 47 bzw. 21 Einheiten über dem Spot).
  Kein Objekt anderer Templates kommt in den drei Mitschnitten näher als 460 Einheiten an einen der 3.722 Spots.
- Das Cube-Watch-Template 1301199 (`0x0013DACF`) kam in keinem Mitschnitt vor. Es gehört laut th.gl-Abgleich zu Altgard.
  **Die Cube-Templates unterscheiden sich also je Zone.** Eltnen und Morheim sind noch unbekannt (`config/cubes.json`).
- `created` (Hypothese aus O1): Cube 1 entstand 161 s und Cube 2 54 s vor der ersten Sichtung. Beide existierten
  also schon, als sie gemeldet wurden. Ob der Zeitstempel wirklich den Respawn markiert, ist noch offen.

### B6. Öffnen und Entfernen eines Cubes (2026-10-13, 2×)
```
38 36  e5 ff 03 dc 47 01                         Öffnen beginnt: Objekt 65509, Akteur 9180 (= eigener Charakter), 01
3a 36  e5 ff 03 dc 47 01 00 00 00 23 c5 9a 25    fertig nach 4,25 s: count 1, item 0x259AC523
1e 56  (im LZ4-Bundle, +46 ms) 01 e5 ff 00 00 | Objekt-xyz | Drop-xyz | … | item u32 | count u32 | …
35 36  e5 ff 03 04 03 00                         entfernt, Grund 04
1f 56  (im LZ4-Bundle, +1 s) 01 e5 ff 00 00 | u32 | drop-id u32 | …
```
- Beim zweiten Cube ist der Ablauf identisch (4,25 s; dasselbe `01 00 00 00 23 c5 9a 25`).
- Der `3a 36`-Abschluss `01 00 00 00 23 c5 9a 25` ist byte-identisch mit Cube Watchs echtem Cube-Vektor (Altgard).
  Andere geöffnete Objekte (Sammelpunkte) enden auf `00 …` bzw. `01/02/03 00 00 00 00 00 00 00`. HYPOTHESE: count 1 +
  Item 0x259AC523 ist der verbrauchte Hidden Cube Key.
- Loot `1e 56`: 7 bzw. 9 Einträge mit Cube-ID und Cube-Position. Unter den Mengen sind **1000, 5, 120 und 10**. Das
  passt zu den laut Recherche garantierten Belohnungen (1.000 Seelenkristalle, 5 Soul Codex, 100–200 Power Shards,
  10 Silentium).

### B7. Eigene Position über `2a 38` (2026-10-13)
- Regel (Cube Watch): Der Body endet mit `entity varint (= die erste id) | 01 00 | x y z f32`. In der Sitzung gab es
  26 Treffer für die eigene id 9180, also etwa einen alle 2–15 s.
- Beleg: Beim Öffnen der Cubes lag die letzte Positionsangabe davor **195 bzw. 234 Einheiten** vom Cube entfernt
  (2,7 bzw. 1,3 s vor Öffnungsbeginn). Bei 1 Einheit = 1 cm sind das etwa 2 m. Das passt zur Interaktionsreichweite
  und stützt die Einheit cm.
- Die Regel trifft gelegentlich auch auf fremde ids zu (5471 2×, 12390 3×). Die eigene id ist die mit den meisten
  Treffern, besser noch die aus `33 36`.
- Die eigene Entity-ID war in allen drei Sitzungen 9180 (11:27, 11:41, 11:43 Uhr).

### B8. Sende-Radius für Cubes: ≈ 9.700–10.500 Einheiten in der Ebene (≈ 100 m) (2026-10-13, 4 Messungen)
Nachtrag aus `radar_20261013_122710`:
- Cube 52651: Die eigene Position stammt von 0,1 s vor der Meldung, Abstand **9.689**.
- Cube 37048: interpoliert **9.680**.

Ein Cube, der direkt in Sichtweite spawnt (B10), zählt nicht als Radius-Messung. Die erste Messreihe:
Die Position zum Zeitpunkt des ersten `34 36` ist linear zwischen den benachbarten `2a 38`-Angaben interpoliert
(`tools/radius.py`):

| Cube | erste Meldung | letzte Position davor | erste Position danach | interpoliert 2D / 3D |
|---|---|---|---|---|
| 65509 | 11:46:50.211 | −14,2 s: 13.454 | +0,2 s: 10.363 | **10.395** / 10.663 |
| 34438 | 11:47:02.412 | −2,5 s: 13.738 | +1,8 s: 8.602 | **10.548** / 11.178 |

- Die 2D-Werte liegen enger beieinander als die 3D-Werte. Vermutlich prüft der Server also eine planare Distanz.
- Andere Objekte haben eigene Radien, je Template unterschiedlich (Median Eintritt/Austritt):
  - viele 12xxxxx und 1500004: ≈ 5.000 / 6.500
  - 1500000: ≈ 10.300 / 11.900 (dieselbe Klasse wie die Cubes)
  - 1100001: ≈ 24.000 / –
  
  Cube Watchs „~15.000“ passt nicht zu unseren Werten.
- Offen: der Austrittsradius bei Cubes (`35 36` Grund `01`). Es gab noch keine Cube-Sichtung ohne Öffnen.

### B9. Zweite Cube-Variante in Verteron: Template **1200041** (2026-10-13)
Quelle: `captures/radar_20261013_122710` (665 s, Verteron, Build 25800038). Fixture:
`fixtures/cubes/2026-10-13_verteron_1200041_teleport.json`, Test `TeleportFixtureTests`.
```
34 36  c3 af 03 | 04 00 | a9 4f 12 00 | 23 99 8a 47 d3 e5 c1 47 …   id 55235, template 1200041, (70962, 99276, 8796)
38 36  c3 af 03 dc 47 01                                            Öffnen durch uns (9180)
3a 36  c3 af 03 dc 47 01 00 00 00 23 c5 9a 25                       fertig: count 1, item 0x259AC523 (wie B6)
35 36  c3 af 03 04 03 00                                            entfernt, Grund 04
```
- Die Position stimmt **exakt** (0 Einheiten, auch z) mit dem th.gl-Spot `hidden_cube@99276:70962` überein.
- Die Cube-Templates unterscheiden sich also auch innerhalb einer Zone (1200039, 1200041). Das Radar lernt
  unbekannte Varianten zur Laufzeit. Das hat es live per Spot-Treffer getan, bevor 1200041 in `config/cubes.json`
  stand.

### B10. `created` im Spawn ist der Spawn-Zeitpunkt (2026-10-13)
Cube 55235 hat `created` = 12:36:09.792. Er kam 1 s später bei uns an, als wir 46 m entfernt standen. Hätte er
vorher existiert, wäre er schon früher gesendet worden, weil wir in Sichtweite waren. Die anderen vier Cubes waren bei
der ersten Sichtung 54–161 s alt. Damals standen wir 112–589 m entfernt, sie existierten also schon. Das Radar zeigt
deshalb „existiert seit“.

### B11. Ein anderer Spieler öffnet einen Cube vor uns (2026-10-13)
Cube 52651 tauchte bei 97 m Entfernung auf. 6,5 s später begann Spieler 9019 ihn zu öffnen (`38 36` → `3a 36` mit
Key-Item → `35 36` Grund `04`). Das Radar meldete korrekt „geöffnet (von Spieler 9019)“.

### B12. Frisch gespawnte Cubes kommen sofort, auch im Stillstand, und sind hart umkämpft (2026-10-13)
Quelle: Kopie von `captures/radar_20261013_125039` (30 min, 0 Resyncs/Lücken). Du standest weitgehend still.

| Cube | `created` | erstes `34 36` | Abstand zu dir | geöffnet von | nach Spawn |
|---|---|---|---|---|---|
| 36443 (1200039) | 13:05:08 | 13:05:09 | 81 m | uns (9180) | 27 s |
| 64044 (1200039) | 13:08:24 | 13:08:25 | 89 m | Spieler 5709 | 18 s |
| 24285 (1200039) | 13:18:44 | 13:18:45 | 82 m | Spieler 3612 | 20 s |

- Ein Cube, der innerhalb der Sichtweite spawnt, wird 1 s nach dem Spawn gesendet, auch wenn man sich nicht bewegt.
  Das Radar (Replay derselben Daten) meldete alle drei sofort.
- Spieler 3612 stand 1 s nach dem Spawn **94 m** vom Cube entfernt und hatte ihn 20 s später offen. Er ist also ohne
  Zögern hingelaufen. Das deutet darauf hin, dass auch andere solche Werkzeuge nutzen. Belegen lässt sich das nicht.
- Zur Frage, ob AFK-Stillstand die Erkennung verhindert: **nein**. Die beiden Cubes wurden nicht übersehen, sie
  wurden in 18–20 s von anderen geöffnet.

## Phase 2: Eigene Position (BELEGT, 2026-10-13)

Ausgewertet wurden alle drei Mitschnitte (`112703` Kampf 25 s, `114103` 111 s, `114345` Verteron 317 s), eigene
Entity 9180. Werkzeuge: `tools/selfscan.py`, `tools/selftrack.py` und `tools/ringfix.py`. Parser:
`PositionMessages` (C#). Fixture: `fixtures/selfpos/2026-10-13_position_sources.json`, inklusive Negativfällen.

### P1. Der Server schickt die eigene Bewegung nicht zurück
Der f32-Triplet-Scan lief über jeden Frame, jeden Opcode und jeden Offset, auch in Bundles, abgeglichen mit der
Referenzspur aus `2a 38`. Er findet in `1a 37`, `1b 37`, `1c 37` und `29 37` nur Positionen **anderer** Entities:
Mobs, einen anderen Spieler (id 1202) und einen Summon (26082). Die eigene id 9180 steht dort nie im ersten Feld.
`29 37` ist eine Bewegung mit Geschwindigkeitsvektor: `id, flags (u8, ungerade → +1 Byte), x y z, vx vy vz, u16`.

### P2. Vier Quellen für die eigene Position, alle ereignisgebunden

| Opcode | Layout | Position von | Beispiel (Body-Hex, 112703) |
|---|---|---|---|
| `2a 38` | …, Caster varint, `01 00`, x y z | Caster | `dc 47 01 11 90 03 e8 03 … 23 51 96 34 a1 01 00 00 dc 47 01 00 b3 98 4a 46 a2 79 9f c7 51 3e 59 44` → (12966, −81651, 869) |
| `2b 38` | …, u64 Zeit, Caster varint, u8, u32, `02`, x y z | Caster | `dc 47 13 84 03 … 28 61 96 34 a1 01 00 00 dc 47 01 17 1d d2 00 02 e7 40 4a 46 95 a1 9f c7 00 40 59 44` → (12944, −81731, 869) |
| `02 38` kind 2 | Akteur varint, flag varint, Skill u32, seq u8, `02`, Ziel varint, Heading f32, x y z | **Ziel** | Mob 40714 castet auf uns: `8a be 02 00 38 9c 12 00 01 02 dc 47 97 a9 66 c1 e7 40 4a 46 95 a1 9f c7 00 40 59 44 …` → (12944, −81731, 869), identisch mit `2b 38` |
| `0e 38` | Entity varint, `02`, Ziel varint, u32, u8, u32, x y z, … | Ziel | `8a be 02 02 dc 47 38 9c 12 00 01 b1 b4 e8 05 36 dc 49 46 dd 93 9f c7 00 c0 7a 44 …` → (12919, −81704, 1003) |

- **`02 38` kind 2 trägt die Position des ZIELS.** Der Median-Abstand zur bekannten Zielposition ist 0 Einheiten,
  zum Caster 160–307 (in allen drei Sitzungen). TheLoops „Caster-xyz“ ist damit widerlegt.
  - Eigene Position aus einem Cast gibt es also nur bei Self-Casts und wenn ein Mob dich angreift.
  - Einige Skills tragen statt xyz kleine Werte (32-Byte-Variante, z. B. −108/−31/−95). Die verwirft die
    Plausibilitätsprüfung (Abstand vom Ursprung > 2.000).
- `2a 38`/`2b 38`: Ziel und Caster sind fast immer gleich (Self-Buffs). In den 5 Fällen mit verschiedenem Ziel passt
  xyz zum Caster (Median 0, zum Ziel 24).
- **Übereinstimmung der Quellen untereinander** (gleiches Zeitfenster ≤ 1 s, planar): `02 38`/`2b 38` Median 0,
  `0e 38`/`2b 38` 37, `2a 38`/`2b 38` 83–159. Das sind alles < 2 m.
- `21 36` (Map-Load) und `23 36` (Teleport) kamen in keinem Mitschnitt vor. Ob sie xyz tragen, bleibt COMMUNITY.

### P3. Dichte: gut im Kampf, dünn beim reinen Laufen
| Sitzung | Fixes | Median-Abstand | 90 % | max. Lücke | Zeit in Lücken > 10 s |
|---|---|---|---|---|---|
| 112703 (Kampf) | 39 / 25 s | 0,3 s | 1,9 s | 2,8 s | 0 s |
| 114103 | 29 / 111 s | 2,2 s | 10,7 s | 15,1 s | 39 s |
| 114345 (Cube-Suche) | 39 / 317 s | 3,9 s | 22,6 s | 45,5 s | **206 s** |

### P4. Plausibilität der Spur
- Geschwindigkeit zwischen Fixes: Median 3,8–6,9 m/s, 90 % ≤ 23 m/s, Maximum 30,9 m/s (bei 1 Einheit = 1 cm). Es gibt
  keine Sprünge über 40 m/s.
- Beim Öffnen der Cubes 195 bzw. 234 Einheiten vom Cube entfernt (B7).
- Spuren als Bild: `captures/<sitzung>/selftrack.svg` (Norden oben = Welt −Y laut Cube Watch).

### P5. Fallback-Heuristik „Ring-Fix“
Objekte erscheinen (`34 36`) und verschwinden (`35 36` Grund `01`) auf template-typischen Radien (B8). Jedes solche
Ereignis legt die eigene Position auf einen Kreis um das Objekt. Mindestens 3 Ereignisse innerhalb von 2 s lassen
sich per Kleinste-Quadrate kombinieren.
- Fehler gegen echte Fixes: Median **307 / 1.082 / 716 Einheiten** (3–11 m). 90 % liegen bei 401 / 9.394 / 2.995
  (Sitzungen 112703 / 114103 / 114345).
- Einschränkung: Die Radien sind aus derselben Sitzung gelernt. Das Radar braucht dafür eine wachsende Radientabelle
  je Template.

### P7. Map-Load `21 36` und Teleport `23 36` tragen die eigene Position (2026-10-13, 6×)
```
21 36  0b 00 00 00 | f2 03 00 00 | 3a 03 87 00 | 00 00 00 00 | 58 72 74 c7 1c 5e a7 47 00 bc 4e 46 | 80 e3 33 43 | 02 00 …
       load count    map 1010      u32           u32 0         x −62578  y 85692  z 13231            heading
23 36  00 00 00 00 00 00 | 58 72 74 c7 1c 5e a7 47 00 bc 4e 46 | 05 00 00 00 | a8 05     (≈ 3,5 s nach 21 36)
```
- Bei allen 6 Teleports (innerhalb von Map 1010) lag die nächste eigene Positionsmeldung **0 m** daneben.
- Map **1010 = Verteron**, eingetragen in `config/radar.json` unter `map_zones`.
- Ein Map-Load auf dieselbe Map ist ein Teleport innerhalb der Zone: Die Cubes bleiben.
- Nach einem Teleport entfernt der Server alle alten Objekte. Das verfälschte die Radius-Statistik (Austritte bei
  100.000+ Einheiten). Das Radar nimmt deshalb 6 s nach `21 36`/`23 36` keine Stichproben und verwirft solche über
  30.000 Einheiten.

### P6. Hypothese „Positions-Ping“
Jeder eigene Self-Buff erzeugt `2a 38`/`2b 38` mit der eigenen Position. Ein billiger Self-Buff auf einer Taste wäre
also ein Positions-Update auf Knopfdruck. Das ist ungetestet.

**Folgerung fürs Radar (Phase 3):**
- Die Cubes werden in absoluten Koordinaten auf einer Zonenkarte gezeigt. Das funktioniert ohne eigene Position.
- Entfernung und Richtung relativ zu dir gibt es nur mit frischem Fix. Das Alter des Fixes wird angezeigt.
- Ist kein frischer Fix da, zeigt das Radar den Ring-Fix mit Unsicherheitskreis.

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
Mitschnittbeginn (Template unbekannt). Akteur 3286 war **nicht** der eigene Charakter (eigene id = 9180, siehe B7),
sondern ein anderer Spieler. Bestätigt durch B6.

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
| `34 36` | Objekt-Spawn; Hidden Cube Verteron = Template 1200039, Altgard laut Cube Watch = 1301199 (`0x0013DACF`) | id varint, kind u16, template u32, x y z f32, f32, heading f32, 3 B, created u64, … | Cube Watch; eigene Mitschnitte | **BELEGT** für Verteron (B5); 1301199 HYPOTHESE |
| `35 36` | Objekt entfernt: `04` zerstört/geöffnet, `01` außer Sicht | id varint, reason u8 | Cube Watch; eigene Mitschnitte | `04` **BELEGT** für Cubes (B6), `01` bei anderen Objekten beobachtet |
| `38 36` / `3a 36` | Öffnen beginnt / ist fertig | objekt varint, akteur varint, `01` / count u32, item u32 | Cube Watch; eigene Mitschnitte | **BELEGT** (B6) |
| `1e 56` | Loot aus geöffnetem Objekt | `01`, objekt u32, objekt-xyz, drop-xyz, …, item u32, count u32, … | Cube Watch; eigene Mitschnitte | beobachtet (B6) |
| `2a 38` / `2b 38` | Buff-Records mit Caster-Position (Layouts siehe P2) | | Cube Watch; NOIA2/Kuroukihime nennen es Buff | **BELEGT** (B7, P2) |
| `1a 37` | Entity-Position: id, 2 B, x y z | | HamMeter `main`, Cube Watch | beobachtet für **fremde** Entities, nie die eigene (P1) |
| `1b 37` / `1c 37` / `29 37` | Bewegung: id, flags u8 (+1 B wenn ungerade), x y z (… `29 37`: + vx vy vz) | | HamMeter `main`; eigene Mitschnitte | beobachtet für **fremde** Entities, nie die eigene (P1) |
| `02 38` kind 2 | Cast mit Heading + xyz | | TheLoop („Caster-xyz“) | **BELEGT: xyz = Ziel**, nicht Caster (P2) |
| `21 36` | Map-Load: count u32, map u32 (+ instanz, 0, x y z heading laut TheLoop-Encoder) | | cyberbadger, chen-zilei, TheLoop | COMMUNITY |
| `41 36` | NPC-Spawn, kind `0c/0d` NPC, `04/05` „andere Objekte“, `1c/1d/1f/5d/5f` Summons | | alle Meter; Kinds laut HamMeter-Doku | Layout beobachtet (O4) |
| `42 36` | Tod/Entfernt: id, 0, flag (1 tot geladen/entfernt, 3 im Kampf gestorben, 7 aus der Welt) | | cyberbadger, HamMeter-Doku | COMMUNITY |

**Sichtweite (Interest-Radius):** Cube Watch nennt „etwa 15.000 Einheiten“. Unsere Messung (B8) ergibt ≈ 9.700–10.500 in der
Ebene.

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
| 1 | Kommen Cubes als Entity? | **Ja, BELEGT (Verteron, 5 Cubes):** `34 36`, Templates 1200039 und 1200041 (B5, B9). Templates je Zone und Variante verschieden. | Altgard/Eltnen/Morheim-Templates sammeln |
| 2 | Sende-Radius | **≈ 9.700–10.500 Einheiten planar (≈ 100 m), 4 Messungen (B8)** | mehr Sichtungen; Austrittsradius (F7-Test) |
| 3 | Despawn/Geöffnet | **BELEGT (2×):** `38 36` → `3a 36` (+4,25 s) → `35 36` Grund `04` (B6) | Grund `01` bei Cubes (außer Sicht) prüfen; fremde Öffnungen |
| 4 | Eigene Position | **BELEGT (Phase 2):** `2a 38`, `2b 38`, `02 38` (wir als Ziel), `0e 38`, nur ereignisgebunden. Fallback Ring-Fix (P1–P6) | `21 36`/`23 36` bei Zonenwechsel prüfen; Positions-Ping testen |
| 5 | Zonenweiter Hinweis | Nichts gefunden (Recherche) | In Phase 1 mitprüfen |

**Abbruchkriterium:** greift **nicht**. Cubes kommen aus ≈ 100 m Entfernung über die Leitung (B8), weit jenseits der
Interaktionsreichweite (≈ 2 m, B7).

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
- **2026-10-13 (Phase 1, erste Aufnahme):** `20261013_114345` in Verteron, 2 Cubes geöffnet.
  - Belege B5–B8: Cube-Template 1200039, Öffnen und Entfernen, Eigenposition `2a 38`, Radius ≈ 10.500.
  - Neu: `config/cubes.json`, `ObjectMessages` (C#), Fixture `fixtures/cubes/2026-10-13_verteron_two_cubes.json`,
    `tools/objects.py`, `tools/radius.py`.
  - Der einzige Marker dieser Aufnahme war `cube_opened` (F6) um 11:47:10.8, nicht F5. Für die Auswertung war er
    nicht nötig.
- **2026-10-13 (Phase 1 abgeschlossen):** Abnahme auf Wunsch ohne weitere Aufnahme.
  - Offen und verschoben: Cube-Austritt `35 36` Grund `01`, mehr Radius-Messungen, Templates anderer Zonen. Das
    Radar sammelt diese Daten im Betrieb.
- **2026-10-13 (Phase 2):** Eigene Position aus 4 Quellen (P1–P6), `PositionMessages` (C#) und Fixture `selfpos`.
  - `02 38` kind 2 trägt die Zielposition. Damit ist TheLoops Lesart korrigiert.
  - Ring-Fix-Fallback. Neue Werkzeuge: `selfscan.py`, `selftrack.py`, `ringfix.py`.
- **2026-10-13 (Phase 3, gebaut):** `cuberadar radar` mit `RadarEngine`, SQLite-Spot-DB, Web-UI auf
  127.0.0.1:5313 (Radar, Karte, Liste, WebSocket) und Selbstcheck aus `config/radar.json`.
  - Template-Lernen über Key-Item und Spot-Treffer.
  - Ein Cube, der nicht wieder gesendet wird, obwohl man nah dran ist, gilt als „weg“.
  - Getestet per Replay von `20261013_114345` (beide Cubes, Status bis „geöffnet von dir“) und 20 s live.
  - Die Abnahme im Spiel steht aus. Offen ist dabei auch die Nordachse (−Y laut Cube Watch).
- **2026-10-13 (Phase 3 abgenommen):** Rückmeldung: „jeder Cube wurde gut erkannt und hat mir eine Warnung gegeben“.
  Sitzung `radar_20261013_122710`: 3 Cubes, einer von Spieler 9019 und zwei von dir geöffnet.
  - Neu: B9 (Template 1200041), B10 (`created` = Spawn), B11, P7 (eigene Position aus `21 36`/`23 36`, Map 1010).
  - Radar: Höhenanzeige (▲/■/▼, Höhenleiste, „über/unter dir“), Spur-Unterbrechung bei Teleports, Ruhezeit für
    Radius-Stichproben. Der erste Map-Load leert nur Cubes einer anderen Zone.
- **2026-10-13 (Radar 0.4.0):** Overlay über dem Spiel. Es ist ein kleines, klick-durchlässiges Fenster, immer im
  Vordergrund, und nimmt nie den Fokus (Strg+Umschalt+F10 ein/aus, F11 verschieben).
  - Die Nordrichtung liegt jetzt serverseitig (`POST /api/north`, `data/settings.json`) und gilt für Web-UI und
    Overlay gleichermaßen.
  - B12 ergänzt: Verpasste Cubes waren nicht unerkannt, andere Spieler öffneten sie nach 18–20 s.
- **2026-10-13 (Radar 0.4.1):** Das Overlay ist jetzt ein Mini-Radar.
  - Norden oben, du in der Mitte, Ringe, Sichtweite, deine Laufspur.
  - Cubes als ▲/▼/■ mit Entfernung und Höhe; außerhalb des Zooms ein »-Zeiger am Rand.
  - Zoom 60 / 120 / 250 m (Strg+Umschalt+F12), wird in `data/settings.json` gespeichert.
- **2026-10-13 (Radar 0.4.2):** Overlay-Verhalten wie bei anderen Spiel-Overlays.
  - Immer mit der Maus verschiebbar (F11-Entsperren entfällt), Zoom per Mausrad.
  - Nur sichtbar, solange Aion 2 das Vordergrundfenster ist.
  - Ausgeblendet, solange der Mauszeiger sichtbar ist (Heuristik für Menüs/UI-Modus, abschaltbar mit
    `OverlayHideInMenus`). Liegt der Zeiger über dem Overlay, bleibt es sichtbar, damit man es greifen kann.
  - Dafür wird nur der Windows-Fenster- und Cursorstatus gelesen. Ob das Spiel den Cursor in der Welt wirklich
    ausblendet, muss sich beim Spielen zeigen.
- **2026-10-13 (Radar 0.5.0):** Nur-Overlay-Modus (`radar --overlay-only`, `overlay.cmd`, im Paket „Nur Overlay
  starten.cmd“).
  - Kein Browser, die Konsole ist versteckt, die Ausgaben gehen nach `radar.log`.
  - Tray-Symbol mit Overlay ein/aus, Zoom, Web-Ansicht und Beenden. Das Tray-Symbol gibt es auch im normalen Modus.
- **2026-10-13 (Radar 0.6.0):** Einstellungsfenster, erreichbar über den ⚙-Knopf im Overlay und das Tray-Symbol.
  - Ton (an/aus, Test, eigene .wav), Zoom, Größe, Deckkraft, Norden, Sichtbarkeitsregeln, Position zurücksetzen,
    Web-Ansicht, Beenden.
  - Änderungen werden sofort angewendet und gespeichert.
  - Solange das eigene Einstellungsfenster vorne ist, bleibt das Overlay sichtbar.
- **2026-10-13 (Radar 0.7.0):** Einzelne Datei `Cube Radar.exe`.
  - Windows-App ohne Konsole, self-contained und komprimiert.
  - Web-Oberfläche und Standard-Konfiguration sind eingebettet und werden nach `%LOCALAPPDATA%\CubeRadar\app\<version>`
    entpackt; Daten liegen in `%LOCALAPPDATA%\CubeRadar`.
  - Ein Doppelklick startet das Overlay.
  - Fehler (Npcap fehlt, läuft bereits, Absturz) erscheinen als Hinweisfenster.
  - Eigenes Programm-Symbol; neuer Menüpunkt „Aufnahmen-Ordner öffnen“.
