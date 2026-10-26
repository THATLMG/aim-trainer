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
const TRK = "Tracking", FLK = "Flick", BOT = "Bots", RCT = "Reaction", FND = "Sens finder", HTK = "Health Track";
const LETTER = { Tracking: "T", Flick: "F", Bots: "B", Reaction: "R", "Health Track": "H" };
const MODE_DESC = {
  [TRK]: "Keep your crosshair on a moving ball. Score is the % of time on target.",
  [HTK]: "No clicking: stay on the ball to drain its health. Kill it and a new one appears.",
  [FLK]: "Snap to targets as they pop up and click. 30 seconds.",
  [BOT]: "Strafing and standing bots with 3 HP. Headshots kill instantly.",
  [RCT]: "Click the moment the screen turns green. 5 tries.",
  [FND]: "5 short tracking rounds at hidden sens values. Finds the one you aim best with."
};
const BallR = [.045, .033, .024, .017], BallSpeed = [.22, .36, .5, .65];
const FlickR = [.042, .032, .024, .017], FlickMin = [.12, .16, .2, .25], FlickMax = [.28, .36, .45, .5], FlickTimeout = [0, 0, 1.6, 1.0];
const DrainRate = [110, 80, 65, 55];   // health per second while your crosshair is on the ball
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
// Optional mouse acceleration, same curve as the Windows app: speed in counts per ms at 800 DPI.
let accelOn = store.get("accel", false), lastMoveAt = 0;
function accelMult(mx, my, t) {
  const dtMs = clamp(t - lastMoveAt, 1, 50);
  lastMoveAt = t;
  if (!accelOn || (!mx && !my)) return 1;
  const speed = Math.hypot(mx, my) / dtMs * 800 / dpiOf(gameId);
  return Math.min(2.5, 1 + .25 * speed);
}

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
const views = ["setup", "home", "game", "loadout", "results"];
function show(v) {
  views.forEach(x => $("v-" + x).hidden = x !== v);
  $("stage").hidden = true;
  scrollTo(0, 0);
  if (v === "home") renderHome();
  if (v === "game") renderGame();
  if (v === "loadout") renderLoadout();
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
  $("accel").checked = accelOn;
  updateCm();
  const modes = $("modes"); modes.textContent = "";
  for (const m of [TRK, HTK, FLK, BOT, RCT, FND])
    modes.append(el("button", { class: "mode-card", "aria-pressed": String(mode === m), style: "--c:" + g.color, onclick: () => { mode = m; renderGame(); } },
      [el("b", { text: m }), el("span", { text: MODE_DESC[m] })]));
  const dd = $("diffs"); dd.textContent = "";
  DIFFS.forEach((name, i) => dd.append(el("button", { class: "chip", "aria-pressed": String(diff === i), style: "--c:" + g.color, onclick: () => { diff = i; renderGame(); } }, [el("span", { text: name })])));
  $("diff-wrap").hidden = !(mode === TRK || mode === HTK || mode === FLK || mode === BOT);
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
$("accel").addEventListener("change", e => { accelOn = e.target.checked; store.set("accel", accelOn); });
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
function fmtRes(m, v) { return m === TRK ? v.toFixed(1) + "%" : m === FLK ? Math.round(v) + " hits" : m === BOT || m === HTK ? Math.round(v) + " kills" : Math.round(v) + " ms"; }
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
        on: 0, tot: 0, leadSum: 0, leadTime: 0, crossPath: 0, ballPath: 0, rState: 0, rTimes: [], greenAt: 0, greenShown: 0, last: 0,
        hp: 100, htKills: 0, htTtk: [], htSpawn: 0, fx: [], floats: [], feed: [], combo: 0, bestCombo: 0, comboPop: 0, shake: 0, flash: 0, recoil: 0, muzzle: 0, sway: 0 };
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
  const f = moveFactor(), a = accelMult(e.movementX * f, e.movementY * f, e.timeStamp);
  dx += e.movementX * f * a; dy += e.movementY * f * a;
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
  stepFx(dt, dx);
  dx = dy = 0;
  if (S.countdown > 0) {
    S.countdown -= dt;
    if (S.countdown <= 0) {
      S.countdown = 0;
      if (S.mode === FLK) spawnTarget(now);
      if (S.mode === BOT) { S.bots.forEach(b => b.spawn = now); startPath(); }
      if (S.mode === HTK) S.htSpawn = now;
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
  else if (S.mode === HTK) stepHealth(dt, now, moved);
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
  if (to > 0 && now - S.spawn > to) { S.expired++; S.combo = 0; spawnTarget(now); }
}
function flickClick(now) {
  fire();
  if (hitShape(S.cx - S.tx, S.cy - S.ty, S.tr)) {
    S.hits++; S.hitTimes.push(now - S.spawn);
    if (!S.primary && S.fd > 3 * S.tr) S.progress.push(((S.cx - S.sx) * S.ux + (S.cy - S.sy) * S.uy) / S.fd);
    burst(S.tx, S.ty, GAMES[gameId].color, 16, 380, .5);
    addCombo();
    if (now - S.spawn < .35) addFloat(S.tx, S.ty - S.tr - 10, "QUICK!", "#ffd34e");
    sound(1);
    spawnTarget(now);
  } else { S.misses++; S.combo = 0; }
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
  fire();
  for (const b of S.bots.filter(b => !b.dead).sort((p, q) => q.u - p.u)) {
    const part = botPart(b, S.cx, S.cy);
    if (!part) continue;
    S.botHits++; if (part === 2) S.headHits++;
    if (S.pathFresh) { analyzePath(S.cx, S.cy); S.pathFresh = false; }
    b.hp -= part === 2 ? 3 : 1; b.flash = .08;
    S.marks.push({ t: .12, head: part === 2 });
    burst(S.cx, S.cy, part === 2 ? "#ff5a5a" : "#19e6e8", part === 2 ? 10 : 6, 300, .35);
    addCombo();
    if (b.hp <= 0) {
      b.dead = .25; S.kills++; if (part === 2) S.headKills++; S.ttk.push(now - b.spawn); startPath();
      burst(b.x, b.ground - b.u, "#19e6e8", 30, 480, .7);
      addFeed(part === 2 ? "Headshot kill" : "Kill", part === 2);
      shake(part === 2 ? .16 : .1);
      if (part === 2) { addFloat(S.cx, S.cy - 26, "HEADSHOT", "#ff4655"); S.flash = .08; sound(3); } else sound(2);
    } else sound(1);
    return;
  }
  S.combo = 0;
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

// health track: tracking where staying on the ball drains its health
function stepHealth(dt, now, moved) {
  stepTracking(dt, moved);
  const by = S.by + S.jumpOff;
  if (!hitShape(S.cx - S.bx, S.cy - by, S.br)) return;
  S.hp -= DrainRate[S.diff] * dt;
  if (R() < dt * 30) burst(S.bx + (R() - .5) * S.br, by + (R() - .5) * S.br, GAMES[gameId].color, 1, 120, .25);
  if (S.hp > 0) return;
  S.htKills++; S.htTtk.push(now - S.htSpawn);
  burst(S.bx, by, GAMES[gameId].color, 26, 420, .6); burst(S.bx, by, "#ffffff", 8, 260, .35);
  addFeed("Target down" + (now - S.htSpawn < 1.2 ? "  ·  fast!" : ""), false);
  shake(.12); sound(2); addCombo();
  const a = S.arena, jh = GP[gameId].jumps ? H * .1 : 0;
  S.hp = 100; S.htSpawn = now;
  S.bx = a.x + S.br + R() * (a.w - 2 * S.br);
  S.by = a.y + S.br + jh + R() * Math.max(1, a.h - 2 * S.br - jh);
  S.jumpOff = S.jumpV = 0;
  const ang = R() * Math.PI * 2; S.dirX = Math.cos(ang); S.dirY = Math.sin(ang); S.heading = ang;
}

// ---------- effects ----------
let loadout = Object.assign({ gun: "warden", skin: "standard", showGun: true, sound: true, shake: true }, store.get("loadout", {}));
let progressStore = Object.assign({ bestRank: -1, bestBotKills: 0 }, store.get("progress", {}));
const saveLoadout = () => store.set("loadout", loadout);

function burst(x, y, color, n, speed, life) {
  for (let i = 0; i < n; i++) {
    const a = R() * Math.PI * 2, sp = speed * (.3 + R() * .7);
    S.fx.push({ x, y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - speed * .3, life, max: life, size: 2 + R() * 3, color });
  }
}
const addFloat = (x, y, text, color) => S.floats.push({ x, y, text, color, life: .7 });
function addFeed(text, head) { S.feed.unshift({ text, head, life: 3 }); if (S.feed.length > 5) S.feed.pop(); }
const shake = t => { if (loadout.shake) S.shake = Math.max(S.shake, t); };
function addCombo() { S.combo++; S.comboPop = 1; S.bestCombo = Math.max(S.bestCombo, S.combo); }
function fire() { S.recoil = Math.min(1.4, S.recoil + 1); S.muzzle = .05; }
function stepFx(dt, mdx) {
  S.fx = S.fx.filter(p => { p.life -= dt; p.vy += 900 * dt; p.x += p.vx * dt; p.y += p.vy * dt; return p.life > 0; });
  S.floats = S.floats.filter(f => { f.life -= dt; f.y -= 60 * dt; return f.life > 0; });
  S.feed = S.feed.filter(f => (f.life -= dt) > 0);
  S.comboPop = Math.max(0, S.comboPop - dt * 4); S.shake = Math.max(0, S.shake - dt); S.flash = Math.max(0, S.flash - dt);
  S.recoil *= Math.pow(.0005, dt); S.muzzle = Math.max(0, S.muzzle - dt);
  S.sway += (clamp(mdx, -40, 40) * .15 - S.sway) * Math.min(1, dt * 10);
}
let actx = null;
function sound(kind) {   // 1 = hit, 2 = kill, 3 = headshot
  if (!loadout.sound) return;
  try {
    actx = actx || new (window.AudioContext || window.webkitAudioContext)();
    const [f0, f1, len, vol] = { 1: [1500, 1100, .04, .18], 2: [520, 980, .11, .28], 3: [2100, 1700, .16, .24] }[kind];
    const o = actx.createOscillator(), g = actx.createGain(), t = actx.currentTime;
    o.frequency.setValueAtTime(f0, t); o.frequency.linearRampToValueAtTime(f1, t + len);
    g.gain.setValueAtTime(vol, t); g.gain.exponentialRampToValueAtTime(.001, t + len);
    o.connect(g); g.connect(actx.destination); o.start(t); o.stop(t + len + .02);
  } catch {}
}

// ---------- ranks ----------
const RANKS = ["Iron", "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Elite", "Champion"];
const RANK_COLORS = ["#82848c", "#c48248", "#c4cedc", "#f4c43a", "#38d2c4", "#6eaaff", "#c860ff", "#ff4c5c"];
function rankSteps(m, d) {
  const k = [1.15, 1, .85, .7][d] || 1;
  const t = m === TRK ? [25, 35, 45, 55, 65, 75, 85] : m === FLK ? [10, 16, 22, 28, 35, 42, 50] : m === BOT ? [5, 9, 13, 17, 22, 27, 33]
    : m === HTK ? [2, 4, 6, 8, 10, 13, 16] : m === RCT ? [330, 290, 260, 235, 210, 190, 170] : null;
  return !t ? null : m === RCT ? t : t.map(x => Math.round(x * k));
}
function rankIndex(m, d, score) {
  const t = rankSteps(m, d); if (!t) return -1;
  let r = 0; t.forEach((v, i) => { if (m === RCT ? score <= v : score >= v) r = i + 1; });
  return r;
}

// ---------- gun skins ----------
const SKINS = [
  { id: "standard", name: "Standard", body: "#40444c", trim: "#22242a", glow: "#828892" },
  { id: "neon", name: "Neon Pulse", body: "#12121a", trim: "#20202c", glow: "#19e6e8" },
  { id: "arctic", name: "Arctic", body: "#dee8f0", trim: "#8ca0b6", glow: "#6ec4ff" },
  { id: "crimson", name: "Crimson", body: "#7c121e", trim: "#3a0a10", glow: "#ff4655", botKills: 15, need: "Get 15 kills in one Bots round" },
  { id: "dragon", name: "Dragon Fire", body: "#46100a", trim: "#220806", glow: "#ff8c28", rank: 3, need: "Reach Gold rank in any mode" },
  { id: "glitch", name: "Glitch", body: "#1c0c2c", trim: "#0e0618", glow: "#ff3cdc", rank: 4, need: "Reach Platinum rank in any mode" },
  { id: "gold", name: "Gold", body: "#cea038", trim: "#805e1a", glow: "#ffeeaa", rank: 5, need: "Reach Diamond rank in any mode" },
  { id: "void", name: "Void", body: "#08080e", trim: "#040408", glow: "#aa5aff", rank: 7, need: "Reach Champion rank in any mode" }
];
const unlocked = s => (s.rank == null || progressStore.bestRank >= s.rank) && (!s.botKills || progressStore.bestBotKills >= s.botKills);
const currentSkin = () => { const s = SKINS.find(x => x.id === loadout.skin); return s && unlocked(s) ? s : SKINS[0]; };
function hexMix(a, b, t) {
  const p = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16)), x = p(a), y = p(b);
  return "rgb(" + x.map((v, i) => Math.round(v + (y[i] - v) * t)).join(",") + ")";
}
function hexA(h, a) { const p = [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16)); return "rgba(" + p.join(",") + "," + a + ")"; }

