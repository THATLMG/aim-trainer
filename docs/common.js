// Shared by the landing page and the leaderboard.
const CFG = window.AT_CONFIG || {};
const REPO = CFG.repo || "THATLMG/aim-trainer";
const DOWNLOAD_URL = "https://github.com/" + REPO + "/releases/latest/download/AimTrainer.zip";

const GAMES = {
  valorant:  { name: "Valorant", color: "#ff5462", yaw: 0.07, dec: 3, tag: "Sharp flicks, tight strafes" },
  cs2:       { name: "CS2", color: "#f0a23b", yaw: 0.022, dec: 2, tag: "Counter-strafes at head height" },
  fortnite:  { name: "Fortnite", color: "#a666ff", yaw: 0.005555, suffix: "%", dec: 1, tag: "Build-fight tracking" },
  apex:      { name: "Apex Legends", color: "#de5444", yaw: 0.022, dec: 2, tag: "Long strafes and slides" },
  overwatch: { name: "Overwatch 2", color: "#f99e1a", yaw: 0.0066, dec: 2, tag: "Fast heroes, lots of vertical" },
  minecraft: { name: "Minecraft", color: "#6cb23c", mc: true, suffix: "%", dec: 0, tag: "PvP strafes and jump-hits" },
  roblox:    { name: "Roblox", color: "#28a0ff", yaw: 0.025, dec: 2, tag: "Bigger, bouncy targets" },
  custom:    { name: "Custom", color: "#3cc4b0", yaw: 0.022, dec: 2, tag: "Any game with CS2-style sens" }
};
const MODES = { T: "Tracking", H: "Health Track", F: "Flick", B: "Bots", R: "Reaction" };
const DIFFS = ["Easy", "Medium", "Hard", "Insane"];
const CODE_SECRET = "aimtrainer-board-v1-7Qp2xK";
const MAX_CLIP = 20 * 1024 * 1024;

const $ = id => document.getElementById(id);

// Inside the Mac app (Tauri), links to other websites open in the real browser instead of the app window.
const TAURI = window.__TAURI__ || null;
if (TAURI) document.addEventListener("click", e => {
  const a = e.target.closest && e.target.closest("a[href]");
  if (!a) return;
  const url = new URL(a.href, location.href);
  if (/^https?:$/.test(url.protocol) && url.host !== location.host) { e.preventDefault(); TAURI.opener.openUrl(url.href).catch(() => {}); }
});
function el(tag, props, kids) {
  const n = document.createElement(tag);
  if (props) for (const k in props) {
    if (props[k] == null) continue;
    if (k === "text") n.textContent = props[k];
    else if (k === "class") n.className = props[k];
    else if (k.startsWith("on")) n.addEventListener(k.slice(2), props[k]);
    else n.setAttribute(k, props[k]);
  }
  (kids || []).forEach(c => c != null && n.append(c));
  return n;
}

// ---------- score codes (same check as the app and the database) ----------
function fnv(str) {
  let h = 0x811c9dc5;
  for (const b of new TextEncoder().encode(str)) { h ^= b; h = Math.imul(h, 0x01000193) >>> 0; }
  return h.toString(16).padStart(8, "0");
}
function decodeCode(raw) {
  const code = (raw || "").trim().replace(/\s+/g, "");
  if (!code) return null;
  const m = /^AIM1\.([A-Za-z0-9_-]+)\.([0-9a-f]{8})$/.exec(code);
  if (!m) return { error: "That doesn't look like a score code. It should start with AIM1." };
  let payload;
  try { let b = m[1].replace(/-/g, "+").replace(/_/g, "/"); while (b.length % 4) b += "="; payload = new TextDecoder().decode(Uint8Array.from(atob(b), c => c.charCodeAt(0))); }
  catch { return { error: "That code is damaged. Copy it again from the app." }; }
  if (fnv(CODE_SECRET + payload) !== m[2]) return { error: "This code doesn't check out. It was changed or copied wrong." };
  const p = payload.split("|");
  if (p[0] !== "v1" || p.length !== 9 || !GAMES[p[2]] || !MODES[p[3]]) return { error: "This code is from a different version of the app." };
  return { code, name: p[1], game: p[2], mode: p[3], diff: +p[4], score: +p[5], sens: +p[6], dpi: +p[7], t: +p[8] };
}

