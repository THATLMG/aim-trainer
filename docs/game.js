// LMG's Aim Trainer in the browser: the same modes, sensitivity math, coach and score codes as the Windows app.
// Menus are HTML; a round is drawn on one canvas with the mouse locked (raw input where the browser supports it).
(() => {
"use strict";

// ---------- game profiles (match AimTrainer.cs) ----------
const GP = {
  minecraft: { hfov: 102, def: 100, hb: .6, jumps: true, size: 1.25, square: true, band: .8, bg: "#161c14" },
  roblox:    { hfov: 102, def: 1.0, hb: .5, jumps: true, size: 1.3, band: .9, bg: "#12171f" },
  cs2:       { hfov: 106.26, def: 1.5, hb: .9, size: .9, band: .25, bg: "#1a1713" },
  valorant:  { hfov: 103, def: .35, hb: .9, size: .85, band: .25, bg: "#1b1317" },
  fortnite:  { hfov: 103, def: 6.5, hb: .5, jumps: true, size: 1, band: .8, bg: "#161320" },
  overwatch: { hfov: 103, def: 5, hb: .55, jumps: true, size: 1.1, band: .7, bg: "#15181e" },
  apex:      { hfov: 110, def: 2.0, hb: .7, size: 1, band: .6, bg: "#1a1515" },
  custom:    { hfov: 103, def: 1.0, hb: .6, size: 1, band: .8, bg: "#111a1a" }
};
const TRK = "Tracking", FLK = "Flick", BOT = "Bots", RCT = "Reaction", FND = "Sens finder";
const LETTER = { Tracking: "T", Flick: "F", Bots: "B", Reaction: "R" };
const MODE_DESC = {
  [TRK]: "Keep your crosshair on a moving ball. Score is the % of time on target.",
  [FLK]: "Snap to targets as they pop up and click. 30 seconds.",
  [BOT]: "Strafing and standing bots with 3 HP. Headshots kill instantly.",
  [RCT]: "Click the moment the screen turns green. 5 tries.",
  [FND]: "5 short tracking rounds at hidden sens values. Finds the one you aim best with."
};
const BallR = [.045, .033, .024, .017], BallSpeed = [.22, .36, .5, .65];
const FlickR = [.042, .032, .024, .017], FlickMin = [.12, .16, .2, .25], FlickMax = [.28, .36, .45, .5], FlickTimeout = [0, 0, 1.6, 1.0];
const BotU = [.075, .06, .05, .04], BotShare = [.3, .5, .7, .85], BotSpd = [2.5, 3.5, 4.5, 6], BotCount = [2, 3, 4, 5];

// ---------- saved data (this browser) ----------
const store = {
  get(k, d) { try { const v = localStorage.getItem("lmg." + k); return v ? JSON.parse(v) : d; } catch { return d; } },
  set(k, v) { try { localStorage.setItem("lmg." + k, JSON.stringify(v)); } catch {} }
};
let profile = store.get("profile", null);        // { name, dpi }
let settings = store.get("settings", {});        // gameId -> { sens, dpi }
let records = store.get("records", []);          // { g, m, d, s, t }
let gameId = store.get("lastGame", null), mode = TRK, diff = 1, rawInput = null;
// Browsers without raw mouse input (Safari, the Mac app) report scaled movement; calibration measures the scale.
let calib = store.get("calib", null);   // { factor }
const moveFactor = () => (rawInput === false && calib ? calib.factor : 1);
let CAL = null;                         // calibration in progress

const sensOf = id => (settings[id] && settings[id].sens) || GP[id].def;
const dpiOf = id => (settings[id] && settings[id].dpi) || (profile ? profile.dpi : 800);
function degPerCount(id, sens) {
  if (GAMES[id].mc) { const f = sens / 200 * .6 + .2; return f * f * f * 1.2; }
  return sens * GAMES[id].yaw;
}
function sensFor(id, dpc) {
  if (GAMES[id].mc) { const f = Math.cbrt(dpc / 1.2); return Math.max(1, (f - .2) / .6 * 200); }
  return dpc / GAMES[id].yaw;
}
const roundSens = (id, s) => +(+s).toFixed(GAMES[id].dec);
const cleanName = s => (s || "").replace(/[^A-Za-z0-9 _-]/g, "").trim().slice(0, 16).trim();
const parseNum = s => parseFloat(String(s).replace(",", ".").replace("%", ""));
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const R = Math.random;

function history(m, d) { return records.filter(r => r.g === gameId && r.m === m && (m === RCT || r.d === d)); }
function saveRecord(m, d, s, lowerBetter) {
  const prev = history(m, d);
  const pb = prev.length > 0 && (lowerBetter ? s < Math.min(...prev.map(r => r.s)) : s > Math.max(...prev.map(r => r.s)));
  records.push({ g: gameId, m, d, s, t: Date.now() });
  if (records.length > 3000) records = records.slice(-3000);
  store.set("records", records);
  return pb;
}

// ---------- score codes (same as the app) ----------
function makeCode(letter, d, score) {
  const payload = ["v1", cleanName(profile.name) || "Player", gameId, letter, d, score, roundSens(gameId, sensOf(gameId)), Math.round(dpiOf(gameId)), Math.floor(Date.now() / 1000)].join("|");
  const bytes = new TextEncoder().encode(payload);
  const b64 = btoa(String.fromCharCode(...bytes)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return "AIM1." + b64 + "." + fnv(CODE_SECRET + payload);
}

// ---------- views ----------
const views = ["setup", "home", "game", "results"];
function show(v) {
  views.forEach(x => $("v-" + x).hidden = x !== v);
  $("stage").hidden = true;
  scrollTo(0, 0);
  if (v === "home") renderHome();
  if (v === "game") renderGame();
}

function renderSetup() {
  $("setup-name").value = profile ? profile.name : (AT.profile ? cleanName(AT.profile.username) : "");
  $("setup-dpi").value = profile ? profile.dpi : 800;
  $("setup-title").textContent = profile ? "Edit your profile" : "Let's set you up";
  $("setup-cancel").hidden = !profile;
}
$("setup-form").addEventListener("submit", e => {
  e.preventDefault();
  const n = cleanName($("setup-name").value), d = parseNum($("setup-dpi").value);
  const msg = $("setup-msg"); msg.textContent = "";
  if (n.length < 2) { msg.append(el("div", { class: "msg err", text: "Pick a player name with at least 2 letters or numbers." })); return; }
  if (!(d >= 100 && d <= 50000)) { msg.append(el("div", { class: "msg err", text: "Enter your mouse DPI as a number, like 800 or 1600." })); return; }
  const old = profile ? profile.dpi : null;
  for (const id in settings) if (settings[id].dpi === old) settings[id].dpi = Math.round(d);
  profile = { name: n, dpi: Math.round(d) };
  store.set("profile", profile); store.set("settings", settings);
  show("home");
});
$("setup-cancel").onclick = () => show("home");

function renderHome() {
  $("touch-note").hidden = !(matchMedia("(pointer: coarse)").matches && !matchMedia("(any-pointer: fine)").matches);
  $("home-who").textContent = "Playing as " + profile.name + " · " + profile.dpi + " DPI";
  const box = $("home-games"); box.textContent = "";
  for (const id in GAMES) {
    const g = GAMES[id], set = settings[id];
    box.append(el("button", { class: "game pick", style: "--c:" + g.color, onclick: () => { gameId = id; store.set("lastGame", id); show("game"); } }, [
      el("i"), el("b", { text: g.name }), el("span", { text: g.tag }),
      el("span", { class: "mono", style: "color:" + (set ? g.color : "var(--muted)"), text: set ? "Sens " + fmtSens(id, set.sens) + " · " + set.dpi + " DPI" : "Sens not set yet" })
    ]));
  }
}

function renderGame() {
  const g = GAMES[gameId];
  $("game-name").textContent = g.name; $("game-name").style.color = g.color;
  $("game-tag").textContent = g.tag;
  $("sens-label").textContent = "In-game sensitivity" + (g.suffix ? " (" + g.suffix + ")" : "");
  $("sens").value = roundSens(gameId, sensOf(gameId));
  $("dpi").value = dpiOf(gameId);
  updateCm();
  const modes = $("modes"); modes.textContent = "";
  for (const m of [TRK, FLK, BOT, RCT, FND])
    modes.append(el("button", { class: "mode-card", "aria-pressed": String(mode === m), style: "--c:" + g.color, onclick: () => { mode = m; renderGame(); } },
      [el("b", { text: m }), el("span", { text: MODE_DESC[m] })]));
  const dd = $("diffs"); dd.textContent = "";
  DIFFS.forEach((name, i) => dd.append(el("button", { class: "chip", "aria-pressed": String(diff === i), style: "--c:" + g.color, onclick: () => { diff = i; renderGame(); } }, [el("span", { text: name })])));
  $("diff-wrap").hidden = !(mode === TRK || mode === FLK || mode === BOT);
  renderProgress();
}
function updateCm() {
  const cm = 360 / (degPerCount(gameId, sensOf(gameId)) * dpiOf(gameId)) * 2.54;
  $("cm360").textContent = cm.toFixed(1) + " cm";
  const row = $("mouse-row"); row.textContent = "";
  row.append(el("span", { class: "muted", style: "font-size:14px", text: calib ? "Mouse calibrated (×" + calib.factor.toFixed(2) + ")" : "Mouse not calibrated · only needed on Mac or in Safari" }),
    el("button", { class: "btn small", text: calib ? "Recalibrate" : "Calibrate mouse", onclick: startCalibration }));
}
function saveSensInputs() {
  const s = parseNum($("sens").value), d = parseNum($("dpi").value);
  if (s > 0 && d >= 100 && d <= 50000) { settings[gameId] = { sens: s, dpi: Math.round(d) }; store.set("settings", settings); updateCm(); }
}
$("sens").addEventListener("input", saveSensInputs);
$("dpi").addEventListener("input", saveSensInputs);

function renderProgress() {
  const box = $("progress"); box.textContent = "";
  if (mode === FND) { box.append(el("p", { class: "muted", text: "Five 10-second tracking rounds, each secretly at a different sensitivity (80% to 120% of yours). At the end you see which one you tracked best with, in " + GAMES[gameId].name + " numbers." })); return; }
  const h = history(mode, diff);
  box.append(el("p", { class: "label", text: "Your progress · " + mode + (mode === RCT ? "" : " · " + DIFFS[diff]) }));
  if (!h.length) { box.append(el("p", { class: "muted", text: "No rounds yet. Your scores will show up here." })); return; }
  const lower = mode === RCT, vals = h.map(r => r.s), best = lower ? Math.min(...vals) : Math.max(...vals);
  box.append(el("div", { class: "row", style: "gap:26px" }, [
    el("div", {}, [el("div", { class: "muted", text: "Best" }), el("div", { class: "big", text: fmtRes(mode, best) })]),
    el("div", {}, [el("div", { class: "muted", text: "Last" }), el("div", { class: "big", text: fmtRes(mode, vals[vals.length - 1]) })]),
    el("div", {}, [el("div", { class: "muted", text: "Rounds" }), el("div", { class: "big", text: String(vals.length) })])]));
  box.append(spark(vals.slice(-20), GAMES[gameId].color));
}
function fmtRes(m, v) { return m === TRK ? v.toFixed(1) + "%" : m === FLK ? Math.round(v) + " hits" : m === BOT ? Math.round(v) + " kills" : Math.round(v) + " ms"; }
function spark(vals, color) {
  const w = 600, h = 90, lo = Math.min(...vals), hi = Math.max(...vals), span = hi - lo || 1;
  const pts = vals.map((v, i) => [vals.length === 1 ? w / 2 : i / (vals.length - 1) * w, h - 8 - (v - lo) / span * (h - 16)]);
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 " + w + " " + h); svg.setAttribute("class", "spark"); svg.setAttribute("preserveAspectRatio", "none");
  svg.innerHTML = '<polyline fill="none" stroke="' + color + '" stroke-width="3" vector-effect="non-scaling-stroke" points="' + pts.map(p => p.join(",")).join(" ") + '"/>' +
    '<circle cx="' + pts[pts.length - 1][0] + '" cy="' + pts[pts.length - 1][1] + '" r="5" fill="#fff"/>';
  return svg;
}

// ---------- the round ----------
const stage = $("stage"), cv = $("stage-canvas"), ctx = cv.getContext("2d");
let S = null, W = 0, H = 0, last = 0, dx = 0, dy = 0;

function size() {
  const dpr = Math.min(2, devicePixelRatio || 1);
  W = cv.clientWidth; H = cv.clientHeight;
  cv.width = W * dpr; cv.height = H * dpr; ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}
addEventListener("resize", () => { if (!stage.hidden) size(); });

function pxPerCount(mult) {
  const f = (W / 2) / Math.tan(GP[gameId].hfov / 2 * Math.PI / 180);
  return degPerCount(gameId, sensOf(gameId)) * mult * f * Math.PI / 180;
}

function prepare(m) {
  saveSensInputs();
  S = { mode: m, diff: m === FND ? 1 : diff, active: false, started: false, countdown: 0, time: 0, len: m === FND ? 10 : 30, mult: 1,
        hits: 0, misses: 0, expired: 0, hitTimes: [], progress: [], shots: 0, botHits: 0, headHits: 0, kills: 0, headKills: 0, ttk: [], bots: [], marks: [],
        on: 0, tot: 0, leadSum: 0, leadTime: 0, crossPath: 0, ballPath: 0, rState: 0, rTimes: [], greenAt: 0, greenShown: 0, last: 0 };
  if (m === FND) { S.fMults = [.8, .9, 1, 1.1, 1.2].sort(() => R() - .5); S.fScores = []; S.fIdx = 0; S.mult = S.fMults[0]; }
  stage.hidden = false;
  stage.style.background = GP[gameId].bg;
  $("stage-start").hidden = false;
  $("stage-title").textContent = GAMES[gameId].name + " · " + (m === FND ? "Sens finder" : m === RCT ? "Reaction test" : m + " · " + DIFFS[S.diff]);
  $("stage-hint").textContent = m === RCT ? "Wait for green, then click (or press Space) as fast as you can. 5 tries."
    : "Your mouse locks to the game while you play. Press Esc to stop.";
  size();
}
$("start").onclick = () => prepare(mode);

$("stage-go").onclick = async () => {
  if (CAL) return beginCalibration();
  $("stage-start").hidden = true;
  try { if (stage.requestFullscreen && !document.fullscreenElement) stage.requestFullscreen().catch(() => {}); } catch {}
  if (S.mode !== RCT) await lockPointer();
  setTimeout(() => {
    size();
    S.cx = W / 2; S.cy = H / 2; S.ppc = pxPerCount(S.mult);
    const ah = H * (.25 + .5 * GP[gameId].band);
    S.arena = S.mode === BOT ? { x: W * .08, y: H * .2, w: W * .84, h: H * .68 } : { x: W * .12, y: (H - ah) / 2, w: W * .76, h: ah };
    S.time = S.len; S.active = true;
    if (S.mode === RCT) { S.rState = 0; S.countdown = 0; S.started = true; }
    else { S.countdown = 3; resetBall(); if (S.mode === BOT) for (let i = 0; i < BotCount[S.diff]; i++) spawnBot(0); }
    last = performance.now(); dx = dy = 0;
    requestAnimationFrame(frame);
  }, 120);   // let fullscreen settle so the width (and the sens math) is right
};

async function lockPointer() {
  try {
    const r = cv.requestPointerLock({ unadjustedMovement: true });
    if (r && r.then) await r;
    rawInput = true;
  } catch {
    try { const r = cv.requestPointerLock(); if (r && r.then) await r; } catch {}
    rawInput = false;
  }
}
document.addEventListener("mousemove", e => {
  if (document.pointerLockElement !== cv) return;
  if (CAL) { if (CAL.phase === 2) CAL.units += e.movementX; return; }
  const f = moveFactor();
  dx += e.movementX * f; dy += e.movementY * f;
});
document.addEventListener("pointerlockchange", () => {
  if (document.pointerLockElement !== cv && CAL && CAL.phase < 3) return endCalibration();
  if (document.pointerLockElement !== cv && S && S.active && S.mode !== RCT) abort();
});

// ---------- mouse calibration ----------
function startCalibration() {
  S = null; CAL = { phase: 0, units: 0 };
  stage.hidden = false; stage.style.background = "#0b0e14";
  $("stage-start").hidden = false;
  $("stage-title").textContent = "Calibrate your mouse";
  $("stage-hint").textContent = "You'll need a ruler. On a Mac, first turn off pointer acceleration: System Settings → Mouse → Advanced → Pointer acceleration. Then click Start: put the mouse at 0 cm, press Space, slide exactly 10 cm to the right, and press Space again.";
  $("stage-go").textContent = "Start calibration";
  size();
}
async function beginCalibration() {
  $("stage-start").hidden = true;
  await lockPointer();
  if (rawInput) {
    calib = null; store.set("calib", null);
    CAL.phase = 4; drawCalibration();
    setTimeout(endCalibration, 3000);
    return;
  }
  CAL.phase = 1; drawCalibration();
}
function calibrationKey() {
  if (CAL.phase === 1) { CAL.phase = 2; CAL.units = 0; }
  else if (CAL.phase === 2) {
    const expected = dpiOf(gameId) * 10 / 2.54, got = Math.abs(CAL.units);
    if (got < 20) { CAL.phase = 1; CAL.error = "No movement detected. Try again: Space at 0 cm, slide 10 cm, Space."; }
    else {
      CAL.factor = clamp(expected / got, .05, 50);
      calib = { factor: CAL.factor }; store.set("calib", calib);
      CAL.phase = 3;
      if (document.pointerLockElement) document.exitPointerLock();
    }
  } else if (CAL.phase === 3) endCalibration();
  drawCalibration();
}
function drawCalibration() {
  if (!CAL) return;
  size();
  ctx.fillStyle = "#0b0e14"; ctx.fillRect(0, 0, W, H);
  ctx.textAlign = "center"; ctx.fillStyle = "#fff";
  const big = { 1: "Mouse at 0 cm, press Space", 2: "Slide exactly 10 cm right, then press Space", 3: "Calibrated!", 4: "No calibration needed" }[CAL.phase] || "";
  const small = CAL.phase === 2 ? "Measured so far: " + Math.abs(CAL.units) + " units"
    : CAL.phase === 3 ? "Your mouse moves " + Math.round(Math.abs(CAL.units)) + " units per 10 cm. Factor ×" + CAL.factor.toFixed(2) + " saved. Press Space or click to finish."
    : CAL.phase === 4 ? "This browser already reads your raw mouse movement, so your sens is exact."
    : (CAL.error || "Keep the mouse flat and slide it in a straight line along the ruler.");
  ctx.font = "700 44px 'Chakra Petch', sans-serif"; ctx.fillText(big, W / 2, H / 2 - 10);
  ctx.font = "17px Barlow, sans-serif"; ctx.fillStyle = "rgba(255,255,255,.8)"; ctx.fillText(small, W / 2, H / 2 + 34);
  ctx.font = "13px Barlow, sans-serif"; ctx.fillStyle = "rgba(255,255,255,.55)"; ctx.fillText("Esc to cancel", W / 2, H - 28);
  if (CAL.phase === 2) requestAnimationFrame(drawCalibration);
}
function endCalibration() {
  CAL = null;
  if (document.pointerLockElement) document.exitPointerLock();
  $("stage-go").textContent = "Start round";
  show("game");
}
cv.addEventListener("click", () => { if (CAL && CAL.phase === 3) endCalibration(); });
function abort() {
  if (!S) return;
  S.active = false;
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  if (document.pointerLockElement) document.exitPointerLock();
  show("game");
}
document.addEventListener("keydown", e => {
  if (CAL && CAL.phase > 0) {
    if (e.key === "Escape") endCalibration();
    else if (e.code === "Space") { e.preventDefault(); calibrationKey(); }
    return;
  }
  if (!S || !S.active) return;
  if (e.key === "Escape") abort();
  if (S.mode === RCT && (e.code === "Space" || e.key === "Enter")) { e.preventDefault(); reactionClick(performance.now() / 1000); }
});
cv.addEventListener("mousedown", e => {
  if (!S || !S.active || e.button !== 0) return;
  const now = performance.now() / 1000;
  if (S.mode === RCT) reactionClick(now);
  else if (S.countdown <= 0 && S.mode === FLK) flickClick(now);
  else if (S.countdown <= 0 && S.mode === BOT) botClick(now);
});

function frame(t) {
  if (!S || !S.active) return;
  const now = t / 1000, dt = Math.min(.05, now - last / 1000); last = t;
  let moved = 0;
  if (S.mode !== RCT) {
    const ox = S.cx, oy = S.cy;
    S.cx = clamp(S.cx + dx * S.ppc, 0, W); S.cy = clamp(S.cy + dy * S.ppc, 0, H);
    moved = Math.hypot(S.cx - ox, S.cy - oy);
  }
  dx = dy = 0;
  if (S.countdown > 0) {
    S.countdown -= dt;
    if (S.countdown <= 0) {
      S.countdown = 0;
      if (S.mode === FLK) spawnTarget(now);
      if (S.mode === BOT) { S.bots.forEach(b => b.spawn = now); startPath(); }
    }
  } else if (dt > 0) step(dt, now, moved);
  if (!S.active) return;
  draw(now);
  if (S.markGreen) { S.greenShown = performance.now() / 1000; S.markGreen = false; }
  requestAnimationFrame(frame);
}

function step(dt, now, moved) {
  if (S.mode === RCT) { if (S.rState === 1 && now >= S.greenAt) { S.rState = 2; S.markGreen = true; } return; }
  if (S.mode === FLK) stepFlick(dt, now, moved);
  else if (S.mode === BOT) stepBots(dt, now, moved);
  else stepTracking(dt, moved);
  S.time -= dt;
  if (S.time <= 0) roundOver();
}

const hitShape = (dx2, dy2, r) => GP[gameId].square ? Math.abs(dx2) <= r && Math.abs(dy2) <= r : dx2 * dx2 + dy2 * dy2 <= r * r;

// tracking ball
function resetBall() {
  const g = GP[gameId], d = S.diff;
  Object.assign(S, { br: BallR[d] * H * g.size, bspd: BallSpeed[d] * H, bx: S.arena.x + S.arena.w / 2, by: S.arena.y + S.arena.h / 2,
    dirX: R() < .5 ? -1 : 1, dirY: 0, heading: 0, turnRate: 0, turnTimer: .6, changeTimer: .6, speedMul: 1, jumpOff: 0, jumpV: 0, jumpTimer: 1.5, bvx: 0, bvy: 0 });
  S.heading = Math.atan2(S.dirY, S.dirX);
}
function newDirection() {
  const g = GP[gameId];
  if (R() < g.hb) { S.dirX = S.dirX >= 0 ? -1 : 1; S.dirY = (R() * 2 - 1) * .5; }
  else { const a = R() * Math.PI * 2; S.dirX = Math.cos(a); S.dirY = Math.sin(a); }
  const n = Math.hypot(S.dirX, S.dirY); S.dirX /= n; S.dirY /= n; S.heading = Math.atan2(S.dirY, S.dirX);
  if (S.diff === 1) { S.changeTimer = .7 + R() * .9; S.speedMul = 1; }
  else if (S.diff === 2) { S.changeTimer = .3 + R() * .6; S.speedMul = .85 + R() * .3; }
  else { S.changeTimer = .15 + R() * .4; S.speedMul = .6 + R() * 1.3; }
}
function updateBall(dt) {
  const g = GP[gameId], G = 3 * H, jumpH = H * .1, a = S.arena, vScale = 1 - .75 * g.hb;
  if (S.diff === 0) {
    S.turnTimer -= dt; if (S.turnTimer <= 0) { S.turnRate = (R() * 2 - 1) * 2.4; S.turnTimer = .5 + R(); }
    S.heading += S.turnRate * dt; S.dirX = Math.cos(S.heading); S.dirY = Math.sin(S.heading);
  } else { S.changeTimer -= dt; if (S.changeTimer <= 0) newDirection(); }
  const spd = S.bspd * S.speedMul;
  S.bvx = S.dirX * spd; S.bvy = S.dirY * spd * vScale;
  S.bx += S.bvx * dt; S.by += S.bvy * dt;
  let top = a.y + S.br + (g.jumps ? jumpH : 0); const bottom = a.y + a.h - S.br; if (top > bottom) top = bottom;
  if (S.bx < a.x + S.br) { S.bx = a.x + S.br; S.dirX = Math.abs(S.dirX); S.heading = Math.atan2(S.dirY, S.dirX); }
  if (S.bx > a.x + a.w - S.br) { S.bx = a.x + a.w - S.br; S.dirX = -Math.abs(S.dirX); S.heading = Math.atan2(S.dirY, S.dirX); }
  if (S.by < top) { S.by = top; S.dirY = Math.abs(S.dirY); S.heading = Math.atan2(S.dirY, S.dirX); }
  if (S.by > bottom) { S.by = bottom; S.dirY = -Math.abs(S.dirY); S.heading = Math.atan2(S.dirY, S.dirX); }
  if (g.jumps && S.diff >= 1) {
    S.jumpTimer -= dt;
    if (S.jumpTimer <= 0 && S.jumpOff === 0 && S.jumpV === 0) { S.jumpV = -Math.sqrt(2 * G * jumpH); S.jumpTimer = 1 + R() * 2; }
    if (S.jumpV !== 0 || S.jumpOff < 0) { S.jumpV += G * dt; S.jumpOff += S.jumpV * dt; if (S.jumpOff >= 0) { S.jumpOff = 0; S.jumpV = 0; } }
  }
  S.bvy += S.jumpV;
}
function stepTracking(dt, moved) {
  updateBall(dt);
  const ex = S.cx - S.bx, ey = S.cy - (S.by + S.jumpOff);
  S.tot += dt; if (hitShape(ex, ey, S.br)) S.on += dt;
  const sp = Math.hypot(S.bvx, S.bvy), dist = Math.hypot(ex, ey);
  if (dist < 3 * S.br && sp > 1) {
    S.leadSum += (ex * S.bvx + ey * S.bvy) / sp / S.br * dt;   // + ahead of the ball, - behind it
    S.leadTime += dt; S.crossPath += moved; S.ballPath += sp * dt;
  }
}

// flick
function spawnTarget(now) {
  const d = S.diff, a = S.arena; S.tr = FlickR[d] * H * GP[gameId].size;
  let ok = false;
  for (let i = 0; i < 40 && !ok; i++) {
    const dist = (FlickMin[d] + R() * (FlickMax[d] - FlickMin[d])) * H, ang = R() * Math.PI * 2;
    S.tx = S.cx + Math.cos(ang) * dist; S.ty = S.cy + Math.sin(ang) * dist;
    ok = S.tx > a.x + S.tr && S.tx < a.x + a.w - S.tr && S.ty > a.y + S.tr && S.ty < a.y + a.h - S.tr;
  }
  if (!ok) { S.tx = a.x + S.tr + R() * (a.w - 2 * S.tr); S.ty = a.y + S.tr + R() * (a.h - 2 * S.tr); }
  S.sx = S.cx; S.sy = S.cy; S.fd = Math.hypot(S.tx - S.cx, S.ty - S.cy);
  S.ux = S.fd ? (S.tx - S.cx) / S.fd : 0; S.uy = S.fd ? (S.ty - S.cy) / S.fd : 0;
  S.primary = S.fd < 1; S.smooth = 0; S.peak = 0; S.spawn = now;
}
function stepFlick(dt, now, moved) {
  if (!S.primary) {
    // the first big movement ends when speed drops to 15% of its peak; where it stopped shows over/undershoot
    S.smooth += (moved / dt - S.smooth) * Math.min(1, dt / .025);
    if (S.smooth > S.peak) S.peak = S.smooth;
    const along = (S.cx - S.sx) * S.ux + (S.cy - S.sy) * S.uy;
    if (along > .3 * S.fd && S.peak > 0 && S.smooth < .15 * S.peak) { S.primary = true; if (S.fd > 3 * S.tr) S.progress.push(along / S.fd); }
  }
  const to = FlickTimeout[S.diff];
  if (to > 0 && now - S.spawn > to) { S.expired++; spawnTarget(now); }
}
function flickClick(now) {
  if (hitShape(S.cx - S.tx, S.cy - S.ty, S.tr)) {
    S.hits++; S.hitTimes.push(now - S.spawn);
    if (!S.primary && S.fd > 3 * S.tr) S.progress.push(((S.cx - S.sx) * S.ux + (S.cy - S.sy) * S.uy) / S.fd);
    spawnTarget(now);
  } else S.misses++;
}

// bots
function spawnBot(now) {
  const sc = [1, .75, .55][Math.floor(R() * 3)], a = S.arena;
  const b = { u: BotU[S.diff] * H * sc, hp: 3, flash: 0, dead: 0, spawn: now };
  b.ground = a.y + a.h * (.55 + .45 * (sc - .55) / .45);
  b.strafe = R() < BotShare[S.diff];
  for (let i = 0; i < 25; i++) {
    b.x = a.x + b.u + R() * (a.w - 2 * b.u);
    if (!S.bots.some(o => !o.dead && Math.abs(o.x - b.x) < (o.u + b.u) * 1.2 && Math.abs(o.ground - b.ground) < 2.6 * Math.max(o.u, b.u))) break;
  }
  b.origin = b.x; b.range = b.u * (2 + R() * 3); b.speed = BotSpd[S.diff] * b.u; b.vx = (R() < .5 ? -1 : 1) * b.speed; b.t = botDir();
  S.bots.push(b);
}
function botDir() { const lo = [.8, .5, .3, .15], hi = [1.8, 1.4, 1, .6]; return lo[S.diff] + R() * (hi[S.diff] - lo[S.diff]); }
function stepBots(dt, now, moved) {
  const a = S.arena;
  for (let i = S.bots.length - 1; i >= 0; i--) {
    const b = S.bots[i];
    if (b.dead) { b.dead -= dt; if (b.dead <= 0) { S.bots.splice(i, 1); spawnBot(now); } continue; }
    b.flash = Math.max(0, b.flash - dt);
    if (!b.strafe) continue;
    b.t -= dt;
    const nx = b.x + b.vx * dt, edge = nx < a.x + b.u || nx > a.x + a.w - b.u;
    if (b.t <= 0 || Math.abs(nx - b.origin) > b.range || edge) {
      const sp = S.diff === 3 ? b.speed * (.7 + R() * .7) : b.speed;
      b.vx = (b.vx > 0 ? -1 : 1) * sp; b.t = botDir();
      if (edge) b.origin = clamp(b.origin, a.x + b.u + b.range, a.x + a.w - b.u - b.range);
    } else b.x = nx;
  }
  if (S.path.length < 3000) S.path.push([S.cx, S.cy, moved / dt]);
  S.marks = S.marks.filter(m => (m.t -= dt) > 0);
}
function botPart(b, px, py) {
  const u = b.u, top = b.ground - 1.9 * u, hr = .31 * u, hc = top - .08 * u - hr, r = u / 2;
  if ((px - b.x) ** 2 + (py - hc) ** 2 <= hr * hr) return 2;
  if (Math.abs(px - b.x) <= r && py >= top + r && py <= b.ground - r) return 1;
  return (px - b.x) ** 2 + (py - top - r) ** 2 <= r * r || (px - b.x) ** 2 + (py - b.ground + r) ** 2 <= r * r ? 1 : 0;
}
function botClick(now) {
  S.shots++;
  for (const b of S.bots.filter(b => !b.dead).sort((p, q) => q.u - p.u)) {
    const part = botPart(b, S.cx, S.cy);
    if (!part) continue;
    S.botHits++; if (part === 2) S.headHits++;
    if (S.pathFresh) { analyzePath(S.cx, S.cy); S.pathFresh = false; }
    b.hp -= part === 2 ? 3 : 1; b.flash = .08;
    S.marks.push({ t: .12, head: part === 2 });
    if (b.hp <= 0) { b.dead = .25; S.kills++; if (part === 2) S.headKills++; S.ttk.push(now - b.spawn); startPath(); }
    return;
  }
}
function startPath() { S.path = []; S.px0 = S.cx; S.py0 = S.cy; S.pathFresh = true; }
function analyzePath(tx, ty) {
  const D = Math.hypot(tx - S.px0, ty - S.py0);
  if (D < H * .05 || S.path.length < 3) return;
  const ux = (tx - S.px0) / D, uy = (ty - S.py0) / D;
  let s = 0, peak = 0; const sm = S.path.map(p => (s += (p[2] - s) * .3, peak = Math.max(peak, s), s));
  if (peak <= 0) return;
  let along = 0;
  for (let i = 0; i < S.path.length; i++) {
    along = (S.path[i][0] - S.px0) * ux + (S.path[i][1] - S.py0) * uy;
    if (along > .3 * D && sm[i] < .15 * peak) { S.progress.push(along / D); return; }
  }
  S.progress.push(along / D);
}

// reaction: 0 intro, 1 red, 2 green, 3 showing a time, 4 false start
function reactionClick(now) {
  if (S.rState === 2) {
    S.lastR = (now - S.greenShown) * 1000; S.rTimes.push(S.lastR); S.rState = 3;
    if (S.rTimes.length >= 5) finishReaction();
  } else if (S.rState === 1) S.rState = 4;
  else { S.rState = 1; S.greenAt = now + 1.2 + R() * 3; }
}

// ---------- drawing ----------
function rr(x, y, w, h, r) { ctx.beginPath(); ctx.roundRect(x, y, w, h, r); }
function draw(now) {
  const g = GAMES[gameId], gp = GP[gameId];
  if (S.mode === RCT) return drawReaction();
  ctx.fillStyle = gp.bg; ctx.fillRect(0, 0, W, H);
  const a = S.arena;
  ctx.fillStyle = "rgba(255,255,255,.025)"; rr(a.x, a.y, a.w, a.h, 18); ctx.fill();
  ctx.strokeStyle = "rgba(255,255,255,.07)"; ctx.lineWidth = 1; ctx.stroke();
  if (S.mode === FLK) {
    if (S.countdown <= 0) {
      drawTarget(S.tx, S.ty, S.tr, false);
      const to = FlickTimeout[S.diff];
      if (to > 0) { const frac = Math.max(0, 1 - (now - S.spawn) / to); ctx.strokeStyle = "rgba(236,239,243,.65)"; ctx.lineWidth = 2; ctx.beginPath(); ctx.arc(S.tx, S.ty, S.tr + 7, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * frac); ctx.stroke(); }
    }
  } else if (S.mode === BOT) [...S.bots].sort((p, q) => p.u - q.u).forEach(drawBot);
  else drawTarget(S.bx, S.by + S.jumpOff, S.br, S.countdown <= 0 && hitShape(S.cx - S.bx, S.cy - (S.by + S.jumpOff), S.br));

  // HUD
  ctx.font = "600 12px 'Chakra Petch', sans-serif"; ctx.fillStyle = "#8592a6"; ctx.textAlign = "left";
  ctx.fillText((g.name + "  ·  " + (S.mode === FND ? "SENS FINDER  ·  ROUND " + Math.min(5, S.fIdx + 1) + " OF 5" : S.mode.toUpperCase() + "  ·  " + DIFFS[S.diff].toUpperCase())).toUpperCase(), 24, H - 24);
  const bw = Math.min(400, W * .4), bx = (W - bw) / 2;
  ctx.fillStyle = "rgba(255,255,255,.1)"; rr(bx, 24, bw, 6, 3); ctx.fill();
  ctx.fillStyle = g.color; rr(bx, 24, bw * Math.max(0, S.time / S.len), 6, 3); ctx.fill();
  ctx.font = "700 16px 'Chakra Petch', sans-serif"; ctx.fillStyle = "#eaf0f6"; ctx.textAlign = "center";
  ctx.fillText(Math.max(0, S.time).toFixed(1), W / 2, 52);
  const score = S.mode === FLK ? S.hits + " hits  ·  " + (S.hits + S.misses ? Math.round(S.hits * 100 / (S.hits + S.misses)) + "% accuracy" : "–")
    : S.mode === BOT ? S.kills + " kills  ·  " + (S.botHits ? Math.round(S.headHits * 100 / S.botHits) + "% headshots" : "–")
    : (S.tot ? Math.round(S.on / S.tot * 100) : 0) + "% on target";
  ctx.textAlign = "right"; ctx.fillText(score, W - 24, 34);
  ctx.textAlign = "center"; ctx.font = "13px Barlow, sans-serif"; ctx.fillStyle = "#8592a6"; ctx.fillText("Esc to stop", W / 2, H - 24);
  if (rawInput === false) { ctx.textAlign = "left"; ctx.fillText("Your browser adds mouse acceleration. Use Chrome or Edge for exact sens.", 24, 34); }
  if (S.countdown > 0) {
    ctx.fillStyle = "#eaf0f6"; ctx.font = "700 110px 'Chakra Petch', sans-serif"; ctx.textAlign = "center";
    ctx.fillText(String(Math.ceil(S.countdown)), W / 2, H * .3);
    ctx.font = "16px Barlow, sans-serif"; ctx.fillStyle = "#8592a6";
    ctx.fillText(S.mode === FND && S.fIdx > 0 ? "Next round, new hidden sensitivity" : S.mode === FLK ? "Click the targets as they appear" : S.mode === BOT ? "Body shots do 1 damage. Headshots kill instantly." : "Keep your crosshair on the ball", W / 2, H * .3 + 36);
  }
  drawCrosshair(S.cx, S.cy);
  if (S.mode === BOT && S.marks.length) {
    const m = S.marks[S.marks.length - 1]; ctx.strokeStyle = m.head ? "#ff5a5a" : "#fff"; ctx.lineWidth = 2;
    for (const [sx, sy] of [[-1, -1], [1, -1], [-1, 1], [1, 1]]) { ctx.beginPath(); ctx.moveTo(S.cx + sx * 7, S.cy + sy * 7); ctx.lineTo(S.cx + sx * 13, S.cy + sy * 13); ctx.stroke(); }
  }
}
function drawTarget(x, y, r, on) {
  const c = GAMES[gameId].color;
  ctx.fillStyle = on ? "#fff" : c;
  if (GP[gameId].square) {
    ctx.fillRect(x - r, y - r, 2 * r, 2 * r);
    ctx.fillStyle = "rgba(0,0,0,.25)"; ctx.fillRect(x - r + 1.1 * r, y - r + .3 * r, .6 * r, .6 * r);
  } else {
    ctx.beginPath(); ctx.arc(x, y, r, 0, 7); ctx.fill();
    if (on) { ctx.fillStyle = c; ctx.beginPath(); ctx.arc(x, y, r * .8, 0, 7); ctx.fill(); }
    ctx.fillStyle = "rgba(255,255,255,.25)"; ctx.beginPath(); ctx.ellipse(x - r * .25, y - r * .35, r * .3, r * .2, 0, 0, 7); ctx.fill();
  }
}
function drawBot(b) {
  const k = b.dead ? b.dead / .25 : 1, u = b.u * (.6 + .4 * k), bodyH = 1.9 * u, hr = .31 * u, top = b.ground - bodyH, hc = top - .08 * u - hr;
  ctx.globalAlpha = k;
  ctx.fillStyle = "rgba(0,0,0,.3)"; ctx.beginPath(); ctx.ellipse(b.x, b.ground, u * .6, u * .1, 0, 0, 7); ctx.fill();
  const grad = ctx.createLinearGradient(b.x - u / 2, 0, b.x + u / 2, 0);
  grad.addColorStop(0, "#5ff6f7"); grad.addColorStop(.55, "#19e6e8"); grad.addColorStop(1, "#0a8f9e");
  ctx.fillStyle = b.flash > 0 ? "#fff" : grad;
  rr(b.x - u / 2, top, u, bodyH, u / 2); ctx.fill();
  ctx.beginPath(); ctx.arc(b.x, hc, hr, 0, 7); ctx.fill();
  if (!b.dead) {
    const bw = u * .9, bh = Math.max(3, u * .07), seg = (bw - 4) / 3, y = hc - hr - bh - 6;
    for (let i = 0; i < 3; i++) { ctx.fillStyle = i < b.hp ? "rgba(255,255,255,.95)" : "rgba(0,0,0,.4)"; ctx.fillRect(b.x - bw / 2 + i * (seg + 2), y, seg, bh); }
  }
  ctx.globalAlpha = 1;
}
function drawCrosshair(x, y) {
  const lines = [[x - 13, y, x - 4, y], [x + 4, y, x + 13, y], [x, y - 13, x, y - 4], [x, y + 4, x, y + 13]];
  for (const [w, c] of [[4, "rgba(0,0,0,.75)"], [2, "#fff"]]) { ctx.lineWidth = w; ctx.strokeStyle = c; lines.forEach(l => { ctx.beginPath(); ctx.moveTo(l[0], l[1]); ctx.lineTo(l[2], l[3]); ctx.stroke(); }); }
  ctx.fillStyle = "#fff"; ctx.fillRect(x - 1, y - 1, 2, 2);
}
function drawReaction() {
  const n = S.rTimes.length;
  const st = { 1: ["#b02634", "Wait for green…", "Try " + (n + 1) + " of 5"], 2: ["#22aa58", "CLICK!", ""],
    3: [GP[gameId].bg, Math.round(S.lastR) + " ms", "Click or press Space for try " + (n + 1) + " of 5"],
    4: ["#c8782a", "Too soon!", "Wait for the green screen. Click to try again."] }[S.rState]
    || [GP[gameId].bg, "Reaction test", "Click or press Space to start. When the screen turns green, click as fast as you can."];
  ctx.fillStyle = st[0]; ctx.fillRect(0, 0, W, H);
  ctx.fillStyle = "#fff"; ctx.textAlign = "center";
  ctx.font = "700 " + (S.rState === 3 ? 96 : 56) + "px 'Chakra Petch', sans-serif"; ctx.fillText(st[1], W / 2, H / 2);
  ctx.font = "17px Barlow, sans-serif"; ctx.fillStyle = "rgba(255,255,255,.88)"; ctx.fillText(st[2], W / 2, H / 2 + 46);
  if (n) { ctx.font = "600 16px 'Chakra Petch', sans-serif"; ctx.fillText("So far: " + S.rTimes.map(t => Math.round(t)).join("  ·  ") + " ms", W / 2, H - 70); }
  ctx.font = "13px Barlow, sans-serif"; ctx.fillStyle = "rgba(255,255,255,.7)"; ctx.fillText("Esc to stop", W / 2, H - 28);
}

// ---------- results ----------
function roundOver() {
  if (S.mode === FND) {
    S.fScores[S.fIdx] = S.tot ? S.on / S.tot * 100 : 0;
    S.fIdx++;
    if (S.fIdx < 5) {
      S.mult = S.fMults[S.fIdx]; S.ppc = pxPerCount(S.mult);
      resetBall(); S.on = S.tot = S.leadSum = S.leadTime = S.crossPath = S.ballPath = 0;
      S.time = S.len; S.countdown = 2; return;
    }
    return finishFinder();
  }
  if (S.mode === FLK) finishFlick(); else if (S.mode === BOT) finishBots(); else finishTracking();
}
function endRound() {
  S.active = false;
  if (document.pointerLockElement) document.exitPointerLock();
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
}
function suggest(m) {
  if (m === 1) return null;
  const cur = sensOf(gameId), ns = roundSens(gameId, sensFor(gameId, degPerCount(gameId, cur) * m));
  return ns > 0 && ns !== roundSens(gameId, cur) ? ns : null;
}
function flickCoach(notEnough) {
  const p = S.progress;
  if (p.length < 6) return { text: notEnough, m: 1 };
  const avg = p.reduce((s, v) => s + v, 0) / p.length, over = Math.round(p.filter(v => v > 1.05).length * 100 / p.length);
  if (avg > 1.06) return { m: 1 - clamp((avg - 1) * .7, .05, .2), text: "Your first flick landed at " + Math.round(avg * 100) + "% of the distance on average, so you tend to fly past the target (" + over + "% of flicks). That usually means your sensitivity is a bit high." };
  if (avg < .86) return { m: 1 + clamp((1 - avg) * .6, .05, .2), text: "Your first flick landed at " + Math.round(avg * 100) + "% of the distance on average, so you usually stop short and need a second correction. That usually means your sensitivity is a bit low." };
  return { m: 1, text: "Your first flick landed at " + Math.round(avg * 100) + "% of the distance on average, which is close to ideal. Your sensitivity looks right for flicking." };
}
const title = () => GAMES[gameId].name + " · " + S.mode + (S.mode === RCT || S.mode === FND ? "" : " · " + DIFFS[S.diff]);

function finishTracking() {
  endRound();
  const pct = S.tot ? S.on / S.tot * 100 : 0, hist = history(TRK, S.diff), pb = saveRecord(TRK, S.diff, pct, false);
  let c = { m: 1, text: "Your tracking looks balanced: you're not over-correcting and not falling behind. Your sensitivity suits you here." };
  if (S.leadTime < 3) c.text = "Not enough close tracking this round to judge your sensitivity. Try to stay on the ball, or drop the difficulty a level.";
  else {
    const ratio = S.ballPath ? S.crossPath / S.ballPath : 1, lead = S.leadSum / S.leadTime;
    if (ratio > 1.35) c = { m: 1 - clamp((ratio - 1.2) * .5, .05, .2), text: "Your crosshair travelled " + ratio.toFixed(1) + "x as far as the ball did. All those small over-corrections usually mean your sensitivity is a bit high." };
    else if (lead < -1 && ratio < 1.15) c = { m: 1 + clamp((-lead - .8) * .15, .05, .2), text: "You were usually trailing about " + (-lead / 2).toFixed(1) + " ball-widths behind the ball. That usually means your sensitivity is a bit low, so it's hard to keep up when it changes direction." };
  }
  showResults({ main: pct.toFixed(1) + "%", label: "of the time on target", pb, coach: c.text, sug: suggest(c.m),
    lines: [hist.length ? "Previous best: " + Math.max(...hist.map(r => r.s)).toFixed(1) + "%" : null, "Rounds played at this difficulty: " + (hist.length + 1)],
    code: makeCode("T", S.diff, pct.toFixed(1)) });
}
function finishFlick() {
  endRound();
  const shots = S.hits + S.misses, hist = history(FLK, S.diff), pb = saveRecord(FLK, S.diff, S.hits, false), c = flickCoach("Not enough flicks this round to judge your sensitivity. Hit at least 6 targets.");
  showResults({ main: String(S.hits), label: "targets hit in 30 seconds", pb, coach: c.text, sug: suggest(c.m),
    lines: ["Accuracy: " + (shots ? Math.round(S.hits * 100 / shots) : 0) + "%  (" + S.misses + " missed clicks)",
      S.hitTimes.length ? "Average time per target: " + Math.round(S.hitTimes.reduce((s, v) => s + v, 0) / S.hitTimes.length * 1000) + " ms" : null,
      S.expired ? S.expired + " targets vanished before you hit them" : null,
      hist.length ? "Previous best: " + Math.max(...hist.map(r => r.s)) + " hits" : null],
    code: makeCode("F", S.diff, String(S.hits)) });
}
function finishBots() {
  endRound();
  const hist = history(BOT, S.diff), pb = saveRecord(BOT, S.diff, S.kills, false), c = flickCoach("Not enough kills this round to judge your sensitivity. Get at least 7 kills.");
  showResults({ main: String(S.kills), label: "bots eliminated in 30 seconds", pb, coach: c.text, sug: suggest(c.m),
    lines: ["Headshots: " + (S.botHits ? Math.round(S.headHits * 100 / S.botHits) : 0) + "% of hits  (" + S.headKills + " headshot kills)",
      "Accuracy: " + (S.shots ? Math.round(S.botHits * 100 / S.shots) : 0) + "%  (" + S.shots + " shots)",
      S.ttk.length ? "Average time to kill: " + Math.round(S.ttk.reduce((s, v) => s + v, 0) / S.ttk.length * 1000) + " ms" : null,
      hist.length ? "Previous best: " + Math.max(...hist.map(r => r.s)) + " kills" : null],
    code: makeCode("B", S.diff, String(S.kills)) });
}
function finishReaction() {
  endRound();
  const t = S.rTimes, avg = t.reduce((s, v) => s + v, 0) / t.length, hist = history(RCT, 0), pb = saveRecord(RCT, 0, avg, true);
  const rating = avg < 170 ? "Outstanding. That's pro-level reaction speed." : avg < 210 ? "Great. Faster than most players." : avg < 250 ? "Good. Right around the typical gamer range."
    : avg < 300 ? "Average. Most people land around 200 to 280 ms." : "A bit slow this time. Being tired or a laggy screen adds time, so try again later.";
  showResults({ main: Math.round(avg) + " ms", label: "average over 5 tries", pb, coachHead: "How you did", coach: rating + " These times include your mouse and monitor delay, so they run a little higher than in-game feel.", sug: null,
    lines: ["Fastest: " + Math.round(Math.min(...t)) + " ms", "Tries: " + t.map(x => Math.round(x)).join("  ·  ") + " ms", hist.length ? "Previous best average: " + Math.round(Math.min(...hist.map(r => r.s))) + " ms" : null],
    code: makeCode("R", 0, String(Math.round(avg))) });
}
function finishFinder() {
  endRound();
  const order = [0, 1, 2, 3, 4].sort((a, b) => S.fMults[a] - S.fMults[b]), ms = order.map(i => S.fMults[i]), sc = order.map(i => S.fScores[i]);
  const k = sc.indexOf(Math.max(...sc)); let best = ms[k];
  if (k > 0 && k < 4) { const den = sc[k + 1] - 2 * sc[k] + sc[k - 1]; if (den < 0) best = ms[k] - .5 * .1 * (sc[k + 1] - sc[k - 1]) / den; best = clamp(best, ms[k] - .1, ms[k] + .1); }
  const spread = Math.max(...sc) - Math.min(...sc), sug = spread < 3 ? null : suggest(best);
  const chart = el("div", { class: "finder-chart" });
  ms.forEach((m, i) => chart.append(el("div", { class: "fbar" + (i === k ? " best" : "") }, [
    el("span", { class: "fval", text: Math.round(sc[i]) + "%" }), el("i", { style: "height:" + Math.max(4, sc[i] / Math.max(1, Math.max(...sc)) * 100) + "%" }),
    el("span", { class: "flabel", text: fmtSens(gameId, sensFor(gameId, degPerCount(gameId, sensOf(gameId)) * m)) })])));
  showResults({ main: fmtSens(gameId, sug || sensOf(gameId)), label: "the sensitivity you tracked best with", pb: false, sug, chart,
    coach: spread < 3 ? "Your scores were within 3% of each other at every sensitivity, so your current sens is fine. Pick whatever feels most comfortable."
      : "You scored " + Math.round(Math.max(...sc)) + "% at best and " + Math.round(Math.min(...sc)) + "% at worst across the five rounds." + (k === 0 || k === 4 ? " Your best round was at the edge of the range tested, so switch to this sens and run the finder again to fine-tune it." : ""),
    lines: [], code: null });
}

let lastResult = null;
function showResults(r) {
  lastResult = r;
  show("results");
  $("res-title").textContent = title();
  $("res-main").textContent = r.main; $("res-main").style.color = GAMES[gameId].color;
  $("res-label").textContent = r.label;
  $("res-pb").hidden = !r.pb;
  const lines = $("res-lines"); lines.textContent = "";
  r.lines.filter(Boolean).forEach(l => lines.append(el("div", { text: l })));
  if (r.chart) lines.append(r.chart);
  $("coach-head").textContent = r.coachHead || "Sensitivity coach";
  $("coach-text").textContent = r.coach;
  const sug = $("coach-sug"); sug.textContent = "";
  if (r.sug != null) {
    const cur = fmtSens(gameId, sensOf(gameId)), next = fmtSens(gameId, r.sug);
    sug.append(el("div", { class: "big", style: "color:" + GAMES[gameId].color, text: "Try " + next }), el("div", { class: "muted", text: "now " + cur }),
      el("div", { class: "row", style: "margin-top:10px" }, [
        el("button", { class: "btn primary", text: "Use " + next, onclick: () => { settings[gameId] = { sens: r.sug, dpi: dpiOf(gameId) }; store.set("settings", settings);
          sug.textContent = ""; sug.append(el("div", { class: "msg okm", text: "Saved. You're now practising at " + next + ". Set the same number in " + GAMES[gameId].name + "." })); } }),
        el("button", { class: "btn", text: "Keep " + cur, onclick: () => { sug.textContent = ""; } })]));
  } else if (S.mode !== RCT) sug.append(el("div", { class: "muted", text: "Current sens: " + fmtSens(gameId, sensOf(gameId)) }));
  const codeBox = $("res-code"); codeBox.hidden = !r.code;
  $("code-text").textContent = r.code || "";
  $("code-msg").textContent = "";
  renderSubmit();
}
function renderSubmit() {
  const box = $("submit-row"); box.textContent = "";
  if (!lastResult || !lastResult.code) return;
  if (!configured()) { box.append(el("span", { class: "muted", text: "The leaderboard isn't open yet." })); return; }
  if (!AT.session) { box.append(el("button", { class: "btn primary", text: "Log in to submit", onclick: login })); return; }
  box.append(el("button", { class: "btn primary", id: "submit-now", text: "Submit to leaderboard", onclick: submitNow }));
}
async function submitNow() {
  const btn = $("submit-now"), msg = $("code-msg");
  btn.disabled = true; msg.textContent = "";
  const r = await AT.sb.rpc("submit_score", { p_code: lastResult.code, p_clip_url: null, p_clip_path: null });
  if (r.error) { btn.disabled = false; msg.append(el("div", { class: "msg err", text: r.error.message })); return; }
  btn.remove();
  msg.append(el("div", { class: "msg okm" }, ["On the leaderboard as Unverified. Want the ✓? ",
    el("a", { href: "board.html#submit", text: "Add a clip in Your runs" }), " (reviewed in 1–3 business days)."]));
}
$("copy-code").onclick = async () => {
  try { await navigator.clipboard.writeText(lastResult.code); $("copy-code").textContent = "Copied!"; }
  catch { getSelection().selectAllChildren($("code-text")); $("copy-code").textContent = "Press Ctrl+C"; }
  setTimeout(() => $("copy-code").textContent = "Copy code", 1800);
};
$("again").onclick = () => prepare(S.mode);
$("back").onclick = () => show("game");
$("to-home").onclick = () => show("home");
$("edit-profile").onclick = () => { renderSetup(); show("setup"); };
$("stage-cancel").onclick = () => { S = null; if (CAL) endCalibration(); else show("game"); };

// ---------- Mac app extras: Discord status and update check ----------
function presenceText() {
  const vis = v => !$("v-" + v).hidden, g = gameId && GAMES[gameId];
  const run = g && S ? g.name + " · " + (S.mode === FND ? "Sens finder" : S.mode === RCT ? "Reaction test" : S.mode + " · " + DIFFS[S.diff]) : "";
  if (!$("stage").hidden && S && S.active) {
    let st = S.countdown > 0 ? "Get ready…"
      : S.mode === FLK ? S.hits + " hits · " + (S.hits + S.misses ? Math.round(S.hits * 100 / (S.hits + S.misses)) : 0) + "% accuracy"
      : S.mode === BOT ? S.kills + " kills · " + (S.botHits ? Math.round(S.headHits * 100 / S.botHits) : 0) + "% headshots"
      : S.mode === RCT ? "Try " + Math.min(5, S.rTimes.length + 1) + " of 5"
      : S.mode === FND ? "Round " + Math.min(5, S.fIdx + 1) + " of 5"
      : (S.tot ? Math.round(S.on / S.tot * 100) : 0) + "% on target";
    return { details: run, status: st, game: gameId };
  }
  if (vis("results") && lastResult && S) {
    const m = lastResult.main;
    const st = S.mode === TRK ? m + " on target" : S.mode === FLK ? m + " hits" : S.mode === BOT ? m + " kills" : S.mode === RCT ? m + " average" : "Best sens " + m;
    return { details: run + " · results", status: st, game: gameId };
  }
  if (vis("game") && g) return { details: g.name + " · choosing a mode", status: "Sens " + fmtSens(gameId, sensOf(gameId)), game: gameId };
  if (vis("setup")) return { details: "Setting up", status: "", game: "" };
  return { details: "In the menus", status: "", game: "" };
}
if (TAURI) {
  let sent = null;
  // Discord allows about 5 updates per 20 seconds, so check every 4 seconds and only send changes.
  setInterval(() => {
    const p = presenceText(), key = JSON.stringify(p);
    if (key === sent) return;
    sent = key;
    TAURI.core.invoke("set_presence", p).catch(() => { sent = null; });
  }, 4000);
  (async () => {
    try {
      const mine = await TAURI.app.getVersion(), rs = await releases(), latest = rs[0] && rs[0].tag_name.replace(/^v/, "");
      const newer = latest && latest.split(".").map(Number).reduce((c, n, i) => c !== 0 ? c : n - (+mine.split(".")[i] || 0), 0) > 0;
      if (newer) $("update-banner").hidden = false, $("update-text").textContent = "Update available: v" + latest + " (you have v" + mine + ")";
    } catch {}
  })();
}

// ---------- start ----------
(async () => {
  await initAuth();
  AT.listeners.push(renderSubmit);
  if (!profile) { renderSetup(); show("setup"); }
  else if (gameId && GAMES[gameId] && location.hash !== "#home") show("game");
  else show("home");
})();
})();