// Draws one of the two original guns pointing left, with the skin's colors and animated pattern.
function drawGunModel(c, px, py, k, angle, t, s, gun, flash, hands) {
  const kes = gun === "kestrel", muzzleX = kes ? -570 : -540, trim = hexMix(s.trim, "#969ca8", .22);
  const poly = (pts, fill) => { c.beginPath(); pts.forEach(([x, y], i) => i ? c.lineTo(x, y) : c.moveTo(x, y)); c.closePath(); if (fill) { c.fillStyle = fill; c.fill(); } };
  c.save(); c.translate(px, py); c.rotate(angle * Math.PI / 180); c.scale(k, k);
  if (hands) { poly([[-330, 40], [-268, 36], [-140, 340], [-280, 340]], "#2c303a"); poly([[40, 90], [110, 70], [320, 300], [160, 340]], "#2c303a"); }
  c.fillStyle = trim;
  poly(kes ? [[-146, 38], [-92, 38], [-92, 152], [-146, 152]] : [[-152, 44], [-96, 44], [-74, 140], [-136, 154]], trim);
  poly([[14, 38], [58, 38], [82, 126], [34, 132]], trim);
  if (kes) { c.beginPath(); c.roundRect(-575, -24, 245, 46, 18); c.fill(); }
  else { c.fillRect(-505, -12, 125, 18); c.fillRect(-545, -19, 45, 32); c.fillRect(-472, -38, 10, 26); }
  c.fillRect(-132, -64, 144, 22);
  const body = new Path2D();
  const add = pts => { pts.forEach(([x, y], i) => i ? body.lineTo(x, y) : body.moveTo(x, y)); body.closePath(); };
  add([[60, -34], [236, -48], [258, 46], [70, 34]]);
  add(kes ? [[-214, -36], [72, -40], [82, 38], [-204, 40]] : [[-204, -44], [72, -44], [84, 40], [-192, 46]]);
  if (kes) body.rect(-334, -28, 130, 54); else body.rect(-388, -32, 192, 62);
  c.fillStyle = s.body; c.fill(body, "nonzero");
  c.save(); c.clip(body, "nonzero"); skinPattern(c, s, t);
  const shade = c.createLinearGradient(0, -50, 0, 50); shade.addColorStop(0, "rgba(255,255,255,.2)"); shade.addColorStop(1, "rgba(0,0,0,.32)");
  c.fillStyle = shade; c.fillRect(-600, -60, 900, 120); c.restore();
  c.strokeStyle = hexMix(s.trim, "#000000", .3); c.lineWidth = 3; c.stroke(body);
  c.fillStyle = "rgba(0,0,0,.47)"; for (let i = 0; i < 5; i++) c.fillRect((kes ? -320 : -370) + i * 30, -10, 16, 6);
  if (hands) { c.fillStyle = "#1c1e24"; c.beginPath(); c.ellipse(-297, 27, 55, 31, 0, 0, 7); c.fill(); c.beginPath(); c.ellipse(53, 98, 43, 48, 0, 0, 7); c.fill(); }
  if (flash) {
    c.beginPath();
    for (let i = 0; i < 12; i++) { const a = i * Math.PI / 6, r = i % 2 ? 26 : 70; c.lineTo(muzzleX - 30 + Math.cos(a) * r * 1.4, -3 + Math.sin(a) * r); }
    c.closePath(); c.fillStyle = "rgba(255,210,110,.9)"; c.fill();
    c.fillStyle = "rgba(255,255,230,.95)"; c.beginPath(); c.ellipse(muzzleX - 30, -3, 22, 21, 0, 0, 7); c.fill();
  }
  c.restore();
}
function skinPattern(c, s, t) {
  if (s.id === "neon") { c.strokeStyle = hexA(s.glow, (140 + 100 * Math.sin(t * 4)) / 255); c.lineWidth = 4; for (const y of [-22, 2, 24]) { c.beginPath(); c.moveTo(-400, y); c.lineTo(250, y - 6); c.stroke(); } }
  else if (s.id === "arctic" || s.id === "crimson") { c.strokeStyle = s.id === "arctic" ? hexA(s.trim, .43) : hexA(s.glow, .67); c.lineWidth = 14; for (let x = -420; x < 300; x += 70) { c.beginPath(); c.moveTo(x, -60); c.lineTo(x + 60, 60); c.stroke(); } }
  else if (s.id === "dragon") { const off = t * 140 % 200; for (let x = -420 + off - 200; x < 300; x += 200) { const g = c.createLinearGradient(x, 0, x + 100, -40); g.addColorStop(0, hexA(s.glow, .86)); g.addColorStop(1, "rgba(255,40,0,0)"); c.fillStyle = g; c.fillRect(x, -60, 200, 120); } }
  else if (s.id === "glitch") { let seed = Math.floor(t * 12); const rr = () => ((seed = (seed * 9301 + 49297) % 233280) / 233280); for (let i = 0; i < 4; i++) { c.fillStyle = i % 2 ? "rgba(40,230,255,.6)" : hexA(s.glow, .6); c.fillRect(-420 + rr() * 500, -50 + rr() * 90, 60 + rr() * 160, 4 + rr() * 10); } }
  else if (s.id === "gold") { const x = -700 + t * 450 % 1300; c.fillStyle = "rgba(255,255,240,.47)"; c.beginPath(); c.moveTo(x, -60); c.lineTo(x + 60, -60); c.lineTo(x + 10, 60); c.lineTo(x - 50, 60); c.fill(); }
  else if (s.id === "void") { for (let i = 0; i < 26; i++) { c.fillStyle = i % 3 ? hexA(s.glow, Math.max(0, (140 + 110 * Math.sin(t * 3 + i)) / 255)) : "rgba(255,255,255," + Math.max(0, (140 + 110 * Math.sin(t * 3 + i)) / 255) + ")"; c.fillRect(-400 + (i * 97 % 640), -40 + (i * 53 % 80), 4, 4); } }
  else { c.strokeStyle = hexA(s.trim, .35); c.lineWidth = 3; c.beginPath(); c.moveTo(-180, -20); c.lineTo(40, -20); c.moveTo(-180, 20); c.lineTo(40, 20); c.stroke(); }
}
function drawHealthBar(x, y, w, frac, draining) {
  const bh = Math.max(5, w * .08), bx = x - w / 2;
  ctx.fillStyle = "rgba(0,0,0,.66)"; rr(bx - 2, y - 2, w + 4, bh + 4, 4); ctx.fill();
  let col = frac > .6 ? "#3ddc84" : frac > .3 ? "#f5b841" : "#ff5050";
  if (draining && Math.sin(performance.now() / 1000 * 30) > 0) col = hexMix(col, "#ffffff", .35);
  if (frac > 0) { ctx.fillStyle = col; rr(bx, y, w * Math.min(1, frac), bh, 3); ctx.fill(); }
}

