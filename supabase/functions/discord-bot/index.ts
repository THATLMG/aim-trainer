// LMG's Aim Trainer Discord bot, running as a Supabase Edge Function (no server to keep online).
//
// One URL handles three kinds of requests:
//   1. Discord interactions (slash commands and buttons), signed by Discord.
//   2. ?event=score  from the database trigger in supabase/004_discord_bot.sql when a run changes status.
//      Only the run id is sent; the bot re-reads the run itself, so a fake call can't post anything untrue.
//   3. ?event=release or ?event=register  from GitHub Actions or you, with the NOTIFY_SECRET header.
//
// Secrets (Supabase: Edge Functions > Secrets). SUPABASE_URL and SUPABASE_SERVICE_ROLE_KEY are provided automatically.
//   DISCORD_BOT_TOKEN, DISCORD_PUBLIC_KEY, NOTIFY_SECRET,
//   DISCORD_UPDATES_CHANNEL, DISCORD_LEADERBOARD_CHANNEL, DISCORD_REVIEW_CHANNEL,
//   DISCORD_REVIEWER_ROLE, DISCORD_UPDATE_ROLE (optional ping on releases)

const env = (k: string) => (globalThis as any).Deno?.env.get(k) ?? (globalThis as any).process?.env[k] ?? "";
const APP_ID = env("DISCORD_APP_ID") || "1564084223093121065";
const SITE = "https://thatlmg.github.io/aim-trainer/";
const REPO = "THATLMG/aim-trainer";
const API = "https://discord.com/api/v10";
const COLOR = 0x3cc4b0, GOLD = 0xf5c542, GREEN = 0x3ddc84, RED = 0xff5050;

// ---------- game data (same as docs/common.js) ----------
export const GAMES: Record<string, any> = {
  valorant: { name: "Valorant", yaw: 0.07, dec: 3 },
  cs2: { name: "CS2", yaw: 0.022, dec: 2 },
  fortnite: { name: "Fortnite", yaw: 0.005555, suffix: "%", dec: 1 },
  apex: { name: "Apex Legends", yaw: 0.022, dec: 2 },
  overwatch: { name: "Overwatch 2", yaw: 0.0066, dec: 2 },
  minecraft: { name: "Minecraft", mc: true, suffix: "%", dec: 0 },
  roblox: { name: "Roblox", yaw: 0.025, dec: 2 },
  custom: { name: "Custom", yaw: 0.022, dec: 2 },
};
export const MODES: Record<string, string> = { T: "Tracking", H: "Health Track", F: "Flick", B: "Bots", R: "Reaction" };
const DIFFS = ["Easy", "Medium", "Hard", "Insane"];
const CODE_SECRET = "aimtrainer-board-v1-7Qp2xK";

const lowerBetter = (m: string) => m === "R";
export function fmtScore(m: string, v: number) {
  v = +v;
  return m === "T" ? v.toFixed(1) + "%" : m === "F" ? Math.round(v) + " hits" : m === "B" || m === "H" ? Math.round(v) + " kills" : Math.round(v) + " ms";
}
const fmtSens = (g: string, s: number) => (+s).toFixed(GAMES[g].dec) + (GAMES[g].suffix || "");
const boardName = (g: string, m: string, d: number) => GAMES[g].name + " · " + MODES[m] + (m === "R" ? "" : " · " + DIFFS[d]);
function degPerCount(g: string, s: number) {
  if (GAMES[g].mc) { const f = s / 200 * 0.6 + 0.2; return f * f * f * 1.2; }
  return s * GAMES[g].yaw;
}
function sensFor(g: string, dpc: number) {
  if (GAMES[g].mc) { const f = Math.cbrt(dpc / 1.2); return Math.max(1, (f - 0.2) / 0.6 * 200); }
  return dpc / GAMES[g].yaw;
}
const cm360 = (g: string, s: number, dpi: number) => 360 / (degPerCount(g, s) * dpi) * 2.54;

