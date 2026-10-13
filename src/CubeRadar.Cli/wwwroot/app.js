// Cube Radar web UI: renders the state pushed by `cuberadar radar` over /ws. Coordinates are game units (cm).
"use strict";

const SEND_RADIUS = 10000; // measured send radius for cubes (docs/FINDINGS.md B8), planar units
const LEVEL_M = 3;         // |height difference| up to this counts as "same height"
const STRIP = 64;          // width of the height strip on the right of the radar (px)
const STATUS_TEXT = {
  Visible: "sichtbar",
  OutOfView: "außer Sicht – steht vermutlich noch",
  Opening: "wird geöffnet",
  Opened: "geöffnet",
  Gone: "weg",
};
const COLORS = {};
for (const k of ["visible", "out", "opening", "gone", "me", "spot", "hist", "text", "muted", "line"])
  COLORS[k] = getComputedStyle(document.documentElement).getPropertyValue("--" + k).trim();

const ui = {
  mode: localStorage.getItem("mode") || "radar",
  range: Number(localStorage.getItem("range") || 200),
  north: localStorage.getItem("north") || "",
  sound: false,
  notify: localStorage.getItem("notify") === "1" && "Notification" in window && Notification.permission === "granted",
};
let msg = null;            // last pushed state
let receivedAt = 0;
let spots = [];            // possible spots of the current zone
let history = [];          // cube positions seen in earlier sessions
let spotsZone = null;
const known = new Set();   // cubes already announced
let firstUpdate = true;
let audio = null;

// ------------------------------------------------------------------ connection

function connect() {
  const ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onmessage = (e) => {
    msg = JSON.parse(e.data);
    receivedAt = Date.now();
    onState();
  };
  ws.onclose = () => setTimeout(connect, 2000);
}

function onState() {
  const r = msg.radar;
  const fresh = [];
  for (const c of r.cubes) {
    const key = `${c.objectId}@${c.firstSeenMs}`;
    if (!known.has(key)) {
      known.add(key);
      if (!firstUpdate && c.status === "Visible") fresh.push(c);
    }
  }
  firstUpdate = false;
  if (fresh.length) announce(fresh);
  if (r.zone && r.zone !== spotsZone) loadZone(r.zone);
  renderHeader();
  renderList(fresh);
  draw();
}

async function loadZone(zone) {
  spotsZone = zone;
  try {
    spots = await (await fetch(`/api/spots?zone=${encodeURIComponent(zone)}`)).json();
    history = await (await fetch(`/api/history?zone=${encodeURIComponent(zone)}`)).json();
  } catch { /* optional data */ }
}
setInterval(() => spotsZone && loadZone(spotsZone), 60000);

// ------------------------------------------------------------------ geometry

function orientation() {
  const o = msg.radar.orientation;
  const axis = ui.north || o.north;
  return {
    "-y": { nx: 0, ny: -1, ex: 1, ey: 0 },
    "+y": { nx: 0, ny: 1, ex: -1, ey: 0 },
    "+x": { nx: 1, ny: 0, ex: 0, ey: 1 },
    "-x": { nx: -1, ny: 0, ex: 0, ey: -1 },
  }[axis] || { nx: o.northX, ny: o.northY, ex: o.eastX, ey: o.eastY };
}

/** Own position: the latest of real fix and ring estimate. */
function me() {
  const r = msg.radar;
  const fix = r.self, ring = r.ring;
  // The ring estimate has no height: the last real fix's z stands in (its age is shown).
  const z = fix ? fix.z : null, zAge = fix ? fix.ageMs : null;
  if (ring && (!fix || ring.timeMs > fix.timeMs)) return { x: ring.x, y: ring.y, z, zAge, age: ring.ageMs, ring: ring.uncertainty, src: "Schätzung" };
  if (fix) return { x: fix.x, y: fix.y, z, zAge, age: fix.ageMs, ring: 0, src: fix.source };
  return null;
}

/** Height of a cube relative to us in metres, or null when our height is unknown. */
function dzOf(c, m) {
  return m && m.z != null ? (c.z - m.z) / 100 : null;
}