// ---------- drawing ----------
function rr(x, y, w, h, r) { ctx.beginPath(); ctx.roundRect(x, y, w, h, r); }
function draw(now) {
  const g = GAMES[gameId], gp = GP[gameId];
  if (S.mode === RCT) return drawReaction();
  ctx.fillStyle = gp.bg; ctx.fillRect(0, 0, W, H);
  const glow = ctx.createRadialGradient(W / 2, H / 2, 0, W / 2, H / 2, Math.max(W, H) * .55);
  glow.addColorStop(0, hexA(g.color, .13)); glow.addColorStop(1, hexA(g.color, 0));
  ctx.fillStyle = glow; ctx.fillRect(0, 0, W, H);
  ctx.save();
  if (S.shake > 0) ctx.translate((R() - .5) * S.shake * 40, (R() - .5) * S.shake * 40);
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
  else {
    const onBall = S.countdown <= 0 && hitShape(S.cx - S.bx, S.cy - (S.by + S.jumpOff), S.br);
    drawTarget(S.bx, S.by + S.jumpOff, S.br, onBall);
    if (S.mode === HTK) drawHealthBar(S.bx, S.by + S.jumpOff - S.br - 14, S.br * 2.4, S.hp / 100, onBall);
  }
  for (const p of S.fx) { const k = p.life / p.max, s = p.size * (.5 + k * .5); ctx.globalAlpha = k; ctx.fillStyle = p.color; ctx.beginPath(); ctx.arc(p.x, p.y, s, 0, 7); ctx.fill(); }
  ctx.globalAlpha = 1;
  ctx.font = "700 18px 'Chakra Petch', sans-serif"; ctx.textAlign = "center";
  for (const f of S.floats) { ctx.globalAlpha = Math.min(1, f.life / .4); ctx.fillStyle = f.color; ctx.fillText(f.text, f.x, f.y); }
  ctx.globalAlpha = 1;
  ctx.restore();
  if ((S.mode === FLK || S.mode === BOT) && loadout.showGun && S.countdown <= 0) {
    const k = H / 1080 * .85;
    drawGunModel(ctx, W * .8 + (S.sway * 3 + S.recoil * 14) * k, H * .92 + (S.recoil * 26 + Math.sin(now * 1.6) * 3) * k, k, 12 + S.recoil * 5, now, currentSkin(), loadout.gun, S.muzzle > 0, true);
  }
  if (S.flash > 0) { ctx.fillStyle = "rgba(255,255,255," + (S.flash / .08 * .24) + ")"; ctx.fillRect(0, 0, W, H); }

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
    : S.mode === HTK ? S.htKills + " kills  ·  " + (S.tot ? Math.round(S.on / S.tot * 100) : 0) + "% on target"
    : (S.tot ? Math.round(S.on / S.tot * 100) : 0) + "% on target";
  ctx.textAlign = "right"; ctx.fillText(score, W - 24, 34);
  ctx.textAlign = "center"; ctx.font = "13px Barlow, sans-serif"; ctx.fillStyle = "#8592a6"; ctx.fillText("Esc to stop", W / 2, H - 24);
  if (rawInput === false && !calib) { ctx.textAlign = "left"; ctx.fillText("For exact sens, calibrate your mouse or use Chrome/Edge.", 24, H - 46); }
  if (S.combo >= 3) {
    const sc = 1 + S.comboPop * .35;
    ctx.textAlign = "center"; ctx.font = "700 " + Math.round(36 * sc) + "px 'Chakra Petch', sans-serif"; ctx.fillStyle = hexMix(g.color, "#ffffff", S.comboPop * .6);
    ctx.fillText("×" + S.combo, W / 2, 104 + 10 * sc);
    ctx.font = "600 11px 'Chakra Petch', sans-serif"; ctx.fillStyle = "#8592a6"; ctx.fillText("COMBO", W / 2, 128 + 12 * sc);
  }
  let fy = 54;
  for (const it of S.feed) {
    const alpha = Math.min(1, it.life / .5), txt = (it.head ? "HS  " : "»  ") + it.text;
    ctx.font = "13px Barlow, sans-serif"; const tw = ctx.measureText(txt).width + 20;
    ctx.globalAlpha = alpha; ctx.fillStyle = "rgba(0,0,0,.6)"; rr(W - 24 - tw, fy, tw, 26, 6); ctx.fill();
    if (it.head) { ctx.fillStyle = "#ff4655"; ctx.fillRect(W - 24 - tw, fy, 3, 26); }
    ctx.fillStyle = it.head ? "#ff7882" : "#eaf0f6"; ctx.textAlign = "center"; ctx.fillText(txt, W - 24 - tw / 2, fy + 17);
    ctx.globalAlpha = 1; fy += 30;
  }
  if (S.countdown > 0) {
    ctx.fillStyle = "#eaf0f6"; ctx.font = "700 110px 'Chakra Petch', sans-serif"; ctx.textAlign = "center";
    ctx.fillText(String(Math.ceil(S.countdown)), W / 2, H * .3);
    ctx.font = "16px Barlow, sans-serif"; ctx.fillStyle = "#8592a6";
    ctx.fillText(S.mode === FND && S.fIdx > 0 ? "Next round, new hidden sensitivity" : S.mode === FLK ? "Click the targets as they appear" : S.mode === BOT ? "Body shots do 1 damage. Headshots kill instantly." : S.mode === HTK ? "No clicking. Stay on the ball to drain its health." : "Keep your crosshair on the ball", W / 2, H * .3 + 36);
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
  if (S.mode === FLK) finishFlick(); else if (S.mode === BOT) finishBots(); else if (S.mode === HTK) finishHealth(); else finishTracking();
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

function trackingCoach() {
  let c = { m: 1, text: "Your tracking looks balanced: you're not over-correcting and not falling behind. Your sensitivity suits you here." };
  if (S.leadTime < 3) c.text = "Not enough close tracking this round to judge your sensitivity. Try to stay on the ball, or drop the difficulty a level.";
  else {
    const ratio = S.ballPath ? S.crossPath / S.ballPath : 1, lead = S.leadSum / S.leadTime;
    if (ratio > 1.35) c = { m: 1 - clamp((ratio - 1.2) * .5, .05, .2), text: "Your crosshair travelled " + ratio.toFixed(1) + "x as far as the ball did. All those small over-corrections usually mean your sensitivity is a bit high." };
    else if (lead < -1 && ratio < 1.15) c = { m: 1 + clamp((-lead - .8) * .15, .05, .2), text: "You were usually trailing about " + (-lead / 2).toFixed(1) + " ball-widths behind the ball. That usually means your sensitivity is a bit low, so it's hard to keep up when it changes direction." };
  }
  return c;
}
function finishTracking() {
  endRound();
  const pct = S.tot ? S.on / S.tot * 100 : 0, hist = history(TRK, S.diff), pb = saveRecord(TRK, S.diff, pct, false), c = trackingCoach();
  showResults({ main: pct.toFixed(1) + "%", score: pct, label: "of the time on target", pb, coach: c.text, sug: suggest(c.m),
    lines: [hist.length ? "Previous best: " + Math.max(...hist.map(r => r.s)).toFixed(1) + "%" : null, "Rounds played at this difficulty: " + (hist.length + 1)],
    code: makeCode("T", S.diff, pct.toFixed(1)) });
}
function finishHealth() {
  endRound();
  const pct = S.tot ? S.on / S.tot * 100 : 0, hist = history(HTK, S.diff), pb = saveRecord(HTK, S.diff, S.htKills, false), c = trackingCoach();
  showResults({ main: String(S.htKills), score: S.htKills, label: "targets tracked down in 30 seconds", pb, coach: c.text, sug: suggest(c.m),
    lines: ["Time on target: " + pct.toFixed(1) + "%", S.htTtk.length ? "Average time to kill: " + (S.htTtk.reduce((s, v) => s + v, 0) / S.htTtk.length).toFixed(2) + " s" : null,
      hist.length ? "Previous best: " + Math.max(...hist.map(r => r.s)) + " kills" : null],
    code: makeCode("H", S.diff, String(S.htKills)) });
}
function finishFlick() {
  endRound();
  const shots = S.hits + S.misses, hist = history(FLK, S.diff), pb = saveRecord(FLK, S.diff, S.hits, false), c = flickCoach("Not enough flicks this round to judge your sensitivity. Hit at least 6 targets.");
  showResults({ main: String(S.hits), score: S.hits, label: "targets hit in 30 seconds", pb, coach: c.text, sug: suggest(c.m),
    lines: ["Accuracy: " + (shots ? Math.round(S.hits * 100 / shots) : 0) + "%  (" + S.misses + " missed clicks)",
      S.hitTimes.length ? "Average time per target: " + Math.round(S.hitTimes.reduce((s, v) => s + v, 0) / S.hitTimes.length * 1000) + " ms" : null,
      S.expired ? S.expired + " targets vanished before you hit them" : null,
      hist.length ? "Previous best: " + Math.max(...hist.map(r => r.s)) + " hits" : null],
    code: makeCode("F", S.diff, String(S.hits)) });
}
function finishBots() {
  endRound();
  const hist = history(BOT, S.diff), pb = saveRecord(BOT, S.diff, S.kills, false), c = flickCoach("Not enough kills this round to judge your sensitivity. Get at least 7 kills.");
  showResults({ main: String(S.kills), score: S.kills, label: "bots eliminated in 30 seconds", pb, coach: c.text, sug: suggest(c.m),
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
  showResults({ main: Math.round(avg) + " ms", score: avg, label: "average over 5 tries", pb, coachHead: "How you did", coach: rating + " These times include your mouse and monitor delay, so they run a little higher than in-game feel.", sug: null,
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
  // ranks and skin unlocks
  const rank = r.score == null ? -1 : rankIndex(S.mode, S.diff, r.score);
  const before = SKINS.filter(unlocked).map(s => s.id);
  if (rank > progressStore.bestRank) progressStore.bestRank = rank;
  if (S.mode === BOT && S.kills > progressStore.bestBotKills) progressStore.bestBotKills = S.kills;
  store.set("progress", progressStore);
  const fresh = SKINS.filter(s => unlocked(s) && !before.includes(s.id));
  show("results");
  $("res-title").textContent = title();
  countUp($("res-main"), r);
  $("res-main").style.color = GAMES[gameId].color;
  $("res-label").textContent = r.label;
  $("res-pb").hidden = !r.pb;
  const badge = $("res-rank"); badge.textContent = ""; badge.hidden = rank < 0;
  if (rank >= 0) {
    const steps = rankSteps(S.mode, S.diff);
    badge.style.setProperty("--rc", RANK_COLORS[rank]);
    badge.append(el("div", { class: "rank-shield" }, [el("span", { text: "▲".repeat(Math.min(3, Math.floor(rank / 2) + 1)) })]),
      el("div", {}, [el("div", { class: "rank-name", text: RANKS[rank] }),
        el("div", { class: "muted", style: "font-size:14px", text: rank < 7 ? "Next: " + RANKS[rank + 1] + " at " + fmtRes(S.mode, steps[rank]) : "Top rank!" })]));
    badge.classList.remove("pop"); void badge.offsetWidth; badge.classList.add("pop");
  }
  const lines = $("res-lines"); lines.textContent = "";
  r.lines.filter(Boolean).forEach(l => lines.append(el("div", { text: l })));
  if ((S.mode === FLK || S.mode === BOT || S.mode === HTK) && S.bestCombo >= 3) lines.append(el("div", { text: "Best combo: ×" + S.bestCombo }));
  fresh.forEach(s => lines.append(el("div", { class: "unlock", style: "--g:" + s.glow }, ["New skin unlocked: ", el("b", { text: s.name }), " · equip it in Loadout"])));
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
// the score rolls up to its final value
function countUp(node, r) {
  if (r.score == null || S.mode === FND) { node.textContent = r.main; return; }
  const start = performance.now(), dur = 900, fmt = v => S.mode === TRK ? v.toFixed(1) + "%" : S.mode === RCT ? Math.round(v) + " ms" : String(Math.round(v));
  node.classList.remove("land");
  const tick = () => {
    const k = Math.min(1, (performance.now() - start) / dur), e = 1 - Math.pow(1 - k, 3);
    node.textContent = k < 1 ? fmt(r.score * e) : r.main;
    if (k < 1) requestAnimationFrame(tick); else node.classList.add("land");
  };
  tick();
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
      : S.mode === HTK ? S.htKills + " kills · " + (S.tot ? Math.round(S.on / S.tot * 100) : 0) + "% on target"
      : S.mode === RCT ? "Try " + Math.min(5, S.rTimes.length + 1) + " of 5"
      : S.mode === FND ? "Round " + Math.min(5, S.fIdx + 1) + " of 5"
      : (S.tot ? Math.round(S.on / S.tot * 100) : 0) + "% on target";
    return { details: run, status: st, game: gameId };
  }
  if (vis("results") && lastResult && S) {
    const m = lastResult.main;
    const st = S.mode === TRK ? m + " on target" : S.mode === FLK ? m + " hits" : (S.mode === BOT || S.mode === HTK) ? m + " kills" : S.mode === RCT ? m + " average" : "Best sens " + m;
    return { details: run + " · results", status: st, game: gameId };
  }
  if (vis("game") && g) return { details: g.name + " · choosing a mode", status: "Sens " + fmtSens(gameId, sensOf(gameId)), game: gameId };
  if (vis("setup")) return { details: "Setting up", status: "", game: "" };
  if (vis("loadout")) return { details: "Picking a gun skin", status: currentSkin().name, game: "" };
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

// ---------- loadout ----------
function renderLoadout() {
  const gp = $("gun-pick"); gp.textContent = "";
  [["warden", "Warden · rifle"], ["kestrel", "Kestrel · suppressed"]].forEach(([id, label]) =>
    gp.append(el("button", { class: "chip", "aria-pressed": String(loadout.gun === id), onclick: () => { loadout.gun = id; saveLoadout(); renderLoadout(); } }, [el("span", { text: label })])));
  const op = $("fx-options"); op.textContent = "";
  [["showGun", "Show gun"], ["sound", "Sounds"], ["shake", "Screen shake"]].forEach(([k, label]) =>
    op.append(el("button", { class: "chip", "aria-pressed": String(!!loadout[k]), onclick: () => { loadout[k] = !loadout[k]; saveLoadout(); if (k === "sound" && loadout.sound) sound(2); renderLoadout(); } }, [el("span", { text: label + ": " + (loadout[k] ? "On" : "Off") })])));
  const br = $("best-rank"); br.textContent = progressStore.bestRank >= 0 ? "Best rank so far: " + RANKS[progressStore.bestRank] : "";
  br.style.color = progressStore.bestRank >= 0 ? RANK_COLORS[progressStore.bestRank] : "";
  const grid = $("skin-grid"); grid.textContent = "";
  for (const s of SKINS) {
    const open = unlocked(s), sel = currentSkin().id === s.id;
    grid.append(el("button", { class: "skin", "aria-pressed": String(sel), disabled: open ? null : "", style: "--b:" + s.body + ";--g:" + s.glow, onclick: () => { loadout.skin = s.id; saveLoadout(); renderLoadout(); } },
      [el("i"), el("div", {}, [el("b", { text: s.name }), el("span", { text: open ? (sel ? "Equipped" : "Click to equip") : "Locked · " + s.need })])]));
  }
  $("gun-label").textContent = currentSkin().name + " · " + (loadout.gun === "kestrel" ? "Kestrel" : "Warden");
  $("gun-label").style.color = currentSkin().glow;
  previewLoop();
}
function previewLoop() {
  const c = $("gun-canvas");
  if ($("v-loadout").hidden) return;
  const dpr = Math.min(2, devicePixelRatio || 1), w = c.clientWidth, h = c.clientHeight;
  if (c.width !== w * dpr) { c.width = w * dpr; c.height = h * dpr; }
  const g2 = c.getContext("2d"); g2.setTransform(dpr, 0, 0, dpr, 0, 0); g2.clearRect(0, 0, w, h);
  const t = performance.now() / 1000, k = Math.min(w / 900, h / 340);
  drawGunModel(g2, w / 2 + 158 * k, h * .42, k,Math.sin(t * .8) * 2, t, currentSkin(), loadout.gun, t % 2.2 < .06, false);
  requestAnimationFrame(previewLoop);
}
$("open-loadout").onclick = () => show("loadout");
$("loadout-back").onclick = e => { e.preventDefault(); show("game"); };

// ---------- start ----------
(async () => {
  await initAuth();
  AT.listeners.push(renderSubmit);
  if (!profile) { renderSetup(); show("setup"); }
  else if (gameId && GAMES[gameId] && location.hash !== "#home") show("game");
  else show("home");
})();
})();