function fnv(t: string) {
  let h = 0x811c9dc5;
  for (const b of new TextEncoder().encode(t)) { h ^= b; h = Math.imul(h, 16777619) >>> 0; }
  return h.toString(16).padStart(8, "0");
}
export function decodeCode(raw: string): any {
  const code = (raw || "").trim().replace(/\s+/g, "");
  const m = /^AIM1\.([A-Za-z0-9_-]+)\.([0-9a-f]{8})$/.exec(code);
  if (!m) return { error: "That doesn't look like a score code. It should start with AIM1." };
  let payload: string;
  try {
    let b = m[1].replace(/-/g, "+").replace(/_/g, "/"); while (b.length % 4) b += "=";
    payload = new TextDecoder("utf-8", { fatal: true }).decode(Uint8Array.from(atob(b), c => c.charCodeAt(0)));
  } catch { return { error: "That code is damaged. Copy it again from the app." }; }
  if (fnv(CODE_SECRET + payload) !== m[2]) return { error: "This code doesn't check out. It was changed or copied wrong." };
  const p = payload.split("|");
  if (p[0] !== "v1" || p.length !== 9 || !GAMES[p[2]] || !MODES[p[3]]) return { error: "This code is from a different version of the app." };
  return { name: p[1], game: p[2], mode: p[3], diff: +p[4], score: +p[5], sens: +p[6], dpi: +p[7], t: +p[8] };
}

// ---------- database (PostgREST with the service key, which never leaves Supabase) ----------
async function db(path: string, init: RequestInit = {}) {
  const key = env("SUPABASE_SERVICE_ROLE_KEY");
  const r = await fetch(env("SUPABASE_URL") + "/rest/v1/" + path, {
    ...init,
    headers: { apikey: key, Authorization: "Bearer " + key, "Content-Type": "application/json", Prefer: "return=representation", ...(init.headers || {}) },
  });
  if (!r.ok) throw new Error("Database error " + r.status + ": " + (await r.text()).slice(0, 200));
  const t = await r.text();
  return t ? JSON.parse(t) : null;
}
const clipLink = (s: any) => s.clip_url || (s.clip_path ? env("SUPABASE_URL") + "/storage/v1/object/public/clips/" + s.clip_path : "");
const playerLink = (uid: string) => SITE + "board.html#player-" + uid;

// Best run per player on one board, same rules as the website.
async function board(game: string, mode: string, diff: number, onlyVerified = false) {
  let q = "scores?select=*&game=eq." + game + "&mode=eq." + mode + "&status=neq.rejected&limit=3000";
  if (mode !== "R") q += "&diff=eq." + diff;
  if (onlyVerified) q += "&status=eq.verified";
  const rows: any[] = await db(q);
  const ids = [...new Set(rows.map(r => r.user_id))];
  const people: any[] = ids.length ? await db("profiles?select=id,username,banned&id=in.(" + ids.join(",") + ")") : [];
  const byId = Object.fromEntries(people.map(p => [p.id, p]));
  const lb = lowerBetter(mode), best: Record<string, any> = {};
  for (const s of rows) {
    if (byId[s.user_id]?.banned) continue;
    const c = best[s.user_id];
    if (!c || (lb ? +s.score < +c.score : +s.score > +c.score)) best[s.user_id] = s;
  }
  return { rows: Object.values(best).sort((a, b) => lb ? a.score - b.score : b.score - a.score), people: byId };
}

