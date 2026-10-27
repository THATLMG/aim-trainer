# Discord server and bot setup

About 30 minutes, once. Nothing here needs a computer left running.
Never paste the bot token or the notify secret into a chat (including Claude). They only go in the places listed below.

## 1. Make the server

1. In Discord: **+** (Add a Server) → **Create My Own** → **For a club or community**. Name it "LMG's Aim Trainer" and use `docs/rpc/logo.png` as the icon.
2. **Server Settings → Enable Community**. Follow the steps: it sets `#rules` and an updates channel for you.
3. **Server Settings → Safety Setup**:
   - Verification Level: **Medium** (account must be 5+ minutes old)
   - AutoMod: turn on **Block spam** and **Block mention spam**
   - Explicit image filter: **All members**

## 2. Channels

Create these categories and channels. "Read-only" means: channel settings → Permissions → @everyone → Send Messages ✖.

| Category | Channel | Notes |
|---|---|---|
| 📢 INFO | `#welcome` | Read-only. Paste the welcome text from `channel-text.md` |
| | `#rules` | Read-only. Paste the rules |
| | `#announcements` | Read-only. Announcement channel type |
| | `#updates` | Read-only. The bot posts every release here |
| | `#download` | Read-only. Paste the download text |
| 💬 COMMUNITY | `#general` | |
| | `#clips` | Show off runs |
| | `#leaderboard` | Read-only for chatting is optional. The bot posts verified runs and new #1s |
| | `#sens-help` | Pin the sens-help text |
| | `#bot-commands` | Where people use `/leaderboard`, `/sens` and so on |
| 🛠️ FEEDBACK | `#bug-reports` | **Forum** channel. Tags: Bug, Fixed, Can't reproduce |
| | `#suggestions` | **Forum** channel. Tags: Idea, Planned, Added |
| | `#mac-help` | |
| 🔒 STAFF | `#staff-chat` | Private: only Owner, Mod, Reviewer |
| | `#run-reviews` | Private: only Owner, Mod, Reviewer. The bot posts runs to approve here |

## 3. Roles

Create these (Server Settings → Roles), top to bottom:

- **Owner**: you, Administrator
- **Mod**: Manage Messages, Timeout Members, Kick Members
- **Reviewer**: no special permissions. The bot checks for this role before Approve/Reject works
- **Tester**: gets early builds
- **LMG's Aim Trainer** (the bot's role, added automatically): drag it **above** the rank roles
- Rank roles, colors to match the game: **Champion** `#ff4c5c`, **Elite** `#c860ff`, **Diamond** `#6eaaff`, **Platinum** `#38d2c4`, **Gold** `#f4c43a`, **Silver** `#c4cedc`, **Bronze** `#c48248`, **Iron** `#82848c`
- **Update pings**, **Event pings**: no permissions, just for mentions

Then **Server Settings → Onboarding** (on Community servers):
- Question "What game do you play?" (Valorant, CS2, Fortnite, Apex, Overwatch, Minecraft, Roblox), just for fun or to show channels
- Question "Want a ping when the game updates?" → Yes gives **Update pings**
- Question "What rank are you?" → Iron … Champion gives that rank role (the bot will hand these out automatically later)

## 4. Turn on the bot

Use the Discord app you already have for the Discord status (ID `1564084223093121065`).

1. Go to https://discord.com/developers/applications → your app.
2. **Bot** tab → **Reset Token** → copy it. Keep it private. Turn **off** "Public Bot" so only you can add it.
3. **General Information** → copy the **Public Key** (this one isn't secret).
4. **Installation** tab → Guild Install scopes: `bot`, `applications.commands`. Permissions: Send Messages, Embed Links, Read Message History, View Channels, Mention Everyone (only needed for the Update pings role).
   Copy the install link, open it, and add the bot to your server.
5. In Discord: **User Settings → Advanced → Developer Mode** on. Now right-click any channel or role → **Copy ID**.

## 5. Put the bot on Supabase

1. Supabase dashboard → **Edge Functions → Deploy a new function → Via Editor**.
   Name it exactly `discord-bot`, paste everything from `supabase/functions/discord-bot/index.ts`, and **Deploy**.
2. Open the function's **Details/Settings** and turn **off** "Verify JWT" (Discord can't send a Supabase login, it signs its requests instead and the bot checks that).
3. **Edge Functions → Secrets**, add:

| Name | Value |
|---|---|
| `DISCORD_BOT_TOKEN` | the token from step 4.2 |
| `DISCORD_PUBLIC_KEY` | the public key from step 4.3 |
| `NOTIFY_SECRET` | make up a long random password (e.g. from a password generator) |
| `DISCORD_UPDATES_CHANNEL` | ID of `#updates` |
| `DISCORD_LEADERBOARD_CHANNEL` | ID of `#leaderboard` |
| `DISCORD_REVIEW_CHANNEL` | ID of `#run-reviews` |
| `DISCORD_REVIEWER_ROLE` | ID of the **Reviewer** role |
| `DISCORD_UPDATE_ROLE` | ID of the **Update pings** role (optional) |
| `DISCORD_ANNOUNCE_CHANNEL` | ID of `#announcements` (your notices from the app get posted here) |

4. **SQL Editor**: run `supabase/003_health_track.sql` if you haven't yet, then `supabase/004_discord_bot.sql` and `supabase/005_notices.sql`.
5. Back in the Discord developer portal → **General Information → Interactions Endpoint URL**:
   `https://lzigsxqthaqzlanvygmm.supabase.co/functions/v1/discord-bot` → **Save**. Discord tests it right away; if it saves, the bot is talking.

## 6. Connect GitHub

1. GitHub repo → **Settings → Secrets and variables → Actions → New repository secret**:
   `DISCORD_NOTIFY_SECRET` = the same value as `NOTIFY_SECRET`.
2. **Actions → Discord bot → Run workflow → register**. This installs the slash commands (`/leaderboard`, `/profile`, `/sens`, `/code`, `/download`).

From now on every GitHub release posts to `#updates` by itself.

## 7. Turn on the Discord buttons

Make a permanent invite: right-click `#welcome` → **Invite People → Edit invite link** → Expire after **Never**, Max uses **No limit**.
Tell Claude the link (invite links are fine to share). It goes into `news.json`, and then the "Join Discord" buttons show up on the
website, in the Windows app, and on everyone's Discord status. No app update needed.

## What the bot does

- `/leaderboard game mode [difficulty] [verified]`: top 10, live from the website
- `/profile name`: a player's best run on every board, with a link to their page
- `/sens from sens to [dpi]`: converts sens between games, plus cm/360
- `/code code`: checks if a score code is real and shows what's in it
- `/download`: Windows, Mac and browser links
- New "verify my run" submissions appear in `#run-reviews` with **Approve / Reject** buttons (Reviewer role only).
  Reviewing on the website instead also closes the buttons.
- Verified runs and new #1s are posted to `#leaderboard`.
- Every release is posted to `#updates` with its notes and download buttons.
- Notices you send from the app ("Send notice" on the home screen, only on your PC) are posted to `#announcements`.
