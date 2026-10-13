# Projekt: Aion 2 „Hidden Cube Radar" (passiv, Npcap)

## Ziel
Ich spiele Aion 2 Global (Region WW). Ich will ein Windows-Tool, das den
Server→Client-Traffic von Aion 2 passiv mitliest und mir **live anzeigt, wo gerade Hidden Cubes stehen**
(Position, Entfernung und Richtung zu mir). Bestehende Tools (Shugo.GG App, NotMeter, diverse DPS-Meter) lesen auf
dieselbe Weise schon Monster in der Nähe aus den Paketen.

Spielmechanik laut Guides (verifizieren!): Hidden Cubes spawnen zufällig an einem von sehr vielen möglichen Spots pro
Zone (Verteron/Altgard, evtl. Eltnen/Morheim; die Angaben schwanken zwischen „172–275 Cubes" und „>1000 Spots pro
Zone"). Sie sind serverweit geteilt. Wird einer geöffnet, spawnt ein neuer an einem zufälligen Spot. Zum Öffnen braucht
man einen Hidden Cube Key, und viele Spots liegen unter Wasser. Statische Community-Karten (aion2hub.com,
allthings.how/aion-2/map/hidden-cubes/) zeigen deshalb nur mögliche Spots, nicht, wo gerade wirklich einer steht.
**Genau diese Lücke soll das Tool schließen.**

## Harte Regeln
- **Strikt passiv:** nur Npcap-Mitschnitt (wie Wireshark). Kein Memory-Reading, keine Injection, keine Pakete senden
  oder verändern, keine Änderungen am Spielclient oder an Spieldateien. Das ToS-Risiko ist mir bewusst und liegt bei
  mir.
- Alles aus der Community-Doku ist eine **Hypothese**, bis es an echten Mitschnitten von mir belegt ist. Nichts
  erfinden. Wenn sich etwas nicht belegen lässt, sag es klar.
- Du kannst das Spiel nicht selbst bedienen. Wenn du Daten brauchst, gib mir eine exakte, nummerierte
  In-Game-Anleitung: wohin laufen, wie lange stehen bleiben, wann ein Marker gesetzt wird und welches Kommando ich
  vorher starten soll.
- **Abbruchkriterium:** Kommt in Phase 1 heraus, dass Cubes gar nicht oder nur in Interaktionsreichweite über die
  Leitung kommen, dann STOPP. Berichte mit Belegen und schlag Alternativen vor, statt etwas Nutzloses zu bauen.
- Projektordner ist dieser Ordner. Referenz-Repos kommen nach `./ref/` (nur lesen, nicht blind ausführen).

## Umgebung (bereits geprüft am 13.10.2026)
- Windows 11 Pro. Npcap 1.84 ist installiert (`C:\Program Files\Npcap`), Wireshark nicht.
- Python 3.12.10 ohne Zusatzpakete (kein scapy, lz4, dpkt, numpy). Node.js und Git sind vorhanden.
- .NET: nur die Runtimes 6/8/10, **kein SDK**. Für C# vorher fragen und dann `winget install Microsoft.DotNet.SDK.10`.
- Das Spiel ist der Global-Client. Clients anderer Regionen (z.B. TW) ignorieren,
  außer ich sage etwas anderes.
- Passive Tools laufen bei mir bereits erfolgreich: Shugo.GG App 0.3.69 und NotMeter (`%LOCALAPPDATA%\Not4You-Meter`).
  Beide sind closed source und dienen nur als Beleg, dass das Mitlesen funktioniert. Mehrere Npcap-Captures parallel
  sind kein Problem.

## Bekannter Protokollstand (Community, Stand Oktober 2026, unbedingt verifizieren)
Quellen: `chen-zilei/aion2-meter` → `docs/PROTOCOL.md`; `cyberbadger6969/aion2-dps-meter` →
`src/AionMeter.Core/Protocol/{FrameDecoder,PacketParser,Opcodes}.cs`. Gilt für den Client nach dem Juni-2026-Patch,
mit dem Global gestartet ist.
- TCP, Server-Port **13328** (Cloudflare-Relay). Nur Server→Client ist dekodierbar, denn Opcode und Body von
  Client→Server sind verschlüsselt. Mit VPN oder Ping-Reducer läuft der Traffic über den Loopback-Adapter.
- Server→Client ist **unverschlüsselt**, aber teils **LZ4**-komprimiert.
- Framing: `varint len, [1 Byte in 0xF0..0xFE], payload`. Die Framegröße auf der Leitung ist `len + varint-Bytes − 4`.
  `payload` ist entweder `opcode (2 Byte) + body` oder `FF FF, u32 raw_size, LZ4-Block`. Der LZ4-Block enthält weitere
  Frames (rekursiv). `0x00` zwischen Frames ist Padding. Integers sind little-endian, meist LEB128-Varints.
- Heartbeat `00 36` + u64 Server-Unix-ms, etwa 20 pro Sekunde. Eignet sich zum Resync nach Paketverlust.
- Opcodes: `33 36` eigener Charakter · `45 36` anderer Spieler · **`41 36` Spawn (NPC/Summon/Effekt)** · `42 36` Tod ·
  `21 36` Map-Load (`load count u32, map id u32`) · `23 36` Teleport · `02 38` Cast (laut Kommentar mit Position nach
  dem Target) · `03 38` Hit-Notice (enthält `x y z f32`) · `04 38` Damage · `05 38` DoT · `01 91`
  Field-Boss-Liste (mit `x y z f32`) · `02 97` Party.
- **Spawn `41 36`:** `id varint` (IDs über 1.000.000 → `(raw & 0x3FFF) | 0x4000`), `mask u32` (erstes Byte = kind;
  `5F 1C 1F 1D 5D` = Summons/Pets/Effekte), variable Felder, `npc_code u24` direkt vor dem Marker
  `00 (00|40) 02`, danach **`x y z f32` in Zentimetern**, …, `01 cur_hp varint max_hp varint`.
- Patches verschieben Opcodes. Im Juni 2026 haben sich mehrere um 1 verschoben, und domel1998 löst das mit einer
  `opcodes.json`.

## Offene Kernfragen (das ist die eigentliche Arbeit)
1. Kommen Hidden Cubes überhaupt als Entity über die Leitung? Als `41 36` mit eigenem `npc_code`/kind oder über einen
   anderen Opcode (Objekt/Gadget/Interactable)?
2. Ab welcher Entfernung schickt der Server sie (Interest-Radius)? Kommen „hidden" Cubes erst, wenn man sehr nah ist?
3. Gibt es Despawn- oder „geöffnet"-Events, damit die Anzeige aktuell bleibt?
4. Wie bekomme ich meine **eigene Position** aus dem Server→Client-Traffic? Die eigene Bewegung geht verschlüsselt
   raus. Kandidaten: Cast und Hit-Notice eigener Skills, Teleport, Map-Load, Positionskorrekturen. Fallback:
   Schwerpunkt frisch gespawnter Entities.
5. Gibt es irgendeinen zoneweiten Hinweis auf Cubes (Systemnachricht, Event, Kartenmarker)?

## Vorgehen (Phasen mit Abnahmekriterien)

### Phase 0: Setup und Recherche (ohne Spiel, keine Rückfragen außer bei Installationen)
- Klone mit `--depth 1` nach `./ref/`:
  - `cyberbadger6969/aion2-dps-meter`: C#, GPL-3.0, aktiv gepflegt, Referenz-Decoder, CLI mit `--record`, Tests
  - `chen-zilei/aion2-meter`: Rust, `PROTOCOL.md`, `replay --opcodes`
  - `TheLoop705/aion2dps`: C#, CLI mit `census`, `replay`, `simulate`, `selftest`
  - `domel1998/Aion2DPSMeter`: C#, Opcode-JSON-Konzept
  - `ZDYoung0519/NOIA2` und `rrew22802-del/aion2-dps`: Rust/Tauri, Opcode-Census
  - `Kuroukihime/AIon2-Dps-Meter`: C#
  - `NennMichSchinken/HamMeter-Aion2` (PR #1: eigener Packet-Reader)
- Lies dort Flow-Erkennung, TCP-Reassembly, Resync, LZ4-Bundles und den Spawn-Parser.
- Suche nach weiteren Quellen zu Objekt- und Gadget-Spawns sowie zu Cubes: GitHub, inven.co.kr, Reddit, Suchbegriffe
  „히든 큐브", „숨겨진 큐브", „hidden cube packet", „gather node".
- Wähle den Stack und begründe die Wahl. Meine Vorgabe: den Decoder nicht neu erfinden. Bei C# baust du auf
  `AionMeter.Core` von cyberbadger auf (GPL-3.0, für privaten Gebrauch ok). Analyse-Skripte gerne in Python.
- **Abnahme:** Ein `dump`-Tool schreibt live und aus `.pcapng` jeden dekodierten Server→Client-Frame als JSONL:
  Zeitstempel, Opcode, Länge, Hex-Body, Herkunft (Top-Level oder aus einem LZ4-Bundle, mit Verschachtelung). Parallel
  speichert es einen rohen `.pcapng` für Replays. Dazu kommen **Marker**: Enter in der Konsole plus optionaler Text,
  besser noch ein globaler Hotkey, damit ich im Spiel „Cube sichtbar", „Cube geöffnet" usw. markieren kann. Den
  Selbsttest machst du an einem kurzen Mitschnitt von mir: Der Census ist plausibel (Heartbeat ~20/s, Spawns beim
  Laufen) und Resyncs bzw. Decode-Fehler bleiben nahe 0.

### Phase 1: Cube identifizieren (mit mir im Spiel)
- Gib mir ein Messprotokoll, zum Beispiel: (1) Aufnahme an einem Ort mit vielen Spots starten (Verteron,
  Ellun River Midstream; Altgard, Fafnite Deposit), (2) Marker setzen, sobald ich einen Cube sehe, (3) mit Key öffnen
  und Marker setzen, (4) weglaufen, bis er außer Sicht ist, Marker setzen und zurückkommen.
- Analysetechniken:
  - **Generischer f32-Triplet-Scanner:** Durchsuche jeden Payload nach drei aufeinanderfolgenden plausiblen `f32`,
    auch bei unbekannten Opcodes und in LZ4-Bundles. Den Wertebereich in Zentimetern leitest du aus bekannten
    Spawn-Koordinaten der aktuellen Map ab. Ergebnis: eine Liste aller Dinge mit Position, Opcode und umgebenden Bytes.
  - **Clustering** nach (Opcode, kind-Byte, `npc_code`): Gesucht sind seltene, unbewegliche Entitäten ohne Kampf und
    ohne HP-Änderung, die am „geöffnet"-Marker ein Despawn- oder Status-Event bekommen.
  - **Differential-Analyse:** Zeitfenster um die Marker gegen eine Baseline vergleichen.
- **Abnahme:** Die Cube-Signatur steht fest (Opcode, Feld-Layout, Identifikator), ebenso die Despawn- bzw.
  Open-Erkennung und der gemessene Sende-Radius in Metern. Belegt ist das mit mindestens 2 unabhängigen Sichtungen
  und als Hex-Test-Fixtures im Repo abgelegt. Sonst gilt das Abbruchkriterium.

### Phase 2: Eigene Position
- Finde heraus, welche Server→Client-Pakete meine Position tragen. Die eigene Entity-ID kommt aus `33 36`. Teste mit
  mir: Skill auf ein Monster, Teleport, Reittier, Gleiten, Schwimmen. Dokumentiere die Fallback-Heuristik.
- **Abnahme:** Eine Positionsspur, die bei einem Rundlauf plausibel ist.

### Phase 3: Radar-App
- Live-Anzeige der Cubes: Map-ID, xyz, Entfernung in Metern, Richtung relativ zu mir, Alter der Sichtung, Status
  „vermutlich geöffnet/despawnt". Neue Cubes lösen Sound oder Toast aus.
- Persistenz in SQLite: Jede Sichtung wird pro Map gespeichert, so entsteht über die Zeit eine eigene
  Spot-Datenbank. Export als CSV/JSON.
- UI: zuerst eine lokale Webseite (localhost) mit Canvas-Radar und Liste, live per WebSocket. Optional danach ein
  Always-on-top-Overlay mit Click-through.
- Patch-Robustheit: Opcodes und Signaturen liegen in einer JSON-Konfiguration. Beim Start läuft ein Selbstcheck, der
  warnt, wenn erwartete Opcodes ausbleiben.
- **Abnahme:** Ich laufe durch Verteron, die App zeigt einen Cube an, bevor ich ihn sehe, und ich finde ihn genau dort.

## Arbeitsweise
- Beginne mit einem kurzen Plan, dann Phase 0.
- Nach jeder Phase ein kurzer Bericht: Was ist belegt, was ist offen, was soll ich als Nächstes tun?
- Halte alle Erkenntnisse in `docs/FINDINGS.md` fest: Protokoll-Belege mit Hex-Beispielen, Datum und Spielversion.
  So können spätere Sessions nach einem Patch nahtlos weitermachen.
- Parser-Tests laufen gegen echte, gespeicherte Fixtures. Git-Repo anlegen und nach jeder Phase committen.