// ---------- Discord helpers ----------
async function discord(path: string, method: string, body?: unknown) {
  const r = await fetch(API + path, {
    method,
    headers: { Authorization: "Bot " + env("DISCORD_BOT_TOKEN"), "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) throw new Error("Discord error " + r.status + ": " + (await r.text()).slice(0, 300));
  return r.status === 204 ? null : r.json();
}
const json = (o: unknown, status = 200) => new Response(JSON.stringify(o), { status, headers: { "Content-Type": "application/json" } });
const reply = (data: any) => json({ type: 4, data: { allowed_mentions: { parse: [] }, ...data } });
const linkBtn = (label: string, url: string) => ({ type: 2, style: 5, label, url });

function hex(s: string) { return Uint8Array.from(s.match(/../g) || [], h => parseInt(h, 16)); }
async function verifyDiscord(req: Request, body: string) {
  const sig = req.headers.get("x-signature-ed25519"), ts = req.headers.get("x-signature-timestamp");
  if (!sig || !ts || !env("DISCORD_PUBLIC_KEY")) return false;
  try {
    const key = await crypto.subtle.importKey("raw", hex(env("DISCORD_PUBLIC_KEY")), { name: "Ed25519" }, false, ["verify"]);
    return await crypto.subtle.verify("Ed25519", key, hex(sig), new TextEncoder().encode(ts + body));
  } catch { return false; }
}

// ---------- slash commands ----------
const gameChoices = Object.entries(GAMES).map(([value, g]) => ({ name: g.name, value }));
export const COMMANDS = [
  { name: "leaderboard", description: "Top 10 on a leaderboard", options: [
    { type: 3, name: "game", description: "Game", required: true, choices: gameChoices },
    { type: 3, name: "mode", description: "Mode", required: true, choices: Object.entries(MODES).map(([value, name]) => ({ name, value })) },
    { type: 4, name: "difficulty", description: "Difficulty (not used for Reaction)", choices: DIFFS.map((name, value) => ({ name, value })) },
    { type: 5, name: "verified", description: "Only show verified runs" }] },
  { name: "profile", description: "A player's best runs", options: [
    { type: 3, name: "name", description: "Their leaderboard name or in-game name", required: true }] },
  { name: "sens", description: "Convert sensitivity between games", options: [
    { type: 3, name: "from", description: "Game you play now", required: true, choices: gameChoices },
    { type: 10, name: "sens", description: "Your sensitivity in that game", required: true, min_value: 0.0001 },
    { type: 3, name: "to", description: "Game to convert to", required: true, choices: gameChoices },
    { type: 4, name: "dpi", description: "Mouse DPI (default 800)", min_value: 100, max_value: 50000 }] },
  { name: "code", description: "Check a score code from the app", options: [
    { type: 3, name: "code", description: "The AIM1… code", required: true }] },
  { name: "download", description: "Get LMG's Aim Trainer" },
];

const opt = (i: any, name: string) => (i.data.options || []).find((o: any) => o.name === name)?.value;

async function cmdLeaderboard(i: any) {
  const game = opt(i, "game"), mode = opt(i, "mode"), diff = mode === "R" ? 0 : opt(i, "difficulty") ?? 1, ver = !!opt(i, "verified");
  const { rows, people } = await board(game, mode, diff, ver);
  const title = boardName(game, mode, diff);
  if (!rows.length) return reply({ content: "Nobody is on **" + title + "** yet. Play it and submit your code at " + SITE + "board.html" });
  const medal = ["🥇", "🥈", "🥉"];
  const lines = rows.slice(0, 10).map((s, n) =>
    (medal[n] || "`#" + (n + 1) + "`") + " **" + fmtScore(mode, s.score) + "**  " + (people[s.user_id]?.username || s.ign) +
    (s.status === "verified" ? "  ✅" : "") + "  ·  " + fmtSens(game, s.sens) + " @ " + s.dpi + " DPI");
  return reply({ embeds: [{ title: "🏆 " + title, url: SITE + "board.html", color: COLOR, description: lines.join("\n"),
    footer: { text: rows.length + (rows.length === 1 ? " player" : " players") + " · best run each" + (lowerBetter(mode) ? " · lower is better" : "") + " · ✅ = verified with a clip" } }] });
}

async function cmdProfile(i: any) {
  const name = String(opt(i, "name")).trim().replace(/[%,()*]/g, "").slice(0, 32);
  if (!name) return reply({ content: "Type a name to look up.", flags: 64 });
  let people: any[] = await db("profiles?select=id,username,banned&username=ilike." + encodeURIComponent(name) + "&limit=1");
  if (!people.length) {
    const byIgn: any[] = await db("scores?select=user_id&ign=ilike." + encodeURIComponent(name) + "&limit=1");
    if (byIgn.length) people = await db("profiles?select=id,username,banned&id=eq." + byIgn[0].user_id);
  }
  if (!people.length || people[0].banned) return reply({ content: "I couldn't find a player called **" + name + "** on the leaderboard.", flags: 64 });
  const p = people[0];
  const runs: any[] = await db("scores?select=*&user_id=eq." + p.id + "&status=neq.rejected&order=created_at.desc&limit=500");
  const best: Record<string, any> = {};
  for (const s of runs) {
    const k = s.game + s.mode + (s.mode === "R" ? 0 : s.diff), c = best[k];
    if (!c || (lowerBetter(s.mode) ? +s.score < +c.score : +s.score > +c.score)) best[k] = s;
  }
  const list = Object.values(best).slice(0, 12).map(s =>
    "**" + fmtScore(s.mode, s.score) + "**  " + boardName(s.game, s.mode, s.diff) + (s.status === "verified" ? "  ✅" : ""));
  const verified = runs.filter(s => s.status === "verified").length;
  return reply({ embeds: [{ title: p.username, url: playerLink(p.id), color: COLOR,
    description: list.length ? list.join("\n") : "No runs yet.",
    footer: { text: runs.length + (runs.length === 1 ? " run · " : " runs · ") + verified + " verified · best run on each board" } }] });
}

function cmdSens(i: any) {
  const from = opt(i, "from"), to = opt(i, "to"), sens = +opt(i, "sens"), dpi = +(opt(i, "dpi") ?? 800);
  const out = sensFor(to, degPerCount(from, sens)), cm = cm360(from, sens, dpi);
  return reply({ embeds: [{ title: "🎯 Sens converter", color: COLOR,
    description: "**" + fmtSens(from, sens) + "** in " + GAMES[from].name + "  →  **" + fmtSens(to, out) + "** in " + GAMES[to].name +
      "\n\nThat's **" + cm.toFixed(1) + " cm** for a full 360° at " + dpi + " DPI." +
      (GAMES[from].mc || GAMES[to].mc ? "\n_Minecraft numbers are approximate._" : "") }] });
}

function cmdCode(i: any) {
  const c = decodeCode(String(opt(i, "code")));
  if (c.error) return reply({ content: "❌ " + c.error });
  return reply({ embeds: [{ title: "✅ Real score code", color: GREEN, fields: [
    { name: "Player", value: c.name || "?", inline: true },
    { name: "Board", value: boardName(c.game, c.mode, c.diff), inline: true },
    { name: "Score", value: fmtScore(c.mode, c.score), inline: true },
    { name: "Sens", value: fmtSens(c.game, c.sens) + " @ " + c.dpi + " DPI", inline: true },
    { name: "Played", value: "<t:" + c.t + ":R>", inline: true }],
    footer: { text: "Submit it at thatlmg.github.io/aim-trainer/board.html" } }] });
}

function cmdDownload() {
  return reply({ embeds: [{ title: "Get LMG's Aim Trainer", url: SITE, color: COLOR,
    description: "Free aim trainer with your real game sens: Tracking, Health Track, Flick, Bots, Reaction and a sens finder." }],
    components: [{ type: 1, components: [
      linkBtn("Windows", "https://github.com/" + REPO + "/releases/latest/download/AimTrainer.zip"),
      linkBtn("Mac", "https://github.com/" + REPO + "/releases/latest/download/LMGs-Aim-Trainer-mac.dmg"),
      linkBtn("Play in browser", SITE + "play.html"),
      linkBtn("Leaderboard", SITE + "board.html")] }] });
}

// ---------- run reviews ----------
function runEmbed(s: any, who: string, extra: any = {}) {
  const clip = clipLink(s);
  return { color: COLOR, title: boardName(s.game, s.mode, s.diff), url: playerLink(s.user_id),
    description: "**" + who + "**: " + fmtScore(s.mode, s.score) + "  ·  " + fmtSens(s.game, s.sens) + " @ " + s.dpi + " DPI" +
      (clip ? "\n🎬 [Watch the clip](" + clip + ")" : ""),
    timestamp: s.played_at, ...extra };
}
const reviewButtons = (id: string) => [{ type: 1, components: [
  { type: 2, style: 3, label: "Approve", emoji: { name: "✅" }, custom_id: "review:" + id + ":verified" },
  { type: 2, style: 4, label: "Reject", emoji: { name: "❌" }, custom_id: "review:" + id + ":rejected" }] }];

async function onReviewButton(i: any) {
  const [, id, status] = String(i.data.custom_id).split(":");
  const roles: string[] = i.member?.roles || [];
  const isAdmin = (BigInt(i.member?.permissions || "0") & 8n) === 8n;
  if (!isAdmin && !(env("DISCORD_REVIEWER_ROLE") && roles.includes(env("DISCORD_REVIEWER_ROLE"))))
    return reply({ content: "Only Reviewers can approve or reject runs.", flags: 64 });
  if (!/^[0-9a-f-]{36}$/.test(id) || !["verified", "rejected"].includes(status)) return reply({ content: "Unknown button.", flags: 64 });
  const found: any[] = await db("scores?select=*&id=eq." + id);
  if (!found.length) return json({ type: 7, data: { content: "This run was deleted.", embeds: [], components: [] } });
  if (!clipLink(found[0])) return reply({ content: "Only runs with a clip can be reviewed.", flags: 64 });
  // Mark the review post as handled first, so the database trigger doesn't overwrite this message.
  await db("discord_posts?on_conflict=score_id,kind", { method: "POST", headers: { Prefer: "resolution=ignore-duplicates" }, body: JSON.stringify({ score_id: id, kind: "review-closed" }) });
  const [s] = await db("scores?id=eq." + id, { method: "PATCH", body: JSON.stringify({ status, reviewed_at: new Date().toISOString() }) });
  return json({ type: 7, data: { embeds: [reviewedEmbed(s, await nameOf(s), status, "<@" + (i.member?.user?.id || "") + ">")], components: [] } });
}
function reviewedEmbed(s: any, who: string, status: string, by: string) {
  const ok = status === "verified";
  return runEmbed(s, who, { color: ok ? GREEN : RED, footer: undefined,
    fields: [{ name: ok ? "✅ Approved" : "❌ Rejected", value: "by " + by }] });
}
async function nameOf(s: any) {
  const p: any[] = await db("profiles?select=username&id=eq." + s.user_id);
  return p[0]?.username || s.ign;
}

// Called by the database trigger whenever a run's status changes.
async function onScoreEvent(id: string) {
  if (!/^[0-9a-f-]{36}$/.test(id)) return json({ ok: false }, 400);
  const found: any[] = await db("scores?select=*&id=eq." + id);
  if (!found.length) return json({ ok: true });
  const s = found[0], who = await nameOf(s);
  const posted: any[] = await db("discord_posts?select=*&score_id=eq." + id);
  const has = (k: string) => posted.find(p => p.kind === k);

  // Each post is recorded before it's sent, so two calls at once can't post twice (the second insert fails).
  if (s.status === "pending" && !has("review") && env("DISCORD_REVIEW_CHANNEL")) {
    const ch = env("DISCORD_REVIEW_CHANNEL");
    await db("discord_posts", { method: "POST", body: JSON.stringify({ score_id: id, kind: "review", channel_id: ch }) });
    const msg: any = await discord("/channels/" + ch + "/messages", "POST",
      { content: "🎬 New run to review", embeds: [runEmbed(s, who)], components: reviewButtons(id), allowed_mentions: { parse: [] } });
    await db("discord_posts?score_id=eq." + id + "&kind=eq.review", { method: "PATCH", body: JSON.stringify({ message_id: msg.id }) });
  }
  // Reviewed on the website instead: close the buttons on the review post.
  const rv = has("review");
  if ((s.status === "verified" || s.status === "rejected") && rv && rv.message_id && !has("review-closed")) {
    await db("discord_posts", { method: "POST", body: JSON.stringify({ score_id: id, kind: "review-closed" }) });
    await discord("/channels/" + rv.channel_id + "/messages/" + rv.message_id, "PATCH",
      { embeds: [reviewedEmbed(s, who, s.status, "the website")], components: [] }).catch(() => {});
  }
  if (s.status === "verified" && !has("verified") && env("DISCORD_LEADERBOARD_CHANNEL")) {
    await db("discord_posts", { method: "POST", body: JSON.stringify({ score_id: id, kind: "verified" }) });
    const { rows } = await board(s.game, s.mode, s.diff);
    const place = rows.findIndex(r => r.id === s.id) + 1;
    const top = place === 1;
    await discord("/channels/" + env("DISCORD_LEADERBOARD_CHANNEL") + "/messages", "POST", {
      content: top ? "👑 **New #1!**" : "✅ Run verified",
      embeds: [runEmbed(s, who, { color: top ? GOLD : GREEN, footer: { text: place ? "#" + place + " on this board" : "Verified" } })],
      allowed_mentions: { parse: [] } });
  }
  return json({ ok: true });
}

// Called by GitHub Actions when a release is published. The release notes become the post.
async function onRelease(tag: string) {
  const r = await fetch("https://api.github.com/repos/" + REPO + "/releases/tags/" + encodeURIComponent(tag), { headers: { "User-Agent": "lmg-aim-bot" } });
  if (!r.ok) return json({ ok: false, error: "release not found" }, 404);
  const rel: any = await r.json();
  const notes = String(rel.body || "").trim();
  const ping = env("DISCORD_UPDATE_ROLE");
  await discord("/channels/" + env("DISCORD_UPDATES_CHANNEL") + "/messages", "POST", {
    content: (ping ? "<@&" + ping + "> " : "") + "**" + (rel.name || tag) + "** is out! Your app updates itself next time you open it.",
    embeds: [{ title: "What's new in " + (rel.name || tag), url: rel.html_url, color: COLOR,
      description: notes.length > 3900 ? notes.slice(0, 3900) + "…" : notes || "Bug fixes and improvements.",
      timestamp: rel.published_at || undefined }],
    components: [{ type: 1, components: [
      linkBtn("Windows", "https://github.com/" + REPO + "/releases/download/" + tag + "/AimTrainer.zip"),
      linkBtn("Mac", "https://github.com/" + REPO + "/releases/latest/download/LMGs-Aim-Trainer-mac.dmg"),
      linkBtn("Play in browser", SITE + "play.html")] }],
    allowed_mentions: ping ? { roles: [ping] } : { parse: [] } });
  return json({ ok: true });
}

// ---------- entry ----------
export async function handle(req: Request): Promise<Response> {
  const url = new URL(req.url), body = await req.text();
  if (req.headers.get("x-signature-ed25519")) {
    if (!(await verifyDiscord(req, body))) return new Response("Bad signature", { status: 401 });
    const i = JSON.parse(body);
    try {
      if (i.type === 1) return json({ type: 1 });
      if (i.type === 2) {
        const n = i.data.name;
        if (n === "leaderboard") return await cmdLeaderboard(i);
        if (n === "profile") return await cmdProfile(i);
        if (n === "sens") return cmdSens(i);
        if (n === "code") return cmdCode(i);
        if (n === "download") return cmdDownload();
      }
      if (i.type === 3 && String(i.data.custom_id).startsWith("review:")) return await onReviewButton(i);
      return reply({ content: "I don't know that one.", flags: 64 });
    } catch (e) {
      console.error(e);
      return reply({ content: "Something went wrong on my end. Try again in a minute.", flags: 64 });
    }
  }
  const event = url.searchParams.get("event");
  try {
    if (event === "score") return await onScoreEvent(String(JSON.parse(body || "{}").id || ""));
    const secret = env("NOTIFY_SECRET");
    if (!secret || req.headers.get("x-notify-secret") !== secret) return new Response("Forbidden", { status: 403 });
    if (event === "release") return await onRelease(String(JSON.parse(body || "{}").tag || ""));
    if (event === "register") return json(await discord("/applications/" + APP_ID + "/commands", "PUT", COMMANDS));
  } catch (e) {
    console.error(e);
    return json({ ok: false, error: String(e) }, 500);
  }
  return new Response("Not found", { status: 404 });
}

if ((globalThis as any).Deno) (globalThis as any).Deno.serve(handle);