const lowerBetter = mode => mode === "R";
function fmtScore(mode, v) {
  v = +v;
  return mode === "T" ? v.toFixed(1) + "%" : mode === "F" ? Math.round(v) + " hits" : (mode === "B" || mode === "H") ? Math.round(v) + " kills" : Math.round(v) + " ms";
}
function fmtSens(g, s) { const G = GAMES[g] || GAMES.custom; return (+s).toFixed(G.dec) + (G.suffix || ""); }
function cm360(g, sens, dpi) {
  const G = GAMES[g] || GAMES.custom;
  let dpc;
  if (G.mc) { const f = (sens / 200) * 0.6 + 0.2; dpc = f * f * f * 1.2; } else dpc = sens * G.yaw;
  return 360 / (dpc * dpi) * 2.54;
}
const fmtDate = d => new Date(d).toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" });
const boardName = (g, m, d) => (GAMES[g] || {}).name + " · " + MODES[m] + (m === "R" ? "" : " · " + DIFFS[d]);

// ---------- GitHub releases ----------
let releasesCache = null;
async function releases() {
  if (!releasesCache) releasesCache = fetch("https://api.github.com/repos/" + REPO + "/releases?per_page=10").then(r => r.ok ? r.json() : []).catch(() => []);
  return releasesCache;
}
// Very small Markdown subset for release notes: **bold**, `code`, - bullets, paragraphs.
function renderNotes(md) {
  const box = el("div");
  let list = null;
  for (const raw of (md || "").split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) { list = null; continue; }
    const target = /^[-*] /.test(line) ? (list || box.appendChild(list = el("ul"))).appendChild(el("li")) : (list = null, box.appendChild(el("p")));
    const text = line.replace(/^[-*] /, "");
    for (const part of text.split(/(\*\*[^*]+\*\*|`[^`]+`)/)) {
      if (/^\*\*.+\*\*$/.test(part)) target.appendChild(el("b", { text: part.slice(2, -2) }));
      else if (/^`.+`$/.test(part)) target.appendChild(el("code", { text: part.slice(1, -1) }));
      else if (part) target.append(part);
    }
  }
  return box;
}

// ---------- Supabase ----------
const AT = { sb: null, session: null, profile: null, listeners: [] };

// The Discord invite lives in news.json so it can change without an app update. When set, every page gets a Discord link.
AT.discordInvite = "";
fetch("https://raw.githubusercontent.com/" + REPO + "/main/news.json", { cache: "no-store" }).then(r => r.json()).then(n => {
  const inv = String(n.discord || "");
  if (!/^https:\/\/(discord\.gg|discord\.com\/invite)\//.test(inv)) return;
  AT.discordInvite = inv;
  document.querySelectorAll(".nav .links").forEach(l => {
    const a = document.createElement("a"); a.href = inv; a.target = "_blank"; a.rel = "noopener"; a.textContent = "Discord";
    l.insertBefore(a, l.lastElementChild);
  });
}).catch(() => {});
const configured = () => !!(CFG.supabaseUrl && CFG.supabaseKey && window.supabase);

async function initAuth() {
  if (!configured()) return;
  AT.sb = window.supabase.createClient(CFG.supabaseUrl, CFG.supabaseKey);
  const { data } = await AT.sb.auth.getSession();
  AT.session = data.session;
  await loadProfile();
  AT.sb.auth.onAuthStateChange(async (_event, session) => {
    const changed = (session && session.user.id) !== (AT.session && AT.session.user.id);
    AT.session = session;
    if (changed) { await loadProfile(); AT.listeners.forEach(f => f()); }
  });
}
async function loadProfile() {
  AT.profile = null;
  if (!AT.session) return;
  for (let i = 0; i < 4 && !AT.profile; i++) {   // the profile row is created right after sign-up
    const { data } = await AT.sb.from("profiles").select("*").eq("id", AT.session.user.id).maybeSingle();
    AT.profile = data;
    if (!data) await new Promise(r => setTimeout(r, 700));
  }
}
function loginDiscord() {
  const back = location.origin + location.pathname;
  AT.sb.auth.signInWithOAuth({ provider: "discord", options: { redirectTo: back } });
}

// Email + password sign-in, in a small dialog built on demand.
function login() {
  let dlg = $("auth-dialog");
  if (!dlg) {
    dlg = el("dialog", { id: "auth-dialog", class: "auth" });
    document.body.append(dlg);
  }
  let mode = "in";
  const draw = () => {
    dlg.textContent = "";
    const name = el("input", { type: "text", id: "auth-name", placeholder: "Username shown on the leaderboard", maxlength: "24", autocomplete: "username" });
    const email = el("input", { type: "email", id: "auth-email", placeholder: "you@example.com", autocomplete: "email", required: "" });
    const pass = el("input", { type: "password", id: "auth-pass", placeholder: mode === "up" ? "At least 6 characters" : "Password", minlength: "6", autocomplete: mode === "up" ? "new-password" : "current-password", required: "" });
    const msg = el("div");
    const form = el("form", { class: "stack", method: "dialog" }, [
      el("div", { class: "row", style: "justify-content:space-between;align-items:center" }, [
        el("h3", { text: mode === "up" ? "Create an account" : "Log in" }),
        el("button", { type: "button", class: "btn small", text: "Close", onclick: () => dlg.close() })]),
      mode === "up" ? el("label", { class: "stack", style: "gap:6px" }, [el("span", { class: "label", text: "Username" }), name]) : null,
      el("label", { class: "stack", style: "gap:6px" }, [el("span", { class: "label", text: "Email" }), email]),
      el("label", { class: "stack", style: "gap:6px" }, [el("span", { class: "label", text: "Password" }), pass]),
      msg,
      el("button", { type: "submit", class: "btn primary", text: mode === "up" ? "Create account" : "Log in" }),
      CFG.discordLogin ? el("button", { type: "button", class: "btn discord", text: "Log in with Discord", onclick: loginDiscord }) : null,
      el("p", { class: "muted", style: "margin:0;font-size:14px;text-align:center" }, [
        mode === "up" ? "Already have an account? " : "New here? ",
        el("a", { href: "#", text: mode === "up" ? "Log in" : "Create an account", onclick: e => { e.preventDefault(); mode = mode === "up" ? "in" : "up"; draw(); } })])
    ]);
    form.addEventListener("submit", async e => {
      e.preventDefault();
      const btn = form.querySelector('[type="submit"]');
      btn.disabled = true; msg.textContent = "";
      let res;
      if (mode === "up") {
        const n = name.value.trim();
        if (n.length < 2) { msg.append(el("div", { class: "msg err", text: "Pick a username with at least 2 characters." })); btn.disabled = false; return; }
        res = await AT.sb.auth.signUp({ email: email.value.trim(), password: pass.value, options: { data: { name: n } } });
        if (!res.error && !res.data.session) {
          msg.append(el("div", { class: "msg okm", text: "Account created. Check your email to confirm it, then log in." }));
          btn.disabled = false; return;
        }
      } else res = await AT.sb.auth.signInWithPassword({ email: email.value.trim(), password: pass.value });
      if (res.error) {
        const t = /invalid login/i.test(res.error.message) ? "Wrong email or password." : /already registered/i.test(res.error.message) ? "That email already has an account. Log in instead." : res.error.message;
        msg.append(el("div", { class: "msg err", text: t }));
        btn.disabled = false; return;
      }
      dlg.close();
    });
    dlg.append(form);
    setTimeout(() => (mode === "up" ? name : email).focus(), 0);
  };
  draw();
  dlg.showModal();
}
async function logout() { await AT.sb.auth.signOut(); location.reload(); }
const isStaff = () => !!AT.profile && !AT.profile.banned && (AT.profile.role === "owner" || AT.profile.role === "mod");
const isOwner = () => !!AT.profile && AT.profile.role === "owner";
const clipUrl = path => AT.sb.storage.from("clips").getPublicUrl(path).data.publicUrl;

function avatar(p, size) {
  const img = el("img", { alt: "", class: "avatar", loading: "lazy" });
  if (size) { img.style.width = img.style.height = size + "px"; }
  if (p && p.avatar_url) img.src = p.avatar_url;
  else img.src = "data:image/svg+xml," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40"><rect width="40" height="40" fill="#171e2a"/><circle cx="20" cy="15" r="7" fill="#19e6e8"/><rect x="10" y="24" width="20" height="14" rx="7" fill="#19e6e8"/></svg>');
  return img;
}
function rolePill(role) {
  if (role === "owner") return el("span", { class: "pill owner", text: "Owner" });
  if (role === "mod") return el("span", { class: "pill mod", text: "Mod" });
  return null;
}
function statusPill(s) {
  const map = { verified: ["ok", "✓ Verified"], rejected: ["no", "Rejected"], pending: ["wait", "In review"], unverified: ["none", "Unverified"] };
  const [cls, text] = map[s] || map.unverified;
  return el("span", { class: "pill " + cls, text });
}
function clipNode(row) {
  if (row.clip_path) { const v = el("video", { controls: "", preload: "metadata" }); v.src = clipUrl(row.clip_path); return v; }
  if (row.clip_url && /^https:\/\//i.test(row.clip_url)) {
    let host = ""; try { host = new URL(row.clip_url).hostname.replace(/^www\./, ""); } catch {}
    return el("a", { href: row.clip_url, target: "_blank", rel: "noopener noreferrer", class: "btn small", text: "Watch clip on " + (host || "link") + " ↗" });
  }
  return el("span", { class: "muted", text: "No clip attached." });
}