function heightInfo(dz) {
  if (dz == null) return { cls: "unknown", text: "Höhe unbekannt", short: "" };
  const a = Math.abs(Math.round(dz));
  if (Math.abs(dz) <= LEVEL_M) return { cls: "level", text: "≈ gleiche Höhe", short: "≈" };
  return dz > 0
    ? { cls: "up", text: `▲ ${a} m über dir`, short: `▲${a}` }
    : { cls: "down", text: `▼ ${a} m unter dir`, short: `▼${a}` };
}

function rel(p, from) {
  const o = orientation();
  const dx = p.x - from.x, dy = p.y - from.y;
  const n = dx * o.nx + dy * o.ny, e = dx * o.ex + dy * o.ey;
  let bearing = Math.atan2(e, n) * 180 / Math.PI;
  if (bearing < 0) bearing += 360;
  return { n, e, dist: Math.hypot(dx, dy), bearing };
}

const COMPASS = ["N", "NO", "O", "SO", "S", "SW", "W", "NW"];
const compass = (b) => COMPASS[Math.round(b / 45) % 8];

function fmtAge(ms) {
  if (ms == null || ms < 0) return "–";
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}:${String(s % 60).padStart(2, "0")} min`;
  return `${Math.floor(m / 60)}:${String(m % 60).padStart(2, "0")} h`;
}
const fmtNum = (v) => Math.round(v).toLocaleString("de-DE");

// ------------------------------------------------------------------ header and list

function chip(cls, label, value, title = "") {
  return `<span class="chip ${cls}" title="${title}"><span class="dot"></span>${label} <b>${value}</b></span>`;
}

function renderHeader() {
  const r = msg.radar, c = msg.capture;
  // Live: packet clock and wall clock share a base, so their gap is the time without game data. Replay: only updates count.
  const idle = Date.now() - receivedAt + (c.mode === "live" ? msg.wallMs - r.nowMs : 0);
  const capCls = c.streams > 0 && idle < 5000 ? "ok" : c.streams > 0 ? "mid" : "bad";
  const m = me();
  const fresh = r.fixFreshSeconds * 1000;
  const posCls = !m ? "bad" : m.ring ? "mid" : m.age <= fresh ? "ok" : m.age <= 30000 ? "mid" : "bad";
  const posVal = !m ? "unbekannt" : `${m.ring ? "± " + Math.round(m.ring / 100) + " m, " : ""}vor ${fmtAge(m.age)}`;
  document.getElementById("chips").innerHTML = [
    chip(capCls, c.mode === "replay" ? "Replay" : "Capture",
      c.mode === "replay" ? (c.status || "läuft") : c.streams > 0 ? (idle < 5000 ? "läuft" : `keine Daten seit ${fmtAge(idle)}`) : (c.status || "wartet"),
      `${c.status || ""} ${c.device || ""} · ${c.packets} Pakete${c.npcapDropped ? ` · ${c.npcapDropped} verworfen` : ""}`),
    chip(r.zone ? "ok" : "mid", "Zone", r.zone || "?", r.mapId ? `Map-ID ${r.mapId}` : "Map-ID unbekannt (kommt beim Zonenwechsel)"),
    chip(r.selfId ? "ok" : "bad", "Ich", r.selfId ?? "?", "eigene Entity-ID"),
    chip(posCls, "Position", posVal, m ? `Quelle: ${m.src}` : "noch keine Positionsmeldung (Buff wirken hilft)"),
  ].join("");
  const w = document.getElementById("warnings");
  w.hidden = r.warnings.length === 0;
  w.innerHTML = r.warnings.map((t) => `<div>${t}</div>`).join("");
}

function arrowSvg(bearing, color) {
  return `<svg class="arrow" viewBox="-22 -22 44 44"><circle r="20" fill="none" stroke="${COLORS.line}"/>
    <text y="-12" text-anchor="middle" font-size="7" fill="${COLORS.muted}">N</text>
    <g transform="rotate(${bearing})"><path d="M0 -15 L7 6 L0 2 L-7 6 Z" fill="${color}"/></g></svg>`;
}

function renderList(fresh) {
  const r = msg.radar, m = me();
  const freshKeys = new Set(fresh.map((c) => `${c.objectId}@${c.firstSeenMs}`));
  const rows = r.cubes.map((c) => {
    const rp = m ? rel(c, m) : null;
    return { c, rp };
  });
  const order = { Visible: 0, Opening: 1, OutOfView: 2, Opened: 3, Gone: 4 };
  rows.sort((a, b) => order[a.c.status] - order[b.c.status] || (a.rp?.dist ?? 1e12) - (b.rp?.dist ?? 1e12));
  document.getElementById("count").textContent = r.cubes.length ? `(${r.cubes.filter((c) => c.status === "Visible").length} sichtbar)` : "";
  document.getElementById("empty").hidden = r.cubes.length > 0;
  document.getElementById("list").innerHTML = rows.map(({ c, rp }) => {
    const color = { Visible: COLORS.visible, OutOfView: COLORS.out, Opening: COLORS.opening }[c.status] || COLORS.gone;
    const hi = heightInfo(dzOf(c, m));
    const status = STATUS_TEXT[c.status] + (c.openedBy ? (c.openedBySelf ? " (von dir)" : ` (von Spieler ${c.openedBy})`) : "");
    const dist = rp ? `${fmtNum(rp.dist / 100)} m<small>${compass(rp.bearing)} ${Math.round(rp.bearing)}°</small>` : `? m<small>eigene Position unbekannt</small>`;
    const heightAge = m && m.zAge > msg.radar.fixFreshSeconds * 1000 ? ` <span class="age">(deine Höhe von vor ${fmtAge(m.zAge)})</span>` : "";
    const created = c.createdMs ? ` · existiert seit ${fmtAge(r.nowMs - c.createdMs)}` : "";
    return `<li class="cube ${c.status}${freshKeys.has(`${c.objectId}@${c.firstSeenMs}`) ? " new" : ""}">
      <div class="dist">${dist}</div>
      ${rp ? arrowSvg(rp.bearing, color) : "<span></span>"}
      <div class="height ${hi.cls}">${hi.text}${heightAge}</div>
      <div class="status">${status}${c.learned ? ` · Template gelernt (${c.learned})` : ""}</div>
      <div class="meta">${c.zone || "?"} · Template ${c.template} · #${c.objectId} · gesehen vor ${fmtAge(r.nowMs - c.firstSeenMs)}${created}${c.status !== "Visible" ? ` · Status seit ${fmtAge(r.nowMs - c.statusSinceMs)}` : ""}</div>
      <div class="coords">x ${fmtNum(c.x)} · y ${fmtNum(c.y)} · z ${fmtNum(c.z)}
        <button data-copy="${Math.round(c.x)} ${Math.round(c.y)} ${Math.round(c.z)}">kopieren</button></div>
    </li>`;
  }).join("");
}

document.getElementById("list").addEventListener("click", (e) => {
  const t = e.target.closest("button[data-copy]");
  if (t) navigator.clipboard?.writeText(t.dataset.copy);
});

// ------------------------------------------------------------------ canvas

const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");

function draw() {
  if (!msg) return;
  const dpr = window.devicePixelRatio || 1;
  let w = canvas.clientWidth;
  const h = canvas.clientHeight;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
  }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const r = msg.radar, m = me(), o = orientation();
  const zoneSpots = spots.map((s) => ({ x: s[0], y: s[1] }));
  const full = w;
  w -= STRIP; // the height strip takes the right edge
  let center, scale;
  if (ui.mode === "radar" && (m || r.cubes.length)) {
    center = m || avg(r.cubes);
    scale = (Math.min(w, h) / 2 - 24) / (ui.range * 100);
  } else {
    const pts = [...r.cubes, ...r.trail.filter((p) => p.length === 2).map((p) => ({ x: p[0], y: p[1] })), ...(m ? [m] : [])];
    const fit = pts.length ? pts : zoneSpots;
    if (!fit.length) return drawText(w / 2, h / 2, "Noch keine Daten", COLORS.muted, "center");
    const ns = fit.map((p) => p.x * o.nx + p.y * o.ny), es = fit.map((p) => p.x * o.ex + p.y * o.ey);
    const pad = 4000;
    const nMin = Math.min(...ns) - pad, nMax = Math.max(...ns) + pad, eMin = Math.min(...es) - pad, eMax = Math.max(...es) + pad;
    scale = Math.min((w - 40) / (eMax - eMin), (h - 40) / (nMax - nMin));
    const cn = (nMin + nMax) / 2, ce = (eMin + eMax) / 2;
    center = { x: cn * o.nx + ce * o.ex, y: cn * o.ny + ce * o.ey };
  }
  const toScreen = (p) => {
    const q = rel(p, center);
    return [w / 2 + q.e * scale, h / 2 - q.n * scale];
  };

  // grid
  ctx.lineWidth = 1;
  if (ui.mode === "radar") {
    const step = niceStep(ui.range / 4);
    ctx.font = "11px Segoe UI, sans-serif";
    for (let d = step; d <= ui.range * 1.5; d += step) {
      ring(w / 2, h / 2, d * 100 * scale, COLORS.line, []);
      drawText(w / 2 + 4, h / 2 - d * 100 * scale - 3, `${d} m`, COLORS.muted);
    }
    ctx.strokeStyle = COLORS.line;
    line(w / 2, 8, w / 2, h - 8);
    line(8, h / 2, w - 8, h / 2);
  }
  drawText(12, 20, "N ↑", COLORS.text);

  // possible spots and history
  for (const s of zoneSpots) dot(toScreen(s), 1.6, COLORS.spot);
  ctx.globalAlpha = 0.55;
  for (const s of history) dot(toScreen(s), 2.4, COLORS.hist);
  ctx.globalAlpha = 1;

  // own trail and position
  if (r.trail.length > 1) {
    ctx.strokeStyle = COLORS.me;
    ctx.globalAlpha = 0.35;
    ctx.beginPath();
    let pen = false;
    for (const p of r.trail) {
      if (p.length < 2) { pen = false; continue; } // teleport: break the line
      const [x, y] = toScreen({ x: p[0], y: p[1] });
      pen ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
      pen = true;
    }
    ctx.stroke();
    ctx.globalAlpha = 1;
  }
  if (m) {
    const [x, y] = toScreen(m);
    ring(x, y, SEND_RADIUS * scale, COLORS.me, [6, 6], 0.35);
    if (m.ring) ring(x, y, Math.max(6, m.ring * scale), COLORS.me, [3, 4], 0.8);
    const stale = m.age > r.fixFreshSeconds * 1000;
    ctx.globalAlpha = stale ? 0.5 : 1;
    dot([x, y], 6, COLORS.me);
    ctx.globalAlpha = 1;
    drawText(x - 10, y + 16, stale || m.ring ? `du, vor ${fmtAge(m.age)}` : "du", COLORS.me, "right");
  }

  // cubes
  const t = Date.now();
  for (const c of r.cubes) {
    let [x, y] = toScreen(c);
    const color = { Visible: COLORS.visible, OutOfView: COLORS.out, Opening: COLORS.opening }[c.status] || COLORS.gone;
    const rp = m ? rel(c, m) : null;
    const inside = x > 12 && x < w - 12 && y > 12 && y < h - 12;
    if (!inside) { // arrow on the rim
      const a = Math.atan2(y - h / 2, x - w / 2);
      const rx = w / 2 - 18, ry = h / 2 - 18;
      const k = 1 / Math.max(Math.abs(Math.cos(a)) / rx, Math.abs(Math.sin(a)) / ry);
      x = w / 2 + Math.cos(a) * k;
      y = h / 2 + Math.sin(a) * k;
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(a);
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.moveTo(10, 0); ctx.lineTo(-6, 7); ctx.lineTo(-6, -7); ctx.closePath();
      ctx.fill();
      ctx.restore();
    } else if (c.status === "Visible" || c.status === "Opening" || c.status === "OutOfView") {
      const pulse = c.status === "Opening" ? 3 + 2 * Math.sin(t / 150) : 0;
      const filled = c.status !== "OutOfView";
      ctx.shadowColor = color;
      ctx.shadowBlur = filled ? 12 : 0;
      marker(x, y, 7 + pulse / 2, heightInfo(dzOf(c, m)).cls, color, filled);
      ctx.shadowBlur = 0;
    } else {
      ctx.strokeStyle = color;
      ctx.lineWidth = 2;
      line(x - 5, y - 5, x + 5, y + 5);
      line(x - 5, y + 5, x + 5, y - 5);
      ctx.lineWidth = 1;
    }
    const hs = heightInfo(dzOf(c, m)).short;
    if (rp) drawText(x + 11, y - 8, `${fmtNum(rp.dist / 100)} m${hs ? " · " + hs : ""}`, color);
  }

  drawHeightStrip(full, h, r, m);
}

/** Square for "same height", triangle up / down for above / below us. */
function marker(x, y, s, cls, color, filled) {
  ctx.beginPath();
  if (cls === "up") { ctx.moveTo(x, y - s); ctx.lineTo(x + s, y + s * 0.8); ctx.lineTo(x - s, y + s * 0.8); }
  else if (cls === "down") { ctx.moveTo(x, y + s); ctx.lineTo(x + s, y - s * 0.8); ctx.lineTo(x - s, y - s * 0.8); }
  else ctx.rect(x - s * 0.85, y - s * 0.85, s * 1.7, s * 1.7);
  ctx.closePath();
  ctx.lineWidth = 2;
  if (filled) { ctx.fillStyle = color; ctx.fill(); }
  else { ctx.strokeStyle = color; ctx.stroke(); }
  ctx.lineWidth = 1;
}

/** Vertical strip: our height in the middle, every live cube above or below it. */
function drawHeightStrip(full, h, r, m) {
  const x0 = full - STRIP, mid = h / 2;
  ctx.fillStyle = "rgba(255,255,255,0.03)";
  ctx.fillRect(x0, 0, STRIP, h);
  ctx.strokeStyle = COLORS.line;
  line(x0, 0, x0, h);
  drawText(x0 + STRIP / 2, 20, "Höhe", COLORS.muted, "center");
  const live = r.cubes.filter((c) => c.status === "Visible" || c.status === "Opening" || c.status === "OutOfView");
  if (!m || m.z == null) {
    drawText(x0 + STRIP / 2, mid, "?", COLORS.muted, "center");
    return;
  }
  const dzs = live.map((c) => dzOf(c, m));
  const range = Math.max(20, Math.ceil(Math.max(0, ...dzs.map(Math.abs)) / 10) * 10 + 10); // metres, middle to edge
  const px = (h / 2 - 34) / range;
  for (const v of [range, range / 2, -range / 2, -range]) {
    const y = mid - v * px;
    ctx.strokeStyle = COLORS.line;
    line(x0 + 6, y, x0 + 14, y);
    drawText(x0 + 17, y + 4, `${v > 0 ? "+" : ""}${Math.round(v)}`, COLORS.muted);
  }
  ctx.strokeStyle = COLORS.me;
  line(x0 + 4, mid, full - 4, mid);
  drawText(full - 4, mid - 4, "du", COLORS.me, "right");
  live.forEach((c, i) => {
    const dz = dzs[i];
    const y = mid - Math.max(-range, Math.min(range, dz)) * px;
    const color = { Visible: COLORS.visible, OutOfView: COLORS.out, Opening: COLORS.opening }[c.status];
    marker(x0 + 22, y, 5, heightInfo(dz).cls, color, c.status !== "OutOfView");
    drawText(x0 + 31, y + 4, `${dz > 0 ? "+" : ""}${Math.round(dz)}`, color);
  });
}

function avg(list) {
  return { x: list.reduce((s, p) => s + p.x, 0) / list.length, y: list.reduce((s, p) => s + p.y, 0) / list.length };
}

function niceStep(v) {
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  return [1, 2, 2.5, 5, 10].map((f) => f * p).find((s) => s >= v) || v;
}

function ring(x, y, r, color, dash, alpha = 1) {
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.strokeStyle = color;
  ctx.setLineDash(dash);
  ctx.beginPath();
  ctx.arc(x, y, r, 0, Math.PI * 2);
  ctx.stroke();
  ctx.restore();
}

function dot([x, y], r, color) {
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.arc(x, y, r, 0, Math.PI * 2);
  ctx.fill();
}

function line(x0, y0, x1, y1) {
  ctx.beginPath();
  ctx.moveTo(x0, y0);
  ctx.lineTo(x1, y1);
  ctx.stroke();
}

function drawText(x, y, text, color, align = "left") {
  ctx.font = "12px Segoe UI, sans-serif";
  ctx.textAlign = align;
  ctx.fillStyle = color;
  ctx.fillText(text, x, y);
  ctx.textAlign = "left";
}

// ------------------------------------------------------------------ alerts

function announce(cubes) {
  const m = me();
  const text = cubes.map((c) => {
    const rp = m ? rel(c, m) : null;
    return rp ? `${fmtNum(rp.dist / 100)} m ${compass(rp.bearing)}` : `${c.zone || ""} x ${fmtNum(c.x)} y ${fmtNum(c.y)}`;
  }).join(", ");
  if (ui.sound && audio) chime();
  if (ui.notify) new Notification(cubes.length > 1 ? `${cubes.length} Hidden Cubes` : "Hidden Cube", { body: text, tag: "cube" });
  document.title = `● Cube: ${text}`;
  setTimeout(() => (document.title = "Cube Radar"), 15000);
}

function chime() {
  const now = audio.currentTime;
  [[880, 0], [1320, 0.16]].forEach(([f, d]) => {
    const osc = audio.createOscillator(), gain = audio.createGain();
    osc.frequency.value = f;
    gain.gain.setValueAtTime(0.0001, now + d);
    gain.gain.exponentialRampToValueAtTime(0.25, now + d + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + d + 0.3);
    osc.connect(gain).connect(audio.destination);
    osc.start(now + d);
    osc.stop(now + d + 0.32);
  });
}

// ------------------------------------------------------------------ controls

function syncControls() {
  document.querySelectorAll(".seg button").forEach((b) => b.classList.toggle("on", b.dataset.mode === ui.mode));
  document.getElementById("range").value = String(ui.range);
  document.getElementById("range").disabled = ui.mode !== "radar";
  document.getElementById("north").value = ui.north;
  const s = document.getElementById("sound");
  s.textContent = ui.sound ? "Ton an" : "Ton aus";
  s.classList.toggle("on", ui.sound);
  const n = document.getElementById("notify");
  n.textContent = ui.notify ? "Hinweise an" : "Hinweise aus";
  n.classList.toggle("on", ui.notify);
}

document.querySelectorAll(".seg button").forEach((b) => b.addEventListener("click", () => {
  ui.mode = b.dataset.mode;
  localStorage.setItem("mode", ui.mode);
  syncControls();
  draw();
}));
document.getElementById("range").addEventListener("change", (e) => {
  ui.range = Number(e.target.value);
  localStorage.setItem("range", ui.range);
  draw();
});
document.getElementById("north").addEventListener("change", (e) => {
  ui.north = e.target.value;
  localStorage.setItem("north", ui.north);
  if (msg) onState();
});
document.getElementById("sound").addEventListener("click", () => {
  ui.sound = !ui.sound;
  if (ui.sound && !audio) audio = new AudioContext();
  if (ui.sound) chime();
  syncControls();
});
document.getElementById("notify").addEventListener("click", async () => {
  if (!("Notification" in window)) return;
  if (!ui.notify && Notification.permission !== "granted") await Notification.requestPermission();
  ui.notify = !ui.notify && Notification.permission === "granted";
  localStorage.setItem("notify", ui.notify ? "1" : "0");
  syncControls();
});
window.addEventListener("resize", draw);
setInterval(() => msg && (renderHeader(), draw()), 1000);

syncControls();
connect();
