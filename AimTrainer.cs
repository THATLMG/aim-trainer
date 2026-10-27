// LMG's Aim Trainer: tracking, flick and reaction practice with a sensitivity coach.
// Reads raw mouse movement so your in-game sensitivity can be matched.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Threading;
using System.Windows.Forms;

class GameInfo
{
    public string Id, Name, Tagline, Suffix = "", Note = "";
    public Color Accent, Bg;
    public double Yaw, HFov, DefaultSens;
    public int Decimals;
    public bool Minecraft, Jumps, Square;
    public double HorizBias, SizeMul = 1, Band = 0.8;

    // Degrees the camera turns per mouse count at a given in-game sensitivity.
    public double DegPerCount(double sens)
    {
        if (Minecraft) { double f = (sens / 200.0) * 0.6 + 0.2; return f * f * f * 1.2; }
        return sens * Yaw;
    }
    public double SensFor(double degPerCount)
    {
        if (Minecraft) { double f = Math.Pow(degPerCount / 1.2, 1.0 / 3.0); return Math.Max(1, (f - 0.2) / 0.6 * 200.0); }
        return degPerCount / Yaw;
    }
    public double Round(double sens) { return Math.Round(sens, Decimals); }
    public string Fmt(double sens) { return Round(sens).ToString("F" + Decimals, CultureInfo.InvariantCulture) + Suffix; }
}

class Rec { public string Game, Mode; public int Diff; public double Score; public DateTime When; }

class Btn { public RectangleF R; public Action Click; }

// What Windows says is playing right now (Spotify, a browser tab, etc.).
class NowPlaying
{
    public string Title = "", Artist = "", Album = "", App = "", Key = "";
    public double Pos, Dur;
    public DateTime PosAt;   // UTC moment Pos was measured
    public bool Playing, CanPrev, CanNext;
    public double Current
    {
        get
        {
            double c = Playing ? Pos + (DateTime.UtcNow - PosAt).TotalSeconds : Pos;
            return Dur > 0 ? Math.Max(0, Math.Min(Dur, c)) : Math.Max(0, c);
        }
    }
}

class LyricLine { public double T; public string Text; }

// Windows media controls. Kept in its own class so a PC without them only loses the music bar.
namespace MediaBridge
{
    using Windows.Foundation;
    using Windows.Media.Control;
    using Windows.Storage.Streams;

    static class Media
    {
        static GlobalSystemMediaTransportControlsSessionManager mgr;

        static T Wait<T>(IAsyncOperation<T> op)
        {
            var start = DateTime.UtcNow;
            while (op.Status == AsyncStatus.Started)
            {
                if ((DateTime.UtcNow - start).TotalSeconds > 3) throw new TimeoutException();
                System.Threading.Thread.Sleep(5);
            }
            if (op.Status != AsyncStatus.Completed) throw new InvalidOperationException("media call failed");
            return op.GetResults();
        }

        public static NowPlaying Poll(string lastKey, out byte[] art)
        {
            art = null;
            if (mgr == null) mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            var s = mgr.GetCurrentSession();
            if (s == null) return null;
            var p = Wait(s.TryGetMediaPropertiesAsync());
            var tl = s.GetTimelineProperties();
            var pi = s.GetPlaybackInfo();
            var n = new NowPlaying { Title = p.Title ?? "", Artist = p.Artist ?? "", Album = p.AlbumTitle ?? "", App = s.SourceAppUserModelId ?? "" };
            n.Key = n.Artist + "\n" + n.Title;
            n.Dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            n.Pos = (tl.Position - tl.StartTime).TotalSeconds;
            n.Playing = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            DateTime at = tl.LastUpdatedTime.UtcDateTime;
            n.PosAt = n.Playing && at > DateTime.UtcNow.AddDays(-1) && at <= DateTime.UtcNow.AddSeconds(5) ? at : DateTime.UtcNow;
            n.CanNext = pi.Controls.IsNextEnabled;
            n.CanPrev = pi.Controls.IsPreviousEnabled;
            if (n.Key != lastKey && p.Thumbnail != null)
            {
                try
                {
                    var stream = Wait(p.Thumbnail.OpenReadAsync());
                    var reader = new DataReader(stream);
                    uint size = (uint)stream.Size;
                    Wait(reader.LoadAsync(size));
                    art = new byte[size];
                    reader.ReadBytes(art);
                }
                catch { art = null; }
            }
            return n;
        }

        // 1 = play/pause, 2 = next, 3 = previous
        public static void Command(int c)
        {
            var s = mgr == null ? null : mgr.GetCurrentSession();
            if (s == null) return;
            if (c == 1) Wait(s.TryTogglePlayPauseAsync());
            else if (c == 2) Wait(s.TrySkipNextAsync());
            else if (c == 3) Wait(s.TrySkipPreviousAsync());
        }
    }
}

// A capsule-shaped practice bot. U is its body width; Ground is where its feet stand.
class Bot
{
    public double X, Ground, U, Vx, Speed, Origin, Range, DirTimer, Hp, Flash, DeadT, SpawnT;
    public bool Strafe, Dead;
}

class Trainer : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMsg msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct NativeMsg { public IntPtr hWnd; public uint msg; public IntPtr wParam, lParam; public uint time; public Point pt; }
    const int WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003;

    const string TRK = "Tracking", FLK = "Flick", BOT = "Bots", RCT = "Reaction", FND = "Sens finder", HTK = "Health Track";
    static readonly double[] DrainRate = { 110, 80, 65, 55 };   // health per second while your crosshair is on the ball
    const string AppVersion = "1.6.7";
    const string Repo = "THATLMG/aim-trainer";
    static readonly string[] DiffNames = { "Easy", "Medium", "Hard", "Insane" };
    static readonly double[] BallR = { 0.045, 0.033, 0.024, 0.017 };
    static readonly double[] BallSpeed = { 0.22, 0.36, 0.50, 0.65 };
    static readonly double[] FlickR = { 0.042, 0.032, 0.024, 0.017 };
    static readonly double[] FlickMin = { 0.12, 0.16, 0.20, 0.25 };
    static readonly double[] FlickMax = { 0.28, 0.36, 0.45, 0.50 };
    static readonly double[] FlickTimeout = { 0, 0, 1.6, 1.0 };
    static readonly double[] BotU = { 0.075, 0.06, 0.05, 0.04 };          // body width as a share of screen height
    static readonly double[] BotStrafeShare = { 0.3, 0.5, 0.7, 0.85 };    // how many bots strafe instead of standing
    static readonly double[] BotSpeed = { 2.5, 3.5, 4.5, 6.0 };           // body widths per second
    static readonly int[] BotCount = { 2, 3, 4, 5 };

    static readonly Color UiBg = Color.FromArgb(15, 17, 21), Panel = Color.FromArgb(27, 30, 36), PanelHi = Color.FromArgb(38, 42, 50);
    static readonly Color Line = Color.FromArgb(55, 60, 70), TextC = Color.FromArgb(236, 239, 243), Muted = Color.FromArgb(150, 158, 170);
    static readonly Color Good = Color.FromArgb(84, 204, 124);

    List<GameInfo> games = new List<GameInfo>();
    GameInfo game;
    string mode = TRK;
    int diff = 1;
    enum Scr { Setup, Home, Game, Play, Results, Loadout }
    Scr scr = Scr.Home;

    const string AppName = "LMG's Aim Trainer";
    const string SiteUrl = "https://thatlmg.github.io/aim-trainer/";
    const string LeaderboardUrl = SiteUrl + "board.html";
    const string DiscordAppId = "1564084223093121065";   // the Discord application named "LMG's Aim Trainer"
    const string CodeSecret = "aimtrainer-board-v1-7Qp2xK";   // must match the leaderboard page
    const string SupabaseUrl = "https://lzigsxqthaqzlanvygmm.supabase.co";
    const string SupabaseKey = "sb_publishable_6vUOyKqJceEClfugBL2aWA_HjELExo0";   // public key, safe to ship (same as docs/config.js)

    // Notices: short messages from the owner to every player. Only a PC with the owner key file can send them.
    string ownerKey = "";
    long noticeId, seenNotice;
    string noticeText = "", noticeMsg = "";
    bool noticeImportant;
    // Owner only: reviews of 4/10 and below from the website that this PC hasn't seen yet.
    string seenReviewAt = "", lowReviewAt = "", lowReviewText = "";
    int lowReviewCount;
    const string ReviewsUrl = SiteUrl + "reviews.html";

    Dictionary<string, double[]> settings = new Dictionary<string, double[]>();
    List<Rec> recs = new List<Rec>();
    string dataPath;
    string playerName = "";
    double globalDpi = 800;
    string setupError = "", scoreCode = "", copyNote = "";
    TextBox sensBox, dpiBox, nameBox, setupDpiBox;
    bool loadingBoxes;
    List<Btn> btns = new List<Btn>();
    Point mouse;
    float S = 1;
    Font fTitle, fH2, fH3, fBody, fSmall, fLabel, fBig, fHuge, fBtn;
    Stopwatch sw = Stopwatch.StartNew();
    Random rnd = new Random();

    // input
    IntPtr rawBuf = Marshal.AllocHGlobal(256);
    bool useRaw, locked;
    int rawDx, rawDy;

    // round state
    string playMode;
    int playDiff;
    double last, timeLeft, roundLen, countdown, pxPerCount, mult = 1;
    float cx, cy;
    RectangleF arena;

    // tracking ball
    double bx, by, bvx, bvy, dirX, dirY, heading, turnRate, turnTimer, changeTimer, speedMul = 1, jumpOff, jumpV, jumpTimer, br, ballSpeed;
    double onTime, totTime, leadSum, leadTime, crossPath, ballPath;

    // flick
    double tx, ty, tr, spawnT, flickD, ux, uy, startX, startY, smoothSpd, peakSpd;
    bool primaryDone;
    int hits, misses, expired;
    List<double> hitTimes = new List<double>(), progress = new List<double>();

    // health track: tracking where staying on the ball drains its health
    double ballHp = 100, htSpawnT;
    int htKills;
    List<double> htTtk = new List<double>();

    // effects
    class Particle { public double X, Y, VX, VY, Life, MaxLife, Size; public Color C; }
    class FloatText { public double X, Y, Life; public string Text; public Color C; }
    class FeedItem { public string Text; public bool Head; public double Life; }
    List<Particle> fx = new List<Particle>();
    List<FloatText> floats = new List<FloatText>();
    List<FeedItem> feed = new List<FeedItem>();
    int combo, bestCombo;
    double comboPop, shakeT, flashT;
    bool soundOn = true, shakeOn = true;

    // gun on screen (Flick and Bots)
    double recoil, muzzleT, sway, tracerT;
    float tracerX, tracerY;
    string gunId = "warden", skinId = "standard";
    bool showGun = true;
    double bestRankEver = -1;   // highest rank index reached, for unlocking skins
    int bestBotKills;
    int resRank = -1;
    double resScore, resAnimStart;
    List<string> newUnlocks = new List<string>();
    System.Windows.Forms.Timer fxTimer = new System.Windows.Forms.Timer { Interval = 33 };

    // bots
    List<Bot> bots = new List<Bot>();
    int shots, botHits, headHits, kills, headKills;
    List<double> ttk = new List<double>();
    double hitMarkT;
    bool hitMarkHead;
    List<double[]> path = new List<double[]>();   // crosshair x, y, speed since the last kill, for the coach
    double pathStartX, pathStartY;
    bool pathFresh;

    // music player and lyrics (filled in by background threads)
    volatile NowPlaying np;
    volatile Image songArt;
    volatile List<LyricLine> lyrics;
    volatile string lyricsState = "", lyricsKey = "";
    volatile bool lyricsSynced, musicDead;
    bool musicOn = true, lyricsInRound = true, lyricsOpen = false;
    int musicCmd;
    System.Windows.Forms.Timer musicTimer = new System.Windows.Forms.Timer { Interval = 250 };

    // Discord status: the UI thread writes these, the Discord thread sends them
    volatile string rpcDetails = "", rpcState = "", rpcGame = "";
    volatile bool discordOn = true, discordConnected;

    // Optional mouse acceleration: faster swipes travel further (similar to Windows' "Enhance pointer precision").
    // Speed is measured in counts per millisecond at 800 DPI so the curve feels the same at any DPI.
    bool accelOn = false;
    double AccelMult(int mx, int my, double dt)
    {
        if (!accelOn || dt <= 0 || (mx == 0 && my == 0) || game == null) return 1;
        double speed = Math.Sqrt(mx * mx + my * my) / (Math.Max(dt, 0.001) * 1000) * 800.0 / Dpi(game);
        return Math.Min(2.5, 1 + 0.25 * speed);
    }
    static readonly long AppStart = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    // updates and news
    string updVersion = "", updNotes = "", updUrl = "", updState = "";
    string newsId = "", newsText = "", seenNews = "";
    string discordInvite = "";   // set from news.json, so the invite can change without an app update

    // reaction: 0 intro, 1 red, 2 green, 3 showing a time, 4 false start
    int rState;
    double greenAt, greenShown, lastReaction;
    bool markGreen;
    List<double> rTimes = new List<double>();

    // sens finder
    double[] fMults, fScores;
    int fIdx;

    // results
    string resTitle, resMain, resLabel, coachHead, coachText;
    List<string> resLines = new List<string>();
    double sugSens = double.NaN;
    bool resPB, sugApplied, showCoach;

    public Trainer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Text = AppName;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.FromPoint(Cursor.Position).Bounds;
        WindowState = FormWindowState.Maximized;
        BackColor = UiBg;
        KeyPreview = true;
        Icon = MakeIcon();

        fTitle = new Font("Bahnschrift", 30, FontStyle.Bold);
        fH2 = new Font("Bahnschrift", 17, FontStyle.Bold);
        fH3 = new Font("Bahnschrift", 13, FontStyle.Bold);
        fBody = new Font("Segoe UI", 11);
        fSmall = new Font("Segoe UI", 9.5f);
        fLabel = new Font("Bahnschrift", 9.5f, FontStyle.Bold);
        fBig = new Font("Bahnschrift", 44, FontStyle.Bold);
        fHuge = new Font("Bahnschrift", 84, FontStyle.Bold);
        fBtn = new Font("Segoe UI Semibold", 11);

        AddGames();
        dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AimTrainer", "data.txt");
        try
        {
            string keyFile = Path.Combine(Path.GetDirectoryName(dataPath), "owner.key");
            if (File.Exists(keyFile)) ownerKey = File.ReadAllText(keyFile).Trim();
        }
        catch { }
        LoadData();

        sensBox = MakeBox();
        dpiBox = MakeBox();
        sensBox.TextChanged += delegate { BoxChanged(); };
        dpiBox.TextChanged += delegate { BoxChanged(); };
        nameBox = MakeBox();
        nameBox.MaxLength = 16;
        setupDpiBox = MakeBox();
        if (playerName == "") { scr = Scr.Setup; ShowSetupBoxes(true); }

        MouseMove += delegate(object s, MouseEventArgs e) { if (scr != Scr.Play) { mouse = e.Location; Invalidate(); } };
        MouseDown += OnDown;
        KeyDown += OnKey;
        Deactivate += delegate { if (scr == Scr.Play) AbortPlay(); };
        Resize += delegate { Invalidate(); };
        FormClosing += delegate { UnlockMouse(); };
        Application.Idle += OnIdle;
    }

    void AddGames()
    {
        games.Add(new GameInfo { Id = "minecraft", Name = "Minecraft", Tagline = "PvP strafes and jump-hits", Suffix = "%", Minecraft = true, HFov = 102, DefaultSens = 100, Decimals = 0, HorizBias = 0.6, Jumps = true, SizeMul = 1.25, Square = true, Band = 0.8, Accent = Color.FromArgb(108, 178, 60), Bg = Color.FromArgb(22, 28, 20), Note = "Use the Sensitivity % from Options > Controls > Mouse Settings." });
        games.Add(new GameInfo { Id = "roblox", Name = "Roblox", Tagline = "Bigger, bouncy targets", Yaw = 0.025, HFov = 102, DefaultSens = 1.0, Decimals = 2, HorizBias = 0.5, Jumps = true, SizeMul = 1.3, Band = 0.9, Accent = Color.FromArgb(40, 160, 255), Bg = Color.FromArgb(18, 23, 32), Note = "Roblox sens varies between games, so the conversion is approximate." });
        games.Add(new GameInfo { Id = "cs2", Name = "CS2", Tagline = "Counter-strafes at head height", Yaw = 0.022, HFov = 106.26, DefaultSens = 1.5, Decimals = 2, HorizBias = 0.9, SizeMul = 0.9, Band = 0.25, Accent = Color.FromArgb(240, 162, 59), Bg = Color.FromArgb(26, 23, 19) });
        games.Add(new GameInfo { Id = "valorant", Name = "Valorant", Tagline = "Sharp flicks, tight strafes", Yaw = 0.07, HFov = 103, DefaultSens = 0.35, Decimals = 3, HorizBias = 0.9, SizeMul = 0.85, Band = 0.25, Accent = Color.FromArgb(255, 84, 98), Bg = Color.FromArgb(27, 19, 23) });
        games.Add(new GameInfo { Id = "fortnite", Name = "Fortnite", Tagline = "Build-fight tracking", Suffix = "%", Yaw = 0.005555, HFov = 103, DefaultSens = 6.5, Decimals = 1, HorizBias = 0.5, Jumps = true, Band = 0.8, Accent = Color.FromArgb(166, 102, 255), Bg = Color.FromArgb(22, 19, 32), Note = "Use the X-axis sensitivity %." });
        games.Add(new GameInfo { Id = "overwatch", Name = "Overwatch 2", Tagline = "Fast heroes, lots of vertical", Yaw = 0.0066, HFov = 103, DefaultSens = 5, Decimals = 2, HorizBias = 0.55, Jumps = true, SizeMul = 1.1, Band = 0.7, Accent = Color.FromArgb(249, 158, 26), Bg = Color.FromArgb(21, 24, 30) });
        games.Add(new GameInfo { Id = "apex", Name = "Apex Legends", Tagline = "Long strafes and slides", Yaw = 0.022, HFov = 110, DefaultSens = 2.0, Decimals = 2, HorizBias = 0.7, Band = 0.6, Accent = Color.FromArgb(222, 84, 68), Bg = Color.FromArgb(26, 21, 21) });
        games.Add(new GameInfo { Id = "custom", Name = "Custom", Tagline = "Any game with CS2-style sens", Yaw = 0.022, HFov = 103, DefaultSens = 1.0, Decimals = 2, HorizBias = 0.6, Band = 0.8, Accent = Color.FromArgb(60, 196, 176), Bg = Color.FromArgb(17, 26, 26), Note = "Uses CS2 / Apex / Source sensitivity numbers." });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        using (var g = CreateGraphics()) S = g.DpiX / 96f;
        var dev = new RAWINPUTDEVICE[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = 0, Target = Handle } };
        useRaw = RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        var t = new Thread(CheckOnline) { IsBackground = true };
        t.Start();
        // Keep checking every 5 minutes while the app stays open, but only in the menus, never during a round.
        var updTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        updTimer.Tick += delegate { if (scr != Scr.Play) new Thread(delegate() { CheckUpdate(); CheckNotices(); CheckLowReviews(); }) { IsBackground = true }.Start(); };
        updTimer.Start();
        var mt = new Thread(MusicLoop) { IsBackground = true };
        mt.Start();
        musicTimer.Tick += delegate
        {
            UpdatePresenceText();
            if (scr != Scr.Play && scr != Scr.Setup && MusicVisible) Invalidate();
        };
        musicTimer.Start();
        fxTimer.Tick += delegate { if (scr == Scr.Home || scr == Scr.Game || scr == Scr.Results || scr == Scr.Loadout) Invalidate(); };
        fxTimer.Start();
        var dt = new Thread(DiscordLoop) { IsBackground = true };
        dt.Start();
    }

    // ---------- Discord status (Rich Presence over Discord's local pipe) ----------
    void UpdatePresenceText()
    {
        string d, s = "", g = "";
        string run = game == null ? "" : playMode == FND ? game.Name + " · Sens finder"
            : playMode == RCT ? game.Name + " · Reaction test"
            : game.Name + " · " + playMode + " · " + DiffNames[playDiff];
        if (scr == Scr.Setup) d = "Setting up";
        else if (scr == Scr.Loadout) d = "Picking a gun skin";
        else if (scr == Scr.Home || game == null) d = "In the menus";
        else if (scr == Scr.Game) { d = game.Name + " · choosing a mode"; s = "Sens " + game.Fmt(Sens(game)); g = game.Id; }
        else if (scr == Scr.Play)
        {
            d = run; g = game.Id;
            if (countdown > 0) s = "Get ready…";
            else if (playMode == FLK) s = hits + " hits · " + (hits + misses > 0 ? hits * 100 / (hits + misses) : 0) + "% accuracy";
            else if (playMode == BOT) s = kills + " kills · " + (botHits > 0 ? headHits * 100 / botHits : 0) + "% headshots";
            else if (playMode == HTK) s = htKills + " kills · " + (totTime > 0 ? (onTime / totTime * 100).ToString("0") : "0") + "% on target";
            else if (playMode == RCT) s = "Try " + Math.Min(5, rTimes.Count + 1) + " of 5";
            else if (playMode == FND) s = "Round " + Math.Min(5, fIdx + 1) + " of 5";
            else s = (totTime > 0 ? (onTime / totTime * 100).ToString("0") : "0") + "% on target";
        }
        else
        {
            d = run + " · results"; g = game.Id;
            s = playMode == TRK ? resMain + " on target" : playMode == FLK ? resMain + " hits" : (playMode == BOT || playMode == HTK) ? resMain + " kills"
              : playMode == RCT ? resMain + " average" : "Best sens " + resMain;
        }
        rpcDetails = d; rpcState = s; rpcGame = g;
    }

    string ActivityJson()
    {
        Func<string, string> fit = t => t.Length > 128 ? t.Substring(0, 127) + "…" : t;
        var assets = new Dictionary<string, object> { { "large_image", SiteUrl + "rpc/logo.png" }, { "large_text", AppName + " v" + AppVersion } };
        string g = rpcGame;
        if (g != "") { assets["small_image"] = SiteUrl + "rpc/" + g + ".png"; assets["small_text"] = games.First(x => x.Id == g).Name; }
        var act = new Dictionary<string, object>
        {
            { "details", fit(rpcDetails) },
            { "timestamps", new Dictionary<string, object> { { "start", AppStart } } },
            { "assets", assets },
            { "buttons", new object[] {
                new Dictionary<string, object> { { "label", "Get LMG's Aim Trainer" }, { "url", SiteUrl } },
                discordInvite != ""
                    ? new Dictionary<string, object> { { "label", "Join the Discord" }, { "url", discordInvite } }
                    : new Dictionary<string, object> { { "label", "Leaderboard" }, { "url", LeaderboardUrl } } } }
        };
        if (rpcState.Length >= 2) act["state"] = fit(rpcState);
        return new JavaScriptSerializer().Serialize(act);
    }

    void DiscordLoop()
    {
        System.IO.Pipes.NamedPipeClientStream pipe = null;
        string lastSent = null;
        DateTime lastAt = DateTime.MinValue;
        var js = new JavaScriptSerializer();
        int pid = Process.GetCurrentProcess().Id;
        while (true)
        {
            try
            {
                if (!discordOn || DiscordAppId == "")
                {
                    if (pipe != null) { try { RpcSend(pipe, 1, "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + pid + "},\"nonce\":\"" + Guid.NewGuid() + "\"}"); RpcRead(pipe); } catch { } pipe.Dispose(); pipe = null; }
                    discordConnected = false; lastSent = null;
                    Thread.Sleep(1000);
                    continue;
                }
                if (pipe == null)
                {
                    pipe = RpcConnect();
                    discordConnected = pipe != null;
                    lastSent = null;
                    if (pipe == null) { Thread.Sleep(15000); continue; }   // Discord isn't running; check again later
                }
                string act = ActivityJson();
                // Discord allows about 5 updates per 20 seconds, so send at most every 4 seconds.
                if (act != lastSent && (DateTime.UtcNow - lastAt).TotalSeconds >= 4)
                {
                    RpcSend(pipe, 1, "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + pid + ",\"activity\":" + act + "},\"nonce\":\"" + Guid.NewGuid() + "\"}");
                    RpcRead(pipe);
                    lastSent = act; lastAt = DateTime.UtcNow;
                }
            }
            catch
            {
                try { if (pipe != null) pipe.Dispose(); } catch { }
                pipe = null; discordConnected = false;
                Thread.Sleep(5000);
            }
            Thread.Sleep(1000);
        }
    }

    static System.IO.Pipes.NamedPipeClientStream RpcConnect()
    {
        for (int i = 0; i < 10; i++)
        {
            var p = new System.IO.Pipes.NamedPipeClientStream(".", "discord-ipc-" + i, System.IO.Pipes.PipeDirection.InOut);
            try
            {
                p.Connect(250);
                RpcSend(p, 0, "{\"v\":1,\"client_id\":\"" + DiscordAppId + "\"}");
                if (RpcRead(p).Contains("READY")) return p;
            }
            catch { }
            p.Dispose();
        }
        return null;
    }

    // Discord's frame format: 4-byte opcode, 4-byte length, then UTF-8 JSON.
    static void RpcSend(Stream s, int op, string json)
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json), head = new byte[8];
        BitConverter.GetBytes(op).CopyTo(head, 0);
        BitConverter.GetBytes(body.Length).CopyTo(head, 4);
        s.Write(head, 0, 8); s.Write(body, 0, body.Length); s.Flush();
    }

    static string RpcRead(Stream s)
    {
        byte[] head = ReadExact(s, 8);
        int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
        string body = System.Text.Encoding.UTF8.GetString(ReadExact(s, len));
        if (op == 2) throw new IOException("Discord closed the connection: " + body);
        return body;
    }

    static byte[] ReadExact(Stream s, int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n) { int r = s.Read(buf, got, n - got); if (r <= 0) throw new IOException("pipe closed"); got += r; }
        return buf;
    }

    // ---------- music ----------
    bool MusicVisible { get { return musicOn && !musicDead; } }

    void MusicLoop()
    {
        string lastKey = null;
        int fails = 0;
        bool everWorked = false;
        while (true)
        {
            int cmd = Interlocked.Exchange(ref musicCmd, 0);
            if (!musicOn) { np = null; lastKey = null; Thread.Sleep(800); continue; }
            try
            {
                if (cmd != 0) MediaBridge.Media.Command(cmd);
                byte[] art;
                var n = MediaBridge.Media.Poll(lastKey, out art);
                everWorked = true; fails = 0;
                if (n != null && n.Key != lastKey)
                {
                    lastKey = n.Key;
                    songArt = art != null ? LoadArt(art) : null;
                    if (n.Title != "") StartLyrics(n);
                }
                if (n == null) lastKey = null;
                np = n;
            }
            catch (Exception e)
            {
                // Missing Windows media support shows up as a load error: hide the music bar for good.
                if (e is TypeLoadException || e is FileNotFoundException || e is MissingMethodException || (!everWorked && ++fails > 5))
                {
                    musicDead = true; np = null;
                    return;
                }
            }
            Thread.Sleep(cmd != 0 ? 150 : 500);
        }
    }

    static Image LoadArt(byte[] bytes)
    {
        try { using (var ms = new MemoryStream(bytes)) using (var img = Image.FromStream(ms)) return new Bitmap(img); }
        catch { return null; }
    }

    void SendMusic(int cmd) { Interlocked.Exchange(ref musicCmd, cmd); }

    void StartLyrics(NowPlaying n)
    {
        lyricsKey = n.Key;
        lyrics = null;
        lyricsState = "loading";
        var t = new Thread(delegate()
        {
            FetchLyrics(n);
            // The lyrics site is sometimes busy: try once more before giving up.
            if (lyricsState == "error" && lyricsKey == n.Key) { Thread.Sleep(2500); FetchLyrics(n); }
        }) { IsBackground = true };
        t.Start();
    }

    static readonly System.Text.RegularExpressions.Regex Junk = new System.Text.RegularExpressions.Regex(
        @"\s*[\(\[][^\)\]]*(official|lyric|audio|video|visuali[sz]er|hd|4k|mv|remaster)[^\)\]]*[\)\]]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex LrcLine = new System.Text.RegularExpressions.Regex(@"^\[(\d+):(\d+(?:\.\d+)?)\]\s?(.*)$");

    void FetchLyrics(NowPlaying n)
    {
        string artist = n.Artist, title = Junk.Replace(n.Title, "").Trim();
        string app = n.App.ToLowerInvariant();
        bool browser = app.Contains("chrome") || app.Contains("msedge") || app.Contains("firefox") || app.Contains("opera") || app.Contains("brave");
        // YouTube titles are usually "Artist - Song" with the channel as the artist.
        if (browser && title.Contains(" - ")) { int i = title.IndexOf(" - "); artist = title.Substring(0, i).Trim(); title = title.Substring(i + 3).Trim(); }
        artist = artist.Replace(" - Topic", "").Trim();

        string state = "none", synced = null, plain = null;
        try
        {
            var js = new JavaScriptSerializer();
            using (var wc = Web())
            {
                Dictionary<string, object> hit = null;
                if (n.Album != "" && n.Dur > 0)
                {
                    try
                    {
                        hit = js.DeserializeObject(wc.DownloadString("https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(artist)
                            + "&track_name=" + Uri.EscapeDataString(title) + "&album_name=" + Uri.EscapeDataString(n.Album)
                            + "&duration=" + (int)Math.Round(n.Dur))) as Dictionary<string, object>;
                    }
                    catch (WebException) { }   // 404 = not found under that exact album
                }
                if (hit == null || (Str(hit, "syncedLyrics") == null && Str(hit, "plainLyrics") == null && !Bool(hit, "instrumental")))
                {
                    var list = js.DeserializeObject(wc.DownloadString("https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
                        + (artist != "" ? "&artist_name=" + Uri.EscapeDataString(artist) : ""))) as object[];
                    hit = null;
                    if (list != null)
                    {
                        var hits = list.OfType<Dictionary<string, object>>().ToList();
                        Func<Dictionary<string, object>, bool> closeLength = h => n.Dur <= 0 || !h.ContainsKey("duration") || h["duration"] == null || Math.Abs(Convert.ToDouble(h["duration"]) - n.Dur) < 4;
                        hit = hits.FirstOrDefault(h => Str(h, "syncedLyrics") != null && closeLength(h))
                            ?? hits.FirstOrDefault(h => Str(h, "syncedLyrics") != null)
                            ?? hits.FirstOrDefault(h => Str(h, "plainLyrics") != null);
                    }
                }
                if (hit != null)
                {
                    synced = Str(hit, "syncedLyrics"); plain = Str(hit, "plainLyrics");
                    if (Bool(hit, "instrumental")) state = "instrumental";
                }
            }
        }
        catch { state = "error"; }

        var lines = new List<LyricLine>();
        bool isSynced = false;
        if (synced != null)
        {
            foreach (var raw in synced.Split('\n'))
            {
                var mm = LrcLine.Match(raw.Trim());
                if (mm.Success)
                    lines.Add(new LyricLine { T = int.Parse(mm.Groups[1].Value) * 60 + double.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture), Text = mm.Groups[3].Value.Trim() });
            }
            isSynced = lines.Count > 0;
        }
        if (!isSynced && plain != null)
            foreach (var raw in plain.Split('\n')) if (raw.Trim() != "") lines.Add(new LyricLine { Text = raw.Trim() });
        if (lines.Count > 0) state = "ok";

        if (lyricsKey != n.Key) return;   // the song changed while we were looking
        lyricsSynced = isSynced;
        lyrics = lines;
        lyricsState = state;
    }

    static string Str(Dictionary<string, object> d, string k)
    {
        object v;
        return d.TryGetValue(k, out v) && v is string && ((string)v).Trim() != "" ? (string)v : null;
    }
    static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }

    // Index of the lyric line to highlight right now.
    int LyricIndex(NowPlaying n, List<LyricLine> ls)
    {
        if (ls == null || ls.Count == 0 || n == null) return -1;
        double cur = n.Current;
        if (!lyricsSynced) return n.Dur > 0 ? Math.Min(ls.Count - 1, (int)(cur / n.Dur * ls.Count)) : 0;
        int idx = -1;
        for (int i = 0; i < ls.Count; i++) if (ls[i].T <= cur + 0.2) idx = i; else break;
        return idx;
    }

    // ---------- updates and news (from the public GitHub repo) ----------
    static WebClient Web()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2, which GitHub requires
        var wc = new WebClient { Encoding = System.Text.Encoding.UTF8 };
        wc.Headers["User-Agent"] = "AimTrainer/" + AppVersion;
        return wc;
    }

    void CheckOnline()
    {
        CheckUpdate();
        CheckNews();
        CheckNotices();
        CheckLowReviews();
    }

    // The newest notice from the last 24 hours, if this player hasn't closed it yet.
    void CheckNotices()
    {
        try
        {
            using (var wc = Web())
            {
                wc.Headers["apikey"] = SupabaseKey;
                string since = Uri.EscapeDataString(DateTime.UtcNow.AddHours(-24).ToString("yyyy-MM-ddTHH:mm:ssZ"));
                var list = new JavaScriptSerializer().DeserializeObject(wc.DownloadString(SupabaseUrl + "/rest/v1/notices?select=id,text,important&order=id.desc&limit=1&created_at=gt." + since)) as object[];
                if (list == null || list.Length == 0) return;
                var n = (Dictionary<string, object>)list[0];
                long id = Convert.ToInt64(n["id"]);
                string text = Convert.ToString(n["text"]);
                bool imp = n["important"] is bool && (bool)n["important"];
                BeginInvoke((MethodInvoker)delegate { if (id > seenNotice && id != noticeId) { noticeId = id; noticeText = text; noticeImportant = imp; Invalidate(); } });
            }
        }
        catch { }
    }

    void DismissNotice()
    {
        seenNotice = noticeId;
        noticeText = "";
        Save();
    }

    // Owner only: a small window to type a notice, then send it to the database.
    void OpenNoticeComposer()
    {
        var f = new Form
        {
            Text = "Send a notice to every player", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, BackColor = Panel, ForeColor = TextC, Font = fBody,
            ClientSize = new Size((int)P(560), (int)P(290))
        };
        var info = new Label { Text = "Everyone sees this at the top of their menus within 5 minutes. It never interrupts a round.", ForeColor = Muted, Font = fSmall, Bounds = new Rectangle((int)P(20), (int)P(16), (int)P(520), (int)P(22)) };
        var box = new TextBox { Multiline = true, MaxLength = 300, BackColor = UiBg, ForeColor = TextC, BorderStyle = BorderStyle.FixedSingle, Bounds = new Rectangle((int)P(20), (int)P(44), (int)P(520), (int)P(130)) };
        var count = new Label { Text = "0 / 300", ForeColor = Muted, Font = fSmall, TextAlign = ContentAlignment.TopRight, Bounds = new Rectangle((int)P(380), (int)P(178), (int)P(160), (int)P(22)) };
        box.TextChanged += delegate { count.Text = box.Text.Length + " / 300"; };
        var imp = new CheckBox { Text = "Important (highlighted in red)", ForeColor = TextC, Bounds = new Rectangle((int)P(20), (int)P(178), (int)P(340), (int)P(26)) };
        var send = new Button { Text = "Send", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = OnColor(Accent), Bounds = new Rectangle((int)P(300), (int)P(226), (int)P(120), (int)P(42)) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = PanelHi, ForeColor = TextC, Bounds = new Rectangle((int)P(430), (int)P(226), (int)P(110), (int)P(42)) };
        send.FlatAppearance.BorderSize = 0; cancel.FlatAppearance.BorderSize = 0;
        f.Controls.AddRange(new Control[] { info, box, count, imp, send, cancel });
        f.CancelButton = cancel;
        using (f)
        {
            if (f.ShowDialog(this) != DialogResult.OK || box.Text.Trim() == "") return;
            string text = box.Text.Trim().Replace("\r\n", " ").Replace("\n", " ");
            bool important = imp.Checked;
            noticeMsg = "Sending…"; Invalidate();
            new Thread(delegate() { SendNotice(text, important); }) { IsBackground = true }.Start();
        }
    }

    void SendNotice(string text, bool important)
    {
        string msg;
        try
        {
            using (var wc = Web())
            {
                wc.Headers["apikey"] = SupabaseKey;
                wc.Headers["Content-Type"] = "application/json";
                var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "p_key", ownerKey }, { "p_text", text }, { "p_important", important } });
                wc.UploadString(SupabaseUrl + "/rest/v1/rpc/send_notice", "POST", body);
            }
            msg = "Sent! Everyone sees it within 5 minutes.";
            CheckNotices();
        }
        catch (WebException ex)
        {
            string detail = "";
            try { using (var r = new StreamReader(ex.Response.GetResponseStream())) detail = r.ReadToEnd(); } catch { }
            msg = detail.Contains("send_notice") && detail.Contains("not find") ? "Couldn't send: run supabase/005_notices.sql in Supabase first."
                : detail.Contains("Not allowed") ? "Couldn't send: this PC's owner key isn't accepted."
                : "Couldn't send. Check your internet and try again.";
        }
        catch { msg = "Couldn't send. Check your internet and try again."; }
        BeginInvoke((MethodInvoker)delegate { noticeMsg = msg; Invalidate(); });
    }

    // Owner only: asks the database for reviews of 4/10 and below changed since the last one seen on this PC.
    void CheckLowReviews()
    {
        if (ownerKey == "") return;
        try
        {
            var rows = OwnerReviews(4, seenReviewAt == "" ? "1970-01-01T00:00:00Z" : seenReviewAt);
            if (rows.Count == 0) return;
            string at = Convert.ToString(rows[0]["updated_at"]);
            string text = Convert.ToInt32(rows[0]["rating"]) + "/10  ·  " + Convert.ToString(rows[0]["text"]).Replace("\n", " ");
            int n = rows.Count;
            BeginInvoke((MethodInvoker)delegate { if (at != seenReviewAt) { lowReviewAt = at; lowReviewText = text; lowReviewCount = n; Invalidate(); } });
        }
        catch { }
    }

    List<Dictionary<string, object>> OwnerReviews(int maxRating, string since)
    {
        using (var wc = Web())
        {
            wc.Headers["apikey"] = SupabaseKey;
            wc.Headers["Content-Type"] = "application/json";
            var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "p_key", ownerKey }, { "p_max_rating", maxRating }, { "p_since", since } });
            var list = new JavaScriptSerializer().DeserializeObject(wc.UploadString(SupabaseUrl + "/rest/v1/rpc/owner_reviews", "POST", body)) as object[];
            return list == null ? new List<Dictionary<string, object>>() : list.Cast<Dictionary<string, object>>().ToList();
        }
    }

    void DismissLowReviews()
    {
        if (lowReviewAt != "") seenReviewAt = lowReviewAt;
        lowReviewText = ""; lowReviewCount = 0;
        Save();
    }

    // Owner only: a window listing every review (newest first), with the low ones marked.
    void OpenReviewsWindow()
    {
        DismissLowReviews();
        var f = new Form
        {
            Text = "Reviews", FormBorderStyle = FormBorderStyle.Sizable, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, ShowInTaskbar = false, BackColor = Panel, ForeColor = TextC, Font = fBody,
            ClientSize = new Size((int)P(720), (int)P(560))
        };
        var info = new Label { Text = "Loading…", ForeColor = Muted, Font = fSmall, Dock = DockStyle.Top, Height = (int)P(30), Padding = new Padding((int)P(12), (int)P(8), 0, 0) };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = UiBg, ForeColor = TextC, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill };
        f.Controls.Add(box); f.Controls.Add(info);
        f.Shown += delegate
        {
            new Thread(delegate()
            {
                string head, all;
                try
                {
                    var rows = OwnerReviews(10, "1970-01-01T00:00:00Z");
                    var sb = new System.Text.StringBuilder();
                    foreach (var r in rows)
                    {
                        int rating = Convert.ToInt32(r["rating"]);
                        DateTime when; DateTime.TryParse(Convert.ToString(r["updated_at"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out when);
                        string name = Convert.ToString(r["name"]), plat = Convert.ToString(r["platform"]), text = Convert.ToString(r["text"]);
                        sb.Append(rating <= 4 ? "⚠ " : "").Append(rating).Append("/10   ").Append(name == "" ? "Anonymous" : name)
                          .Append(plat != "" ? "  ·  " + plat : "").Append("  ·  ").Append(when.ToString("MMM d, yyyy h:mm tt")).Append("\r\n")
                          .Append(text == "" ? "(no comment)" : text.Replace("\r", "").Replace("\n", "\r\n")).Append("\r\n\r\n");
                    }
                    int low = rows.Count(r => Convert.ToInt32(r["rating"]) <= 4);
                    head = rows.Count == 0 ? "No reviews yet." : rows.Count + (rows.Count == 1 ? " review" : " reviews") + ", " + low + " at 4/10 or below (marked ⚠). Newest first.";
                    all = sb.ToString();
                }
                catch (WebException ex)
                {
                    string detail = "";
                    try { using (var rd = new StreamReader(ex.Response.GetResponseStream())) detail = rd.ReadToEnd(); } catch { }
                    head = detail.Contains("owner_reviews") ? "Run supabase/006_reviews.sql in Supabase first."
                        : detail.Contains("Not allowed") ? "This PC's owner key isn't accepted." : "Couldn't load reviews. Check your internet.";
                    all = "";
                }
                catch { head = "Couldn't load reviews."; all = ""; }
                try { f.BeginInvoke((MethodInvoker)delegate { info.Text = head; box.Text = all; box.Select(0, 0); }); } catch { }
            }) { IsBackground = true }.Start();
        };
        using (f) f.ShowDialog(this);
    }

    void CheckUpdate()
    {
        var js = new JavaScriptSerializer();
        try
        {
            using (var wc = Web())
            {
                var rel = js.Deserialize<Dictionary<string, object>>(wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest"));
                string tag = ((string)rel["tag_name"]).TrimStart('v', 'V');
                // Any release that isn't this exact version counts as an update, so version numbers can restart (1.0.0) without stranding anyone.
                if (tag != "" && tag != AppVersion)
                {
                    string url = null;
                    foreach (var a in (System.Collections.IEnumerable)rel["assets"])
                    {
                        var d = (Dictionary<string, object>)a;
                        if ((string)d["name"] == "AimTrainer.exe") url = (string)d["browser_download_url"];
                    }
                    string body = rel.ContainsKey("body") && rel["body"] != null ? (string)rel["body"] : "";
                    string first = body.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '#', ' ')).FirstOrDefault(l => l.Length > 0) ?? "";
                    if (url != null)
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (updState == "downloading" || (updState == "later" && tag == updVersion)) return;   // "Later" lasts until the next version
                            updVersion = tag; updNotes = first; updUrl = url; updState = "available"; Invalidate();
                        });
                }
            }
        }
        catch { }
    }

    void CheckNews()
    {
        var js = new JavaScriptSerializer();
        try
        {
            using (var wc = Web())
            {
                var n = js.Deserialize<Dictionary<string, object>>(wc.DownloadString("https://raw.githubusercontent.com/" + Repo + "/main/news.json"));
                string id = n.ContainsKey("id") ? Convert.ToString(n["id"]) : "", text = n.ContainsKey("text") ? Convert.ToString(n["text"]) : "";
                string invite = n.ContainsKey("discord") ? Convert.ToString(n["discord"]) : "";
                if (invite.StartsWith("https://discord.gg/") || invite.StartsWith("https://discord.com/invite/"))
                    BeginInvoke((MethodInvoker)delegate { discordInvite = invite; Invalidate(); });
                if (id != "" && text != "")
                    BeginInvoke((MethodInvoker)delegate { if (id != seenNews) { newsId = id; newsText = text; Invalidate(); } });
            }
        }
        catch { }
    }

    void DoUpdate()
    {
        updState = "downloading";
        Invalidate();
        var t = new Thread(delegate()
        {
            string exe = Application.ExecutablePath, tmp = exe + ".new", old = exe + ".old";
            try
            {
                using (var wc = Web()) wc.DownloadFile(updUrl, tmp);
                var head = new byte[2];
                using (var f = File.OpenRead(tmp)) f.Read(head, 0, 2);
                if (head[0] != 'M' || head[1] != 'Z' || new FileInfo(tmp).Length < 30000) throw new Exception("not a program");
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);      // Windows lets a running program be renamed, not overwritten
                try { File.Move(tmp, exe); }
                catch { File.Move(old, exe); throw; }
                BeginInvoke((MethodInvoker)delegate { UnlockMouse(); Save(); Process.Start(exe); Close(); });
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                BeginInvoke((MethodInvoker)delegate { updState = "error"; Invalidate(); });
            }
        }) { IsBackground = true };
        t.Start();
    }

    static void OpenReleases()
    {
        try { Process.Start("https://github.com/" + Repo + "/releases/latest"); } catch { }
    }

    void DismissNews()
    {
        seenNews = newsId;
        newsText = "";
        Save();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) ReadRaw(m.LParam);
        base.WndProc(ref m);
    }

    void ReadRaw(IntPtr h)
    {
        uint header = (uint)(8 + 2 * IntPtr.Size), size = 0;
        GetRawInputData(h, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0 || size > 256) return;
        if (GetRawInputData(h, RID_INPUT, rawBuf, ref size, header) != size) return;
        if (Marshal.ReadInt32(rawBuf, 0) != 0) return;                  // not a mouse
        if ((Marshal.ReadInt16(rawBuf, (int)header) & 1) != 0) return;   // absolute (tablet / remote desktop)
        rawDx += Marshal.ReadInt32(rawBuf, (int)header + 12);
        rawDy += Marshal.ReadInt32(rawBuf, (int)header + 16);
    }

    // ---------- data ----------
    double Sens(GameInfo g) { double[] v; return settings.TryGetValue(g.Id, out v) ? v[0] : g.DefaultSens; }
    double Dpi(GameInfo g) { double[] v; return settings.TryGetValue(g.Id, out v) ? v[1] : globalDpi; }

    // Reads data.txt, or the backup copy if data.txt is missing, empty or unreadable, so an update or a crash
    // in the middle of saving never wipes someone's name, settings, ranks and unlocks.
    void LoadData()
    {
        string[] lines = ReadDataFile(dataPath);
        if (lines == null || !lines.Any(l => l.StartsWith("P\t"))) lines = ReadDataFile(dataPath + ".bak") ?? lines;
        if (lines == null) return;
        var ci = CultureInfo.InvariantCulture;
        foreach (var line in lines)
        {
            try   // one bad line skips just that line, not everything after it
            {
                var p = line.Split('\t');
                if (p[0] == "P" && p.Length >= 3) { playerName = CleanName(p[1]); globalDpi = double.Parse(p[2], ci); }
                if (p[0] == "N" && p.Length >= 2) seenNews = p[1];
                if (p[0] == "T" && p.Length >= 2) long.TryParse(p[1], out seenNotice);
                if (p[0] == "V" && p.Length >= 2) seenReviewAt = p[1];
                if (p[0] == "M" && p.Length >= 2) musicOn = p[1] == "1";
                if (p[0] == "M2" && p.Length >= 2) lyricsInRound = p[1] == "1";
                if (p[0] == "D" && p.Length >= 2) discordOn = p[1] == "1";
                if (p[0] == "A" && p.Length >= 2) accelOn = p[1] == "1";
                if (p[0] == "G" && p.Length >= 6) { gunId = p[1]; skinId = p[2]; showGun = p[3] == "1"; soundOn = p[4] == "1"; shakeOn = p[5] == "1"; }
                if (p[0] == "K" && p.Length >= 3) { bestRankEver = double.Parse(p[1], ci); bestBotKills = int.Parse(p[2], ci); }
                if (p[0] == "S" && p.Length >= 4) settings[p[1]] = new[] { double.Parse(p[2], ci), double.Parse(p[3], ci) };
                if (p[0] == "R" && p.Length >= 6)
                    recs.Add(new Rec { Game = p[1], Mode = p[2], Diff = int.Parse(p[3], ci), Score = double.Parse(p[4], ci), When = DateTime.Parse(p[5], ci) });
            }
            catch { }
        }
    }

    static string[] ReadDataFile(string path)
    {
        for (int i = 0; i < 5; i++)   // another copy of the app may be writing it right now
        {
            try { return File.Exists(path) ? File.ReadAllLines(path) : null; }
            catch (IOException) { Thread.Sleep(80); }
            catch { return null; }
        }
        return null;
    }

    void Save()
    {
        try
        {
            var ci = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            if (playerName != "") lines.Add(string.Format(ci, "P\t{0}\t{1}", playerName, globalDpi));
            if (seenNews != "") lines.Add("N\t" + seenNews.Replace("\t", " "));
            if (seenNotice > 0) lines.Add("T\t" + seenNotice);
            if (seenReviewAt != "") lines.Add("V\t" + seenReviewAt);
            lines.Add("M\t" + (musicOn ? "1" : "0"));
            lines.Add("M2\t" + (lyricsInRound ? "1" : "0"));
            lines.Add("D\t" + (discordOn ? "1" : "0"));
            lines.Add("A\t" + (accelOn ? "1" : "0"));
            lines.Add(string.Join("\t", new[] { "G", gunId, skinId, showGun ? "1" : "0", soundOn ? "1" : "0", shakeOn ? "1" : "0" }));
            lines.Add(string.Format(ci, "K\t{0}\t{1}", bestRankEver, bestBotKills));
            foreach (var kv in settings) lines.Add(string.Format(ci, "S\t{0}\t{1}\t{2}", kv.Key, kv.Value[0], kv.Value[1]));
            foreach (var r in recs) lines.Add(string.Format(ci, "R\t{0}\t{1}\t{2}\t{3}\t{4:yyyy-MM-dd HH:mm}", r.Game, r.Mode, r.Diff, r.Score, r.When));
            // Write a new file first and swap it in, keeping the last good copy as data.txt.bak, so the file is never half written.
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath));
            string tmp = dataPath + ".tmp", bak = dataPath + ".bak";
            File.WriteAllLines(tmp, lines);
            if (!File.Exists(dataPath)) File.Move(tmp, dataPath);
            else
                try { File.Replace(tmp, dataPath, bak, true); }
                catch { File.Copy(dataPath, bak, true); File.Copy(tmp, dataPath, true); File.Delete(tmp); }
        }
        catch { }
    }

    List<Rec> History(string m, int d)
    {
        return recs.Where(r => r.Game == game.Id && r.Mode == m && (m == RCT || r.Diff == d)).ToList();
    }

    bool SaveRec(string m, int d, double score, bool lowerBetter)
    {
        var prev = History(m, d);
        bool pb = prev.Count > 0 && (lowerBetter ? score < prev.Min(r => r.Score) : score > prev.Max(r => r.Score));
        resScore = score; resRank = RankIndex(m, d, score);
        recs.Add(new Rec { Game = game.Id, Mode = m, Diff = d, Score = score, When = DateTime.Now });
        Save();
        return pb;
    }

    // ---------- text boxes ----------
    TextBox MakeBox()
    {
        var t = new TextBox { Font = new Font("Segoe UI", 14), BorderStyle = BorderStyle.FixedSingle, BackColor = PanelHi, ForeColor = TextC, Visible = false };
        Controls.Add(t);
        return t;
    }

    void BoxChanged()
    {
        if (loadingBoxes || game == null) return;
        double s, d;
        if (TryNum(sensBox.Text, out s) && s > 0 && TryNum(dpiBox.Text, out d) && d >= 100 && d <= 50000)
        {
            settings[game.Id] = new[] { s, d };
            Save();
            Invalidate();
        }
    }

    static bool TryNum(string s, out double v)
    {
        return double.TryParse(s.Trim().TrimEnd('%').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    void ShowBoxes(bool show)
    {
        if (show && game != null)
        {
            loadingBoxes = true;
            sensBox.Text = game.Round(Sens(game)).ToString(CultureInfo.InvariantCulture);
            dpiBox.Text = Dpi(game).ToString(CultureInfo.InvariantCulture);
            loadingBoxes = false;
        }
        sensBox.Visible = dpiBox.Visible = show;
        if (!show) ActiveControl = null;
    }

    void Place(TextBox t, float x, float y, float w)
    {
        var p = new Point((int)x, (int)y);
        if (t.Location != p) t.Location = p;
        if (t.Width != (int)w) t.Width = (int)w;
    }

    // ---------- navigation ----------
    void GoHome() { scr = Scr.Home; ShowBoxes(false); Invalidate(); }
    void GoGame(GameInfo g) { game = g; scr = Scr.Game; ShowBoxes(true); Invalidate(); }

    // ---------- profile setup ----------
    void ShowSetupBoxes(bool show)
    {
        if (show)
        {
            nameBox.Text = playerName;
            setupDpiBox.Text = globalDpi.ToString(CultureInfo.InvariantCulture);
            setupError = "";
        }
        nameBox.Visible = setupDpiBox.Visible = show;
        if (show) nameBox.Focus(); else ActiveControl = null;
    }

    void GoSetup() { scr = Scr.Setup; ShowBoxes(false); ShowSetupBoxes(true); Invalidate(); }

    void LeaveSetup() { ShowSetupBoxes(false); GoHome(); }

    void FinishSetup()
    {
        string n = CleanName(nameBox.Text);
        double d;
        if (n.Length < 2) { setupError = "Pick a player name with at least 2 letters or numbers."; Invalidate(); return; }
        if (!TryNum(setupDpiBox.Text, out d) || d < 100 || d > 50000) { setupError = "Enter your mouse DPI as a number, like 800 or 1600."; Invalidate(); return; }
        // Games that were still using the old profile DPI follow the new one.
        foreach (var kv in settings) if (kv.Value[1] == globalDpi) kv.Value[1] = d;
        playerName = n;
        globalDpi = Math.Round(d);
        Save();
        LeaveSetup();
    }

    static string CleanName(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s.Trim())
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == ' ') sb.Append(c);
        string r = sb.ToString().Trim();
        return r.Length > 16 ? r.Substring(0, 16).Trim() : r;
    }

    // ---------- score codes ----------
    // AIM1.<base64url payload>.<checksum>; the leaderboard page checks the checksum the same way.
    string MakeCode(string modeLetter, int d, string score)
    {
        var ci = CultureInfo.InvariantCulture;
        long unix = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        string payload = string.Join("|", new[] { "v1", playerName, game.Id, modeLetter, d.ToString(ci), score,
            game.Round(Sens(game)).ToString(ci), ((int)Dpi(game)).ToString(ci), unix.ToString(ci) });
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "AIM1." + b64 + "." + Fnv(CodeSecret + payload).ToString("x8");
    }

    static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s)) { h ^= b; h = unchecked(h * 16777619); }
        return h;
    }

    void CopyCode()
    {
        try { Clipboard.SetText(scoreCode); copyNote = "Copied! Paste it on the leaderboard's Submit tab."; }
        catch { copyNote = "Couldn't copy. Try again."; }
    }

    static void OpenLeaderboard()
    {
        try { Process.Start(LeaderboardUrl); } catch { }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (scr == Scr.Setup)
        {
            if (e.KeyCode == Keys.Enter) { FinishSetup(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape && playerName != "") { LeaveSetup(); e.Handled = e.SuppressKeyPress = true; }
            return;
        }
        bool inBox = sensBox.Focused || dpiBox.Focused;
        if (e.KeyCode == Keys.Escape)
        {
            if (scr == Scr.Play) AbortPlay();
            else if (scr == Scr.Results || scr == Scr.Loadout) GoGame(game);
            else if (scr == Scr.Game) GoHome();
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.G && !inBox && scr == Scr.Game && (mode == FLK || mode == BOT)) { showGun = !showGun; Save(); Invalidate(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Enter && inBox) { ActiveControl = null; e.Handled = e.SuppressKeyPress = true; }
        else if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && !inBox)
        {
            if (scr == Scr.Game || scr == Scr.Results) StartPlay(mode);
            else if (scr == Scr.Play && playMode == RCT) ReactionClick(sw.Elapsed.TotalSeconds);
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    void OnDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (scr == Scr.Play)
        {
            double now = sw.Elapsed.TotalSeconds;
            if (playMode == RCT) ReactionClick(now);
            else if (playMode == FLK && countdown <= 0) FlickClick(now);
            else if (playMode == BOT && countdown <= 0) BotClick(now);
            return;
        }
        ActiveControl = null;
        for (int i = btns.Count - 1; i >= 0; i--)
            if (btns[i].R.Contains(e.Location)) { btns[i].Click(); Invalidate(); return; }
    }

    // ---------- mouse lock ----------
    void LockMouse()
    {
        if (locked) return;
        Cursor.Position = PointToScreen(new Point(ClientSize.Width / 2, ClientSize.Height / 2));
        Cursor.Clip = RectangleToScreen(ClientRectangle);
        Cursor.Hide();
        locked = true;
    }

    void UnlockMouse()
    {
        if (!locked) return;
        Cursor.Clip = Rectangle.Empty;
        Cursor.Show();
        locked = false;
    }

    double PxPerCount(double m)
    {
        double f = (ClientSize.Width / 2.0) / Math.Tan(game.HFov / 2 * Math.PI / 180);
        return game.DegPerCount(Sens(game)) * m * f * Math.PI / 180;
    }

    // ---------- round setup ----------
    void StartPlay(string m)
    {
        if (game == null) return;
        playMode = m;
        playDiff = m == FND ? 1 : diff;
        scr = Scr.Play;
        ShowBoxes(false);
        Focus();
        float W = ClientSize.Width, H = ClientSize.Height;
        float ah = H * (float)(0.25 + 0.5 * game.Band);
        arena = new RectangleF(W * 0.12f, (H - ah) / 2, W * 0.76f, ah);
        if (m == BOT) arena = new RectangleF(W * 0.08f, H * 0.2f, W * 0.84f, H * 0.68f);
        cx = W / 2; cy = H / 2;
        mult = 1;
        hits = misses = expired = 0;
        hitTimes.Clear(); progress.Clear(); rTimes.Clear();
        shots = botHits = headHits = kills = headKills = 0;
        ttk.Clear(); bots.Clear(); path.Clear();
        hitMarkT = 0; pathFresh = false;
        ballHp = 100; htKills = 0; htTtk.Clear(); htSpawnT = 0;
        fx.Clear(); floats.Clear(); feed.Clear();
        combo = bestCombo = 0; comboPop = shakeT = flashT = 0;
        recoil = 0; muzzleT = 0;
        if (m == BOT) for (int i = 0; i < BotCount[playDiff]; i++) SpawnBot(0);
        ResetTrackStats();
        rawDx = rawDy = 0;
        sugSens = double.NaN; sugApplied = false;

        if (m == FND)
        {
            fMults = new[] { 0.8, 0.9, 1.0, 1.1, 1.2 }.OrderBy(x => rnd.Next()).ToArray();
            fScores = new double[5];
            fIdx = 0;
            mult = fMults[0];
        }
        pxPerCount = PxPerCount(mult);
        roundLen = timeLeft = m == FND ? 10 : 30;

        if (m == RCT) { rState = 0; countdown = 0; UnlockMouse(); }
        else { countdown = 3; LockMouse(); ResetBall(); }
        last = sw.Elapsed.TotalSeconds;
        Invalidate();
    }

    void ResetTrackStats() { onTime = totTime = leadSum = leadTime = crossPath = ballPath = 0; }

    void ResetBall()
    {
        float H = ClientSize.Height;
        br = BallR[playDiff] * H * game.SizeMul;
        ballSpeed = BallSpeed[playDiff] * H;
        bx = arena.X + arena.Width / 2;
        by = arena.Y + arena.Height / 2;
        dirX = rnd.Next(2) == 0 ? -1 : 1; dirY = 0;
        heading = Math.Atan2(dirY, dirX);
        turnTimer = changeTimer = 0.6;
        speedMul = 1;
        jumpOff = jumpV = 0;
        jumpTimer = 1.5;
        bvx = bvy = 0;
    }

    void AbortPlay()
    {
        UnlockMouse();
        GoGame(game);
    }

    // ---------- main loop ----------
    void OnIdle(object sender, EventArgs e)
    {
        NativeMsg msg;
        while (scr == Scr.Play && !PeekMessage(out msg, IntPtr.Zero, 0, 0, 0)) Frame();
    }

    void Frame()
    {
        double now = sw.Elapsed.TotalSeconds;
        double dt = Math.Min(0.05, now - last);
        last = now;

        int dx = 0, dy = 0;
        if (locked)
        {
            if (useRaw) { dx = rawDx; dy = rawDy; }
            else
            {
                Point c = PointToScreen(new Point(ClientSize.Width / 2, ClientSize.Height / 2)), p = Cursor.Position;
                dx = p.X - c.X; dy = p.Y - c.Y;
                Cursor.Position = c;
            }
        }
        rawDx = rawDy = 0;

        double moved = 0;
        if (playMode != RCT)
        {
            float ox = cx, oy = cy;
            double am = AccelMult(dx, dy, dt);
            cx = Math.Max(0, Math.Min(ClientSize.Width, cx + (float)(dx * pxPerCount * am)));
            cy = Math.Max(0, Math.Min(ClientSize.Height, cy + (float)(dy * pxPerCount * am)));
            moved = Math.Sqrt((cx - ox) * (cx - ox) + (cy - oy) * (cy - oy));
        }

        StepFx(dt, dx);
        if (countdown > 0)
        {
            countdown -= dt;
            if (countdown <= 0)
            {
                countdown = 0;
                if (playMode == FLK) SpawnTarget(now);
                if (playMode == BOT) { foreach (var b in bots) b.SpawnT = now; StartPath(); }
                if (playMode == HTK) htSpawnT = now;
            }
        }
        else if (dt > 0) Step(dt, now, moved);

        if (scr == Scr.Play) { Invalidate(); Update(); }
        if (markGreen) { greenShown = sw.Elapsed.TotalSeconds; markGreen = false; }
        if (sw.Elapsed.TotalSeconds - now < 0.002) Thread.Sleep(1);
    }

    void Step(double dt, double now, double moved)
    {
        if (playMode == RCT)
        {
            if (rState == 1 && now >= greenAt) { rState = 2; markGreen = true; }
            return;
        }
        if (playMode == FLK) StepFlick(dt, now, moved);
        else if (playMode == BOT) StepBots(dt, now, moved);
        else if (playMode == HTK) StepHealth(dt, now, moved);
        else StepTracking(dt, moved);

        timeLeft -= dt;
        if (timeLeft <= 0) RoundOver();
    }

    // ---------- health track ----------
    void StepHealth(double dt, double now, double moved)
    {
        StepTracking(dt, moved);
        double by2 = by + jumpOff;
        if (!Hit(cx - bx, cy - by2, br)) return;
        ballHp -= DrainRate[playDiff] * dt;
        if (rnd.NextDouble() < dt * 30) Burst(bx + (rnd.NextDouble() - 0.5) * br, by2 + (rnd.NextDouble() - 0.5) * br, game.Accent, 1, 120, 0.25);
        if (ballHp > 0) return;
        htKills++;
        htTtk.Add(now - htSpawnT);
        Burst(bx, by2, game.Accent, 26, 420, 0.6);
        Burst(bx, by2, Color.White, 8, 260, 0.35);
        AddFeed("Target down" + (now - htSpawnT < 1.2 ? "  ·  fast!" : ""), false);
        Shake(0.12); Sound(2);
        combo++; comboPop = 1; if (combo > bestCombo) bestCombo = combo;
        // a fresh ball somewhere else in the arena
        ballHp = 100; htSpawnT = now;
        bx = arena.Left + br + rnd.NextDouble() * (arena.Width - 2 * br);
        by = arena.Top + br + (game.Jumps ? JumpH() : 0) + rnd.NextDouble() * Math.Max(1, arena.Height - 2 * br - (game.Jumps ? JumpH() : 0));
        jumpOff = jumpV = 0;
        double a = rnd.NextDouble() * Math.PI * 2;
        dirX = Math.Cos(a); dirY = Math.Sin(a); heading = a;
    }

    // ---------- effects ----------
    void Burst(double x, double y, Color c, int n, double speed, double life)
    {
        for (int i = 0; i < n; i++)
        {
            double ang = rnd.NextDouble() * Math.PI * 2, sp = speed * (0.3 + rnd.NextDouble() * 0.7) * S;
            fx.Add(new Particle { X = x, Y = y, VX = Math.Cos(ang) * sp, VY = Math.Sin(ang) * sp - speed * 0.3 * S, Life = life, MaxLife = life, Size = (2 + rnd.NextDouble() * 3) * S, C = c });
        }
    }
    // ---------- sounds: tiny tones made in memory (1 = hit, 2 = kill, 3 = headshot) ----------
    [DllImport("winmm.dll")] static extern bool PlaySound(IntPtr sound, IntPtr hmod, uint flags);
    const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
    static IntPtr[] tones;

    static IntPtr MakeTone(double[] freqs, double seconds, double volume)
    {
        int rate = 22050, n = (int)(rate * seconds);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, k = (double)i / n;
            double f = freqs[0] + (freqs[freqs.Length - 1] - freqs[0]) * k;   // slide between the first and last frequency
            double env = Math.Min(1, t * 400) * Math.Pow(1 - k, 2);           // quick attack, smooth fade
            w.Write((short)(Math.Sin(2 * Math.PI * f * t) * env * volume * 32767));
        }
        byte[] bytes = ms.ToArray();
        return GCHandle.Alloc(bytes, GCHandleType.Pinned).AddrOfPinnedObject();  // stays valid while the sound plays
    }

    void Sound(int kind)
    {
        if (!soundOn) return;
        try
        {
            if (tones == null) tones = new[] { MakeTone(new[] { 1500.0, 1100 }, 0.04, 0.35), MakeTone(new[] { 520.0, 980 }, 0.11, 0.45), MakeTone(new[] { 2100.0, 1700 }, 0.16, 0.4) };
            PlaySound(tones[kind - 1], IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
        catch { }
    }

    void AddFloat(double x, double y, string text, Color c) { floats.Add(new FloatText { X = x, Y = y, Text = text, C = c, Life = 0.7 }); }
    void AddFeed(string text, bool head) { feed.Insert(0, new FeedItem { Text = text, Head = head, Life = 3 }); if (feed.Count > 5) feed.RemoveAt(5); }
    void Shake(double t) { if (shakeOn) shakeT = Math.Max(shakeT, t); }
    void BreakCombo() { combo = 0; }

    void StepFx(double dt, int mouseDx)
    {
        for (int i = fx.Count - 1; i >= 0; i--)
        {
            var p = fx[i];
            p.Life -= dt;
            if (p.Life <= 0) { fx.RemoveAt(i); continue; }
            p.VY += 900 * S * dt; p.X += p.VX * dt; p.Y += p.VY * dt;
        }
        for (int i = floats.Count - 1; i >= 0; i--) { floats[i].Life -= dt; floats[i].Y -= 60 * S * dt; if (floats[i].Life <= 0) floats.RemoveAt(i); }
        for (int i = feed.Count - 1; i >= 0; i--) { feed[i].Life -= dt; if (feed[i].Life <= 0) feed.RemoveAt(i); }
        comboPop = Math.Max(0, comboPop - dt * 4);
        shakeT = Math.Max(0, shakeT - dt);
        flashT = Math.Max(0, flashT - dt);
        recoil *= Math.Pow(0.0005, dt);                     // springs back in about a quarter second
        muzzleT = Math.Max(0, muzzleT - dt);
        tracerT = Math.Max(0, tracerT - dt);
        sway += (Math.Max(-40, Math.Min(40, mouseDx)) * 0.15 - sway) * Math.Min(1, dt * 10);
    }

    bool Hit(double dx, double dy, double r)
    {
        if (game.Square) return Math.Abs(dx) <= r && Math.Abs(dy) <= r;
        return dx * dx + dy * dy <= r * r;
    }

    // ---------- tracking ----------
    void StepTracking(double dt, double moved)
    {
        UpdateBall(dt);
        double dx = cx - bx, dy = cy - (by + jumpOff);
        totTime += dt;
        if (Hit(dx, dy, br)) onTime += dt;

        double sp = Math.Sqrt(bvx * bvx + bvy * bvy), dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 3 * br && sp > 1)
        {
            leadSum += (dx * bvx + dy * bvy) / sp / br * dt;   // + ahead of the ball, - behind it
            leadTime += dt;
            crossPath += moved;
            ballPath += sp * dt;
        }
    }

    double JumpH() { return ClientSize.Height * 0.1; }

    void UpdateBall(double dt)
    {
        double H = ClientSize.Height, G = 3.0 * H;
        double vScale = 1 - 0.75 * game.HorizBias;
        if (playDiff == 0)
        {
            turnTimer -= dt;
            if (turnTimer <= 0) { turnRate = (rnd.NextDouble() * 2 - 1) * 2.4; turnTimer = 0.5 + rnd.NextDouble(); }
            heading += turnRate * dt;
            dirX = Math.Cos(heading); dirY = Math.Sin(heading);
        }
        else
        {
            changeTimer -= dt;
            if (changeTimer <= 0) NewDirection();
        }

        double spd = ballSpeed * speedMul;
        bvx = dirX * spd; bvy = dirY * spd * vScale;
        bx += bvx * dt; by += bvy * dt;

        double top = arena.Top + br + (game.Jumps ? JumpH() : 0), bottom = arena.Bottom - br;
        if (top > bottom) top = bottom;
        if (bx < arena.Left + br) { bx = arena.Left + br; dirX = Math.Abs(dirX); heading = Math.Atan2(dirY, dirX); }
        if (bx > arena.Right - br) { bx = arena.Right - br; dirX = -Math.Abs(dirX); heading = Math.Atan2(dirY, dirX); }
        if (by < top) { by = top; dirY = Math.Abs(dirY); heading = Math.Atan2(dirY, dirX); }
        if (by > bottom) { by = bottom; dirY = -Math.Abs(dirY); heading = Math.Atan2(dirY, dirX); }

        if (game.Jumps && playDiff >= 1)
        {
            jumpTimer -= dt;
            if (jumpTimer <= 0 && jumpOff == 0 && jumpV == 0) { jumpV = -Math.Sqrt(2 * G * JumpH()); jumpTimer = 1.0 + rnd.NextDouble() * 2.0; }
            if (jumpV != 0 || jumpOff < 0)
            {
                jumpV += G * dt;
                jumpOff += jumpV * dt;
                if (jumpOff >= 0) { jumpOff = 0; jumpV = 0; }
            }
        }
        bvy += jumpV;
    }

    void NewDirection()
    {
        if (rnd.NextDouble() < game.HorizBias) { dirX = dirX >= 0 ? -1 : 1; dirY = (rnd.NextDouble() * 2 - 1) * 0.5; }
        else { double a = rnd.NextDouble() * Math.PI * 2; dirX = Math.Cos(a); dirY = Math.Sin(a); }
        double n = Math.Sqrt(dirX * dirX + dirY * dirY);
        dirX /= n; dirY /= n;
        heading = Math.Atan2(dirY, dirX);
        if (playDiff == 1) { changeTimer = 0.7 + rnd.NextDouble() * 0.9; speedMul = 1; }
        else if (playDiff == 2) { changeTimer = 0.3 + rnd.NextDouble() * 0.6; speedMul = 0.85 + rnd.NextDouble() * 0.3; }
        else { changeTimer = 0.15 + rnd.NextDouble() * 0.4; speedMul = 0.6 + rnd.NextDouble() * 1.3; }
    }

    // ---------- flick ----------
    void SpawnTarget(double now)
    {
        float H = ClientSize.Height;
        tr = FlickR[playDiff] * H * game.SizeMul;
        bool ok = false;
        for (int i = 0; i < 40 && !ok; i++)
        {
            double d = (FlickMin[playDiff] + rnd.NextDouble() * (FlickMax[playDiff] - FlickMin[playDiff])) * H;
            double a = rnd.NextDouble() * Math.PI * 2;
            tx = cx + Math.Cos(a) * d; ty = cy + Math.Sin(a) * d;
            ok = tx > arena.Left + tr && tx < arena.Right - tr && ty > arena.Top + tr && ty < arena.Bottom - tr;
        }
        if (!ok)
        {
            tx = arena.Left + tr + rnd.NextDouble() * (arena.Width - 2 * tr);
            ty = arena.Top + tr + rnd.NextDouble() * (arena.Height - 2 * tr);
        }
        startX = cx; startY = cy;
        flickD = Math.Sqrt((tx - cx) * (tx - cx) + (ty - cy) * (ty - cy));
        ux = flickD > 0 ? (tx - cx) / flickD : 0; uy = flickD > 0 ? (ty - cy) / flickD : 0;
        primaryDone = flickD < 1;
        smoothSpd = peakSpd = 0;
        spawnT = now;
    }

    void StepFlick(double dt, double now, double moved)
    {
        if (!primaryDone)
        {
            // The first big movement ends when speed falls to 15% of its peak; where it stopped shows over/undershoot.
            smoothSpd += (moved / dt - smoothSpd) * Math.Min(1, dt / 0.025);
            if (smoothSpd > peakSpd) peakSpd = smoothSpd;
            double along = (cx - startX) * ux + (cy - startY) * uy;
            if (along > 0.3 * flickD && peakSpd > 0 && smoothSpd < 0.15 * peakSpd)
            {
                primaryDone = true;
                if (flickD > 3 * tr) progress.Add(along / flickD);
            }
        }
        double timeout = FlickTimeout[playDiff];
        if (timeout > 0 && now - spawnT > timeout) { expired++; BreakCombo(); SpawnTarget(now); }
    }

    void FlickClick(double now)
    {
        Fire();
        if (Hit(cx - tx, cy - ty, tr))
        {
            hits++;
            hitTimes.Add(now - spawnT);
            if (!primaryDone && flickD > 3 * tr) progress.Add(((cx - startX) * ux + (cy - startY) * uy) / flickD);
            Burst(tx, ty, game.Accent, 16, 380, 0.5);
            combo++; comboPop = 1; if (combo > bestCombo) bestCombo = combo;
            if (now - spawnT < 0.35) AddFloat(tx, ty - tr - P(10), "QUICK!", Color.FromArgb(255, 211, 78));
            Sound(1);
            SpawnTarget(now);
        }
        else { misses++; BreakCombo(); }
    }

    // ---------- bots ----------
    void SpawnBot(double now)
    {
        double H = ClientSize.Height;
        double[] scales = { 1.0, 0.75, 0.55 };   // smaller bots stand further back, higher on screen
        double sc = scales[rnd.Next(3)];
        var b = new Bot { U = BotU[playDiff] * H * sc, Hp = 3, SpawnT = now };
        b.Ground = arena.Top + arena.Height * (0.55 + 0.45 * (sc - 0.55) / 0.45);
        b.Strafe = rnd.NextDouble() < BotStrafeShare[playDiff];
        for (int i = 0; i < 25; i++)
        {
            b.X = arena.Left + b.U + rnd.NextDouble() * (arena.Width - 2 * b.U);
            bool clear = true;
            foreach (var o in bots)
                if (!o.Dead && Math.Abs(o.X - b.X) < (o.U + b.U) * 1.2 && Math.Abs(o.Ground - b.Ground) < 2.6 * Math.Max(o.U, b.U)) clear = false;
            if (clear) break;
        }
        b.Origin = b.X;
        b.Range = b.U * (2 + rnd.NextDouble() * 3);
        b.Speed = BotSpeed[playDiff] * b.U;
        b.Vx = (rnd.Next(2) == 0 ? -1 : 1) * b.Speed;
        b.DirTimer = BotDirTime();
        bots.Add(b);
    }

    double BotDirTime()
    {
        double[] lo = { 0.8, 0.5, 0.3, 0.15 }, hi = { 1.8, 1.4, 1.0, 0.6 };
        return lo[playDiff] + rnd.NextDouble() * (hi[playDiff] - lo[playDiff]);
    }

    void StepBots(double dt, double now, double moved)
    {
        for (int i = bots.Count - 1; i >= 0; i--)
        {
            var b = bots[i];
            if (b.Dead)
            {
                b.DeadT -= dt;
                if (b.DeadT <= 0) { bots.RemoveAt(i); SpawnBot(now); }
                continue;
            }
            b.Flash = Math.Max(0, b.Flash - dt);
            if (!b.Strafe) continue;
            b.DirTimer -= dt;
            double nx = b.X + b.Vx * dt;
            bool hitEdge = nx < arena.Left + b.U || nx > arena.Right - b.U;
            if (b.DirTimer <= 0 || Math.Abs(nx - b.Origin) > b.Range || hitEdge)
            {
                double speed = playDiff == 3 ? b.Speed * (0.7 + rnd.NextDouble() * 0.7) : b.Speed;
                b.Vx = (b.Vx > 0 ? -1 : 1) * speed;
                b.DirTimer = BotDirTime();
                if (hitEdge) b.Origin = Math.Max(arena.Left + b.U + b.Range, Math.Min(arena.Right - b.U - b.Range, b.Origin));
            }
            else b.X = nx;
        }
        if (path.Count < 3000) path.Add(new[] { cx, cy, moved / dt });
        hitMarkT = Math.Max(0, hitMarkT - dt);
    }

    // 0 = miss, 1 = body, 2 = head
    static int BotPart(Bot b, double px, double py)
    {
        double u = b.U, bodyTop = b.Ground - 1.9 * u, headR = 0.31 * u, headCy = bodyTop - 0.08 * u - headR, r = u / 2;
        if ((px - b.X) * (px - b.X) + (py - headCy) * (py - headCy) <= headR * headR) return 2;
        if (Math.Abs(px - b.X) <= r && py >= bodyTop + r && py <= b.Ground - r) return 1;
        double dTop = (px - b.X) * (px - b.X) + (py - bodyTop - r) * (py - bodyTop - r);
        double dBot = (px - b.X) * (px - b.X) + (py - b.Ground + r) * (py - b.Ground + r);
        return dTop <= r * r || dBot <= r * r ? 1 : 0;
    }

    void BotClick(double now)
    {
        shots++;
        Fire();
        foreach (var b in bots.Where(x => !x.Dead).OrderByDescending(x => x.U))
        {
            int part = BotPart(b, cx, cy);
            if (part == 0) continue;
            botHits++;
            if (part == 2) headHits++;
            if (pathFresh) { AnalyzePath(cx, cy); pathFresh = false; }
            b.Hp -= part == 2 ? 3 : 1;
            b.Flash = 0.08;
            hitMarkT = 0.12; hitMarkHead = part == 2;
            Burst(cx, cy, part == 2 ? Color.FromArgb(255, 90, 90) : Color.FromArgb(25, 230, 232), part == 2 ? 10 : 6, 300, 0.35);
            combo++; comboPop = 1; if (combo > bestCombo) bestCombo = combo;
            if (b.Hp <= 0)
            {
                b.Dead = true; b.DeadT = 0.25;
                kills++;
                if (part == 2) headKills++;
                ttk.Add(now - b.SpawnT);
                StartPath();
                Burst(b.X, b.Ground - b.U, Color.FromArgb(25, 230, 232), 30, 480, 0.7);
                AddFeed(part == 2 ? "Headshot kill" : "Kill", part == 2);
                Shake(part == 2 ? 0.16 : 0.1);
                if (part == 2) { AddFloat(cx, cy - P(26), "HEADSHOT", Color.FromArgb(255, 70, 85)); flashT = 0.08; Sound(3); }
                else Sound(2);
            }
            else Sound(1);
            return;
        }
        BreakCombo();
    }

    // The gun kicks back and flashes on every shot.
    void Fire()
    {
        recoil = Math.Min(1.4, recoil + 1);
        muzzleT = 0.05;
        tracerT = 0.07; tracerX = cx; tracerY = cy;
    }

    void StartPath() { path.Clear(); pathStartX = cx; pathStartY = cy; pathFresh = true; }

    // Where did the first big mouse movement toward the next target stop? Same idea as flick mode.
    void AnalyzePath(double tx2, double ty2)
    {
        double D = Math.Sqrt((tx2 - pathStartX) * (tx2 - pathStartX) + (ty2 - pathStartY) * (ty2 - pathStartY));
        if (D < ClientSize.Height * 0.05 || path.Count < 3) return;
        double ux2 = (tx2 - pathStartX) / D, uy2 = (ty2 - pathStartY) / D;
        var smooth = new double[path.Count];
        double s = 0, peak = 0;
        for (int i = 0; i < path.Count; i++) { s += (path[i][2] - s) * 0.3; smooth[i] = s; if (s > peak) peak = s; }
        if (peak <= 0) return;
        double along = 0;
        for (int i = 0; i < path.Count; i++)
        {
            along = (path[i][0] - pathStartX) * ux2 + (path[i][1] - pathStartY) * uy2;
            if (along > 0.3 * D && smooth[i] < 0.15 * peak) { progress.Add(along / D); return; }
        }
        progress.Add(along / D);
    }

    // ---------- reaction ----------
    void ReactionClick(double now)
    {
        if (rState == 2)
        {
            lastReaction = (now - greenShown) * 1000;
            rTimes.Add(lastReaction);
            rState = 3;
            if (rTimes.Count >= 5) FinishReaction();
        }
        else if (rState == 1) rState = 4;
        else { rState = 1; greenAt = now + 1.2 + rnd.NextDouble() * 3.0; }
        Invalidate();
    }

    // ---------- results ----------
    void RoundOver()
    {
        if (playMode == FND)
        {
            fScores[fIdx] = totTime > 0 ? onTime / totTime * 100 : 0;
            fIdx++;
            if (fIdx < 5)
            {
                mult = fMults[fIdx];
                pxPerCount = PxPerCount(mult);
                ResetBall();
                ResetTrackStats();
                timeLeft = roundLen;
                countdown = 2;
                return;
            }
            FinishFinder();
        }
        else if (playMode == FLK) FinishFlick();
        else if (playMode == BOT) FinishBots();
        else if (playMode == HTK) FinishHealth();
        else FinishTracking();
    }

    void ShowResults()
    {
        copyNote = "";
        UnlockMouse();
        // ranks and skin unlocks
        newUnlocks.Clear();
        var before = Skins.Where(Unlocked).Select(s => s.Id).ToList();
        if (resRank > bestRankEver) bestRankEver = resRank;
        if (playMode == BOT && kills > bestBotKills) bestBotKills = kills;
        foreach (var s in Skins) if (Unlocked(s) && !before.Contains(s.Id)) newUnlocks.Add(s.Name);
        Save();
        resAnimStart = sw.Elapsed.TotalSeconds;
        scr = Scr.Results;
        Invalidate();
    }

    // ---------- health bar over the Health Track ball ----------
    void DrawHealthBar(Graphics g, double x, double y, double w, double frac, bool draining)
    {
        float bw = (float)w, bh = Math.Max(P(5), (float)w * 0.08f), bx2 = (float)(x - w / 2), by2 = (float)y;
        FillRound(g, Color.FromArgb(170, 0, 0, 0), new RectangleF(bx2 - P(2), by2 - P(2), bw + P(4), bh + P(4)), P(4));
        Color c = frac > 0.6 ? Color.FromArgb(61, 220, 132) : frac > 0.3 ? Color.FromArgb(245, 184, 65) : Color.FromArgb(255, 80, 80);
        if (draining) c = Blend(c, Color.White, 0.25 + 0.25 * Math.Sin(sw.Elapsed.TotalSeconds * 30));
        if (frac > 0) FillRound(g, c, new RectangleF(bx2, by2, bw * (float)Math.Min(1, frac), bh), P(3));
    }

    // ---------- ranks ----------
    static readonly string[] RankNames = { "Iron", "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Elite", "Champion" };
    static readonly Color[] RankColors = {
        Color.FromArgb(130, 132, 140), Color.FromArgb(196, 130, 72), Color.FromArgb(196, 206, 220), Color.FromArgb(244, 196, 58),
        Color.FromArgb(56, 210, 196), Color.FromArgb(110, 170, 255), Color.FromArgb(200, 96, 255), Color.FromArgb(255, 76, 92) };

    // Score needed for each rank above Iron; harder difficulties need less.
    static double[] RankSteps(string m, int d, out bool lower)
    {
        double[] f = { 1.15, 1, 0.85, 0.7 };
        double k = d >= 0 && d < 4 ? f[d] : 1;
        lower = m == RCT;
        double[] t = m == TRK ? new double[] { 25, 35, 45, 55, 65, 75, 85 }
            : m == FLK ? new double[] { 10, 16, 22, 28, 35, 42, 50 }
            : m == BOT ? new double[] { 5, 9, 13, 17, 22, 27, 33 }
            : m == HTK ? new double[] { 2, 4, 6, 8, 10, 13, 16 }
            : m == RCT ? new double[] { 330, 290, 260, 235, 210, 190, 170 }
            : null;
        if (t != null && !lower) t = t.Select(x => Math.Round(x * k, m == TRK ? 0 : 0)).ToArray();
        return t;
    }
    // Your best rank in a mode at a difficulty, from your saved rounds in the current game (-1 if none yet).
    int BestRank(string m, int d)
    {
        var h = History(m, d);
        if (h.Count == 0) return -1;
        return RankIndex(m, d, m == RCT ? h.Min(r => r.Score) : h.Max(r => r.Score));
    }
    static int RankIndex(string m, int d, double score)
    {
        bool lower;
        var t = RankSteps(m, d, out lower);
        if (t == null) return -1;
        int r = 0;
        for (int i = 0; i < t.Length; i++) if (lower ? score <= t[i] : score >= t[i]) r = i + 1;
        return r;
    }

    void DrawRankBadge(Graphics g, float x, float y, float size, int rank, double reveal)
    {
        if (rank < 0) return;
        Color c = RankColors[rank];
        float s = size * (float)(0.6 + 0.4 * Math.Min(1, reveal));
        float cx2 = x + size / 2, cy2 = y + size / 2;
        // glow
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(cx2 - s, cy2 - s, s * 2, s * 2);
            using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)(110 * Math.Min(1, reveal)), c), SurroundColors = new[] { Color.FromArgb(0, c) } })
                g.FillPath(pb, gp);
        }
        // shield shape with a chevron per rank step
        var pts = new[] { new PointF(cx2, cy2 - s * 0.5f), new PointF(cx2 + s * 0.42f, cy2 - s * 0.32f), new PointF(cx2 + s * 0.36f, cy2 + s * 0.18f),
            new PointF(cx2, cy2 + s * 0.5f), new PointF(cx2 - s * 0.36f, cy2 + s * 0.18f), new PointF(cx2 - s * 0.42f, cy2 - s * 0.32f) };
        using (var b = new LinearGradientBrush(new PointF(cx2, cy2 - s / 2), new PointF(cx2, cy2 + s / 2), Blend(c, Color.White, 0.35), Blend(c, Color.Black, 0.35)))
            g.FillPolygon(b, pts);
        using (var pen = new Pen(Blend(c, Color.White, 0.6), P(2.5f))) g.DrawPolygon(pen, pts);
        int chevrons = Math.Min(3, rank / 2 + 1);
        using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), s * 0.06f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            for (int i = 0; i < chevrons; i++)
            {
                float yy = cy2 - s * 0.14f + i * s * 0.14f;
                g.DrawLines(pen, new[] { new PointF(cx2 - s * 0.18f, yy), new PointF(cx2, yy + s * 0.1f), new PointF(cx2 + s * 0.18f, yy) });
            }
    }

    // ---------- gun skins ----------
    class Skin { public string Id, Name, Need = ""; public int Rank = -1, BotKills; public Color Body, Trim, Glow; }
    static readonly Skin[] Skins = {
        new Skin { Id = "standard", Name = "Standard", Body = Color.FromArgb(64, 68, 76), Trim = Color.FromArgb(34, 36, 42), Glow = Color.FromArgb(130, 136, 146) },
        new Skin { Id = "neon", Name = "Neon Pulse", Body = Color.FromArgb(18, 18, 26), Trim = Color.FromArgb(32, 32, 44), Glow = Color.FromArgb(25, 230, 232) },
        new Skin { Id = "arctic", Name = "Arctic", Body = Color.FromArgb(222, 232, 240), Trim = Color.FromArgb(140, 160, 182), Glow = Color.FromArgb(110, 196, 255) },
        new Skin { Id = "crimson", Name = "Crimson", Body = Color.FromArgb(124, 18, 30), Trim = Color.FromArgb(58, 10, 16), Glow = Color.FromArgb(255, 70, 85), BotKills = 15, Need = "Get 15 kills in one Bots round" },
        new Skin { Id = "dragon", Name = "Dragon Fire", Body = Color.FromArgb(70, 16, 10), Trim = Color.FromArgb(34, 8, 6), Glow = Color.FromArgb(255, 140, 40), Rank = 3, Need = "Reach Gold rank in any mode" },
        new Skin { Id = "glitch", Name = "Glitch", Body = Color.FromArgb(28, 12, 44), Trim = Color.FromArgb(14, 6, 24), Glow = Color.FromArgb(255, 60, 220), Rank = 4, Need = "Reach Platinum rank in any mode" },
        new Skin { Id = "gold", Name = "Gold", Body = Color.FromArgb(206, 160, 56), Trim = Color.FromArgb(128, 94, 26), Glow = Color.FromArgb(255, 238, 170), Rank = 5, Need = "Reach Diamond rank in any mode" },
        new Skin { Id = "void", Name = "Void", Body = Color.FromArgb(8, 8, 14), Trim = Color.FromArgb(4, 4, 8), Glow = Color.FromArgb(170, 90, 255), Rank = 7, Need = "Reach Champion rank in any mode" }
    };
    bool Unlocked(Skin s) { return (s.Rank < 0 || bestRankEver >= s.Rank) && (s.BotKills == 0 || bestBotKills >= s.BotKills); }
    Skin CurrentSkin { get { var s = Skins.FirstOrDefault(x => x.Id == skinId); return s != null && Unlocked(s) ? s : Skins[0]; } }
    static PointF Pt(float x, float y) { return new PointF(x, y); }

    // In rounds the gun is a first-person viewmodel: low on the right, pointing forward into the screen and angled
    // slightly toward the crosshair. The side-on drawing below is bent onto a 3D plane with an exact perspective warp.
    const float GunL = -760, GunT = -130, GunR = 340, GunB = 380;   // the side-on drawing's area, in model units
    static readonly RectangleF GunArea = RectangleF.FromLTRB(GunL, GunT, GunR, GunB);
    PointF[] gunWarp;                 // while set, gun shapes are warped onto these 4 screen corners of GunArea
    Func<PointF, PointF> gunProjPt;   // the same warp for single points (gradient brushes)
    // The gun has real thickness: pass 1 draws each part's shaded side walls, pass 2 the flat faces on top.
    int gunPass;                      // 0 = flat (Loadout preview), 1 = side walls only, 2 = faces only
    Func<double, double, double, double[]> gun3D;      // model point + distance into the gun -> 3D point
    Func<double[], PointF> gunScreen3;                 // 3D point -> screen
    double[] gunDir, gunDown;                          // the barrel and "down" directions in 3D
    // Rough outline of the gun and arms in model units, to tell when something is hidden behind them.
    static readonly PointF[] GunHull = { new PointF(-580, -45), new PointF(-140, -70), new PointF(240, -52), new PointF(330, 300),
                                         new PointF(160, 350), new PointF(-145, 350), new PointF(-335, 45), new PointF(-580, 28) };
    double gunAlpha = 1, gunAlphaT = -1;   // the gun turns see-through while a bot or the crosshair is behind it
    Bitmap gunBmp;                         // where the gun is drawn first when it's see-through

    void DrawGun(Graphics g, float W, float H, double t)
    {
        bool pistol = gunId == "sparrow", botRound = playMode == BOT;
        float f = H * 0.9f;   // focal length: how strong the perspective is
        // In Bots the gun sits lower and further right: the nearest bots stand at the bottom of the screen.
        double x0 = (pistol ? 250 : 300) + (botRound ? 40 : 0) + sway * 2, y0 = (pistol ? 185 : 215) + (botRound ? 75 : 0) + Math.Sin(t * 1.6) * 3 + recoil * 8, z0 = 680 - recoil * 45;
        // Barrel direction: mostly straight ahead, angled a little toward the middle, nudged slightly toward the
        // crosshair (never outward, or the gun would turn edge-on) and kicked up by recoil.
        double dx = Math.Min(0.06, (cx - W / 2) / f * 0.15) - 0.05, dy = (cy - H / 2) / f * 0.12 - 0.03 - recoil * 0.07, dz = 1;
        double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz); dx /= dl; dy /= dl; dz /= dl;
        // "Down" on the gun: straight down, made perpendicular to the barrel.
        double ux = -dy * dx, uy = 1 - dy * dy, uz = -dy * dz, ul = Math.Sqrt(ux * ux + uy * uy + uz * uz); ux /= ul; uy /= ul; uz /= ul;
        // "Into the gun" (away from the camera): perpendicular to both.
        double sx = uy * dz - uz * dy, sy = uz * dx - ux * dz, sz = ux * dy - uy * dx;
        if (sx < 0) { sx = -sx; sy = -sy; sz = -sz; }
        Func<double, double, PointF> proj = (lx, ly) =>
        {
            double X = x0 - lx * dx + ly * ux, Y = y0 - lx * dy + ly * uy, Z = z0 - lx * dz + ly * uz;
            return new PointF(W / 2 + (float)(f * X / Z), H / 2 + (float)(f * Y / Z));
        };

        if (tracerT > 0)
        {
            var m = proj(pistol ? -385 : gunId == "kestrel" ? -600 : -570, -3);
            using (var pen = new Pen(Color.FromArgb((int)(220 * tracerT / 0.07), 255, 228, 150), Math.Max(2, H / 300)))
                g.DrawLine(pen, m.X, m.Y, tracerX, tracerY);
        }

        // Fade toward see-through while the crosshair or a living bot is behind the gun, and back when it's clear.
        bool hidden = false;
        var hullPts = GunHull.Select(p => proj(p.X, p.Y)).ToArray();
        using (var hull = new GraphicsPath())
        {
            hull.AddPolygon(hullPts);
            hidden = hull.IsVisible(cx, cy);
            if (botRound)
                using (var region = new Region(hull))
                    foreach (var b in bots)
                        if (!b.Dead && region.IsVisible(new RectangleF((float)(b.X - b.U / 2), (float)(b.Ground - 2.6 * b.U), (float)b.U, (float)(2.6 * b.U)))) { hidden = true; break; }
        }
        double fdt = gunAlphaT < 0 ? 1 : Math.Max(0, Math.Min(1, t - gunAlphaT));
        gunAlphaT = t;
        gunAlpha += ((hidden ? 0.3 : 1) - gunAlpha) * Math.Min(1, fdt * 12);

        Action<Graphics> paint = gg =>
        {
            // Drawing happens in screen pixels, so carry over the current transform (screen shake).
            var screen = gg.Transform;
            var area = new[] { proj(GunL, GunT), proj(GunR, GunT), proj(GunL, GunB), proj(GunR, GunB) };
            screen.TransformPoints(area);
            var st = gg.Save();
            gg.ResetTransform();
            gunWarp = area;
            gunProjPt = p => { var a = new[] { proj(p.X, p.Y) }; screen.TransformPoints(a); return a[0]; };
            gun3D = (lx, ly, side) => new[] { x0 - lx * dx + ly * ux + side * sx, y0 - lx * dy + ly * uy + side * sy, z0 - lx * dz + ly * uz + side * sz };
            gunScreen3 = v => { var a = new[] { new PointF(W / 2 + (float)(f * v[0] / v[2]), H / 2 + (float)(f * v[1] / v[2])) }; screen.TransformPoints(a); return a[0]; };
            gunDir = new[] { dx, dy, dz }; gunDown = new[] { ux, uy, uz };
            try
            {
                gunPass = 1; DrawGunModel(gg, 0, 0, 1, 0, t, CurrentSkin, gunId, muzzleT > 0, true);
                gunPass = 2; DrawGunModel(gg, 0, 0, 1, 0, t, CurrentSkin, gunId, muzzleT > 0, true);
            }
            finally { gunPass = 0; gunWarp = null; gunProjPt = null; gg.Restore(st); screen.Dispose(); }
        };
        if (gunAlpha > 0.98) { paint(g); return; }

        // See-through: draw the gun onto a spare bitmap, then copy just its corner of the screen over at partial opacity.
        int bw = (int)W, bh = (int)H;
        if (gunBmp == null || gunBmp.Width != bw || gunBmp.Height != bh)
        {
            if (gunBmp != null) gunBmp.Dispose();
            gunBmp = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        }
        var box = Rectangle.Intersect(new Rectangle(0, 0, bw, bh), Rectangle.Round(RectangleF.FromLTRB(
            hullPts.Min(p => p.X) - 60, hullPts.Min(p => p.Y) - 60, W, H)));
        if (box.Width <= 0 || box.Height <= 0) return;
        using (var gg = Graphics.FromImage(gunBmp))
        {
            gg.SetClip(box); gg.Clear(Color.Transparent);
            gg.SmoothingMode = g.SmoothingMode; gg.PixelOffsetMode = g.PixelOffsetMode;
            using (var m = g.Transform) gg.Transform = m;
            paint(gg);
        }
        using (var ia = new System.Drawing.Imaging.ImageAttributes())
        {
            ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)gunAlpha });
            var st = g.Save();
            g.ResetTransform();
            g.DrawImage(gunBmp, box, box.X, box.Y, box.Width, box.Height, GraphicsUnit.Pixel, ia);
            g.Restore(st);
        }
    }

    // ----- shape helpers: draw flat normally, or warped onto the 3D plane while gunWarp is set -----
    void ToScreen(Graphics g, GraphicsPath p)
    {
        using (var m = g.Transform) p.Transform(m);
        p.Warp(gunWarp, GunArea, null, WarpMode.Perspective, 0.2f);
    }
    // depth > 0 makes the shape a solid that thick (in pass 1 its side walls are drawn).
    void GFill(Graphics g, Brush b, GraphicsPath p, float depth = 0)
    {
        using (p)
        {
            if (gunPass == 1) { var sb = b as SolidBrush; if (depth > 0 && sb != null) DrawWalls(g, sb.Color, p, depth); return; }
            if (gunWarp == null) { g.FillPath(b, p); return; }
            ToScreen(g, p);
            var m = g.Transform; g.ResetTransform(); g.FillPath(b, p); g.Transform = m; m.Dispose();
        }
    }
    void GStroke(Graphics g, Pen pen, GraphicsPath p)
    {
        if (gunPass == 1) { p.Dispose(); return; }
        if (gunWarp == null) { using (p) g.DrawPath(pen, p); return; }
        p.Widen(pen);
        using (var b = new SolidBrush(pen.Color)) GFill(g, b, p);
    }
    void GClip(Graphics g, GraphicsPath body)
    {
        if (gunPass == 1) return;
        using (var p = (GraphicsPath)body.Clone())
        {
            if (gunWarp == null) { g.SetClip(p, CombineMode.Intersect); return; }
            ToScreen(g, p);
            var m = g.Transform; g.ResetTransform(); g.SetClip(p, CombineMode.Intersect); g.Transform = m; m.Dispose();
        }
    }
    // The side walls of a solid: each edge of the shape is extruded into the gun. Only walls facing the camera are drawn,
    // farthest first, lit from above so top surfaces are bright and undersides dark.
    void DrawWalls(Graphics g, Color c, GraphicsPath p, float depth)
    {
        using (var m = g.Transform) p.Transform(m);
        p.Flatten(null, 1f);
        var pts = p.PathPoints; var types = p.PathTypes;
        var walls = new List<KeyValuePair<double, KeyValuePair<PointF[], Color>>>();
        double[] D = gunDir, U = gunDown, L = { 0.35, -0.85, -0.4 };
        for (int s0 = 0, i = 1; i <= pts.Length; i++)
        {
            if (i < pts.Length && (types[i] & 7) != 0) continue;   // still inside this figure
            int n = i - s0;
            if (n >= 3)
            {
                float mx = 0, my = 0;
                for (int j = s0; j < i; j++) { mx += pts[j].X; my += pts[j].Y; }
                mx /= n; my /= n;
                for (int j = 0; j < n; j++)
                {
                    PointF a = pts[s0 + j], b = pts[s0 + (j + 1) % n];
                    double ex = b.X - a.X, ey = b.Y - a.Y, len = Math.Sqrt(ex * ex + ey * ey);
                    if (len < 0.01) continue;
                    double nx = ey / len, ny = -ex / len, cxm = (a.X + b.X) / 2, cym = (a.Y + b.Y) / 2;
                    if (nx * (cxm - mx) + ny * (cym - my) < 0) { nx = -nx; ny = -ny; }   // point outward
                    double[] N = { -nx * D[0] + ny * U[0], -nx * D[1] + ny * U[1], -nx * D[2] + ny * U[2] };
                    var M = gun3D(cxm, cym, depth / 2);
                    if (N[0] * M[0] + N[1] * M[1] + N[2] * M[2] >= 0) continue;          // faces away from the camera
                    double lit = 0.42 + 0.58 * Math.Max(0, N[0] * L[0] + N[1] * L[1] + N[2] * L[2]);
                    var col = Color.FromArgb(c.A, (int)Math.Min(255, c.R * lit + 6), (int)Math.Min(255, c.G * lit + 6), (int)Math.Min(255, c.B * lit + 6));
                    var quad = new[] { gunScreen3(gun3D(a.X, a.Y, 0)), gunScreen3(gun3D(b.X, b.Y, 0)), gunScreen3(gun3D(b.X, b.Y, depth)), gunScreen3(gun3D(a.X, a.Y, depth)) };
                    walls.Add(new KeyValuePair<double, KeyValuePair<PointF[], Color>>(M[0] * M[0] + M[1] * M[1] + M[2] * M[2], new KeyValuePair<PointF[], Color>(quad, col)));
                }
            }
            s0 = i;
        }
        walls.Sort((x, y) => y.Key.CompareTo(x.Key));
        var keep = g.Transform; g.ResetTransform();
        foreach (var w in walls)
            using (var br = new SolidBrush(w.Value.Value)) g.FillPolygon(br, w.Value.Key);
        g.Transform = keep; keep.Dispose();
    }

    // Gradient end points: brushes are used in screen space while warping.
    PointF GPt(Graphics g, float x, float y)
    {
        if (gunWarp == null) return Pt(x, y);
        var a = new[] { Pt(x, y) };
        using (var m = g.Transform) m.TransformPoints(a);
        return gunProjPt(a[0]);
    }
    static GraphicsPath PPoly(params PointF[] pts) { var p = new GraphicsPath(); p.AddPolygon(pts); return p; }
    static GraphicsPath PRect(float l, float t, float r, float b) { var p = new GraphicsPath(); p.AddRectangle(RectangleF.FromLTRB(l, t, r, b)); return p; }
    static GraphicsPath PEll(float x, float y, float w, float h) { var p = new GraphicsPath(); p.AddEllipse(x, y, w, h); return p; }
    static GraphicsPath PLine(float x1, float y1, float x2, float y2) { var p = new GraphicsPath(); p.AddLine(x1, y1, x2, y2); return p; }

    static string GunName(string id) { return id == "kestrel" ? "Kestrel" : id == "sparrow" ? "Sparrow" : "Warden"; }

    // Draws one of the three original guns, pointing left, with the skin's colors and animated pattern.
    void DrawGunModel(Graphics g, float px, float py, float k, float angle, double t, Skin s, string gun, bool flash, bool hands)
    {
        var st = g.Save();
        g.TranslateTransform(px, py); g.RotateTransform(angle); g.ScaleTransform(k, k);
        if (gun == "sparrow") { DrawPistol(g, t, s, flash, hands); g.Restore(st); return; }
        bool kes = gun == "kestrel";
        float muzzleX = kes ? -570 : -540;

        if (hands)
            using (var sleeve = new SolidBrush(Color.FromArgb(44, 48, 58)))
            {
                GFill(g, sleeve, PPoly(Pt(-330, 40), Pt(-268, 36), Pt(-140, 340), Pt(-280, 340)), 44);   // left forearm
                GFill(g, sleeve, PPoly(Pt(40, 90), Pt(110, 70), Pt(320, 300), Pt(160, 340)), 44);        // right forearm
            }

        using (var trim = new SolidBrush(Blend(s.Trim, Color.FromArgb(150, 156, 168), 0.22)))
        {
            // magazine and grip sit behind the body
            GFill(g, trim, kes ? PPoly(Pt(-146, 38), Pt(-92, 38), Pt(-92, 152), Pt(-146, 152))
                               : PPoly(Pt(-152, 44), Pt(-96, 44), Pt(-74, 140), Pt(-136, 154)), 22);
            GFill(g, trim, PPoly(Pt(14, 38), Pt(58, 38), Pt(82, 126), Pt(34, 132)), 26);
            if (kes) GFill(g, trim, Round(RectangleF.FromLTRB(-575, -24, -330, 22), 18), 30);   // suppressor
            else
            {
                GFill(g, trim, PRect(-505, -12, -380, 6), 14);                // barrel
                GFill(g, trim, PRect(-545, -19, -500, 13), 22);               // muzzle brake
                GFill(g, trim, PRect(-472, -38, -462, -12), 6);              // front sight
            }
            GFill(g, trim, PRect(-132, -64, 12, -42), 26);                    // top rail
        }

        using (var body = new GraphicsPath { FillMode = FillMode.Winding })
        {
            body.AddPolygon(new[] { Pt(60, -34), Pt(236, -48), Pt(258, 46), Pt(70, 34) });                       // stock
            body.AddPolygon(kes ? new[] { Pt(-214, -36), Pt(72, -40), Pt(82, 38), Pt(-204, 40) }
                                : new[] { Pt(-204, -44), Pt(72, -44), Pt(84, 40), Pt(-192, 46) });                // receiver
            body.AddRectangle(kes ? RectangleF.FromLTRB(-334, -28, -204, 26) : RectangleF.FromLTRB(-388, -32, -196, 30)); // handguard
            BodyFinish(g, body, s, t, -50, 50, -600, 300, 36);
        }
        // vents on the handguard
        using (var vent = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            for (int i = 0; i < 5; i++) { float vx = (kes ? -320 : -370) + i * 30; GFill(g, vent, PRect(vx, -10, vx + 16, -4)); }

        if (hands)
            using (var glove = new SolidBrush(Color.FromArgb(28, 30, 36)))
            {
                GFill(g, glove, PEll(-352, -4, 110, 62), 34);   // left hand on the handguard
                GFill(g, glove, PEll(10, 50, 86, 96), 34);      // right hand on the grip
            }

        if (flash) DrawMuzzleFlash(g, muzzleX);
        g.Restore(st);
    }

    // Fills the gun body with its skin color and pattern, a top-to-bottom shine, and an outline.
    void BodyFinish(Graphics g, GraphicsPath body, Skin s, double t, float shineTop, float shineBottom, float left, float right, float depth)
    {
        using (var b = new SolidBrush(s.Body)) GFill(g, b, (GraphicsPath)body.Clone(), depth);
        var clip = g.Clip; GClip(g, body);
        DrawSkinPattern(g, s, t);
        using (var shade = new LinearGradientBrush(GPt(g, 0, shineTop), GPt(g, 0, shineBottom), Color.FromArgb(50, 255, 255, 255), Color.FromArgb(80, 0, 0, 0)))
            GFill(g, shade, PRect(left, shineTop - 10, right, shineBottom + 10));
        g.Clip = clip;
        using (var pen = new Pen(Blend(s.Trim, Color.Black, 0.3), 3)) GStroke(g, pen, (GraphicsPath)body.Clone());
    }

    // The Sparrow pistol, drawn in the same coordinate space as the rifles.
    void DrawPistol(Graphics g, double t, Skin s, bool flash, bool hands)
    {
        g.ScaleTransform(1.2f, 1.2f);
        var trimC = Blend(s.Trim, Color.FromArgb(150, 156, 168), 0.22);
        if (hands)
            using (var sleeve = new SolidBrush(Color.FromArgb(44, 48, 58)))
                GFill(g, sleeve, PPoly(Pt(-10, 110), Pt(70, 96), Pt(270, 300), Pt(120, 320)), 40);   // forearm
        using (var trim = new SolidBrush(trimC))
        {
            GFill(g, trim, PPoly(Pt(-40, 14), Pt(14, 14), Pt(40, 124), Pt(-12, 132)), 24);          // grip
            var guard = new GraphicsPath(); guard.AddArc(-112, -2, 72, 52, 0, 180);
            using (var pen = new Pen(trimC, 8)) GStroke(g, pen, guard);                          // trigger guard
            GFill(g, trim, PRect(-86, 18, -78, 36), 4);                                              // trigger
            GFill(g, trim, PRect(-300, -34, -288, -22), 12);                                          // barrel tip
            GFill(g, trim, PRect(-284, -62, -274, -48), 6);                                          // front sight
            GFill(g, trim, PRect(10, -62, 30, -50), 10);                                              // rear sight
        }
        using (var body = new GraphicsPath { FillMode = FillMode.Winding })
        {
            body.AddPolygon(new[] { Pt(-292, -50), Pt(34, -50), Pt(42, -10), Pt(-292, -10) });      // slide
            body.AddPolygon(new[] { Pt(-262, -10), Pt(24, -10), Pt(18, 18), Pt(-254, 18) });        // frame
            BodyFinish(g, body, s, t, -54, 22, -320, 80, 26);
        }
        using (var dark = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
        {
            for (int i = 0; i < 5; i++) GFill(g, dark, PRect(-14 + i * 10, -44, -10 + i * 10, -16));   // slide serrations
            GFill(g, dark, PRect(-150, -46, -90, -38));                                                 // ejection port
        }
        if (hands)
            using (var glove = new SolidBrush(Color.FromArgb(28, 30, 36)))
            {
                GFill(g, glove, PEll(-70, 36, 80, 68), 30);     // support hand
                GFill(g, glove, PEll(-30, 36, 84, 92), 34);     // shooting hand on the grip
            }
        if (flash) DrawMuzzleFlash(g, -292);
    }

    void DrawMuzzleFlash(Graphics g, float muzzleX)
    {
        var star = new List<PointF>();
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6, r = i % 2 == 0 ? 70 : 26;
            star.Add(Pt(muzzleX - 30 + (float)(Math.Cos(a) * r * 1.4), -3 + (float)(Math.Sin(a) * r)));
        }
        using (var b = new SolidBrush(Color.FromArgb(230, 255, 210, 110))) GFill(g, b, PPoly(star.ToArray()));
        using (var b = new SolidBrush(Color.FromArgb(240, 255, 255, 230))) GFill(g, b, PEll(muzzleX - 52, -24, 44, 42));
    }

    void DrawSkinPattern(Graphics g, Skin s, double t)
    {
        switch (s.Id)
        {
            case "neon":
                {
                    int a = (int)(140 + 100 * Math.Sin(t * 4));
                    using (var pen = new Pen(Color.FromArgb(a, s.Glow), 4))
                        foreach (int y in new[] { -22, 2, 24 }) GStroke(g, pen, PLine(-400, y, 250, y - 6));
                    break;
                }
            case "arctic":
            case "crimson":
                using (var pen = new Pen(Color.FromArgb(s.Id == "arctic" ? 110 : 170, s.Id == "arctic" ? s.Trim : s.Glow), 14))
                    for (int x = -420; x < 300; x += 70) GStroke(g, pen, PLine(x, -60, x + 60, 60));
                break;
            case "dragon":
                {
                    float off = (float)(t * 140 % 200);
                    using (var b = new LinearGradientBrush(GPt(g, -400 + off, 0), GPt(g, -300 + off, -40), Color.FromArgb(220, s.Glow), Color.FromArgb(0, 255, 40, 0)) { WrapMode = WrapMode.TileFlipXY })
                        GFill(g, b, PRect(-420, -60, 280, 60));
                    break;
                }
            case "glitch":
                {
                    var r = new Random((int)(t * 12));
                    for (int i = 0; i < 4; i++)
                        using (var b = new SolidBrush(Color.FromArgb(150, i % 2 == 0 ? s.Glow : Color.FromArgb(40, 230, 255))))
                        {
                            float x = -420 + r.Next(500), y = -50 + r.Next(90);
                            GFill(g, b, PRect(x, y, x + 60 + r.Next(160), y + 4 + r.Next(10)));
                        }
                    break;
                }
            case "gold":
                {
                    float x = (float)(-700 + t * 450 % 1300);
                    using (var b = new SolidBrush(Color.FromArgb(120, 255, 255, 240)))
                        GFill(g, b, PPoly(Pt(x, -60), Pt(x + 60, -60), Pt(x + 10, 60), Pt(x - 50, 60)));
                    break;
                }
            case "void":
                for (int i = 0; i < 26; i++)
                {
                    float x = -400 + (i * 97 % 640), y = -40 + (i * 53 % 80);
                    int a = (int)(140 + 110 * Math.Sin(t * 3 + i));
                    using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, a), i % 3 == 0 ? Color.White : s.Glow))) GFill(g, b, PEll(x, y, 4, 4));
                }
                break;
            default:
                using (var pen = new Pen(Color.FromArgb(90, s.Trim), 3)) { GStroke(g, pen, PLine(-180, -20, 40, -20)); GStroke(g, pen, PLine(-180, 20, 40, 20)); }
                break;
        }
    }

    // ---------- animated menu background ----------
    // The two big glows are slow to paint (a full-screen gradient each), so they're drawn once into a bitmap
    // and copied in; only the scrolling grid is drawn fresh each frame.
    Bitmap menuBg;
    string menuBgKey = "";

    void DrawMenuBg(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height;
        double t = sw.Elapsed.TotalSeconds;
        Color c = Accent;
        string key = W + "x" + H + c.ToArgb();
        if (menuBg == null || menuBgKey != key)
        {
            if (menuBg != null) menuBg.Dispose();
            menuBg = new Bitmap(Math.Max(1, (int)W), Math.Max(1, (int)H), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            menuBgKey = key;
            using (var gb = Graphics.FromImage(menuBg))
            {
                gb.SmoothingMode = SmoothingMode.AntiAlias;
                gb.Clear(UiBg);
                foreach (var blob in new[] { new[] { 0.78, 0.12, 0.55, 0.0 }, new[] { 0.12, 0.85, 0.45, 2.0 } })
                {
                    float bx3 = W * (float)blob[0], by3 = H * (float)blob[1], r = W * (float)blob[2];
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(bx3 - r, by3 - r, r * 2, r * 2);
                        using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(26, blob[3] == 0 ? c : Color.FromArgb(255, 70, 85)), SurroundColors = new[] { Color.FromArgb(0, c) } })
                            gb.FillPath(pb, gp);
                    }
                }
            }
        }
        var mode = g.CompositingMode; g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(menuBg, 0, 0);
        g.CompositingMode = mode;
        float step = P(48), offs = (float)(t * 8 % step);
        using (var pen = new Pen(Color.FromArgb(10, 255, 255, 255), 1))
        {
            for (float x = -step + offs; x < W; x += step) g.DrawLine(pen, x, 0, x, H);
            for (float y = -step + offs; y < H; y += step) g.DrawLine(pen, 0, y, W, y);
        }
    }

    // ---------- loadout screen ----------
    void PaintLoadout(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height, m = P(40);
        double t = sw.Elapsed.TotalSeconds;
        DrawMenuBg(g);
        AddBtn(g, new RectangleF(m, m, P(100), P(38)), "‹  Back", () => GoGame(game), false, false);
        DrawT(g, "LOADOUT", fTitle, TextC, m - P(2), m + P(54));
        DrawT(g, "Pick the gun and skin you see during Flick and Bots.", fBody, Muted, m + 1, m + P(106));

        // preview
        var pv = new RectangleF(m, m + P(150), W - 2 * m, H * 0.36f);
        FillRound(g, Color.FromArgb(200, Panel), pv, P(16));
        DrawRound(g, Blend(Line, CurrentSkin.Glow, 0.4), pv, P(16), 1);
        float pk = pv.Height / 330f;
        DrawGunModel(g, pv.X + pv.Width * 0.5f + 158 * pk, pv.Y + pv.Height * 0.48f, pk,(float)Math.Sin(t * 0.8) * 2, t, CurrentSkin, gunId, (t % 2.2) < 0.06, false);
        DrawT(g, CurrentSkin.Name.ToUpper() + "  ·  " + GunName(gunId).ToUpper(), fH3, CurrentSkin.Glow == Color.Empty ? TextC : Blend(CurrentSkin.Glow, Color.White, 0.3), pv.X + P(22), pv.Y + P(18));

        // guns
        float y = pv.Bottom + P(24);
        DrawT(g, "GUN", fLabel, Muted, m, y);
        AddBtn(g, new RectangleF(m, y + P(22), P(200), P(42)), "Warden  ·  rifle", () => { gunId = "warden"; Save(); }, false, gunId == "warden");
        AddBtn(g, new RectangleF(m + P(212), y + P(22), P(220), P(42)), "Kestrel  ·  suppressed", () => { gunId = "kestrel"; Save(); }, false, gunId == "kestrel");
        AddBtn(g, new RectangleF(m + P(444), y + P(22), P(190), P(42)), "Sparrow  ·  pistol", () => { gunId = "sparrow"; Save(); }, false, gunId == "sparrow");

        // options
        float ox = m + P(674);
        DrawT(g, "OPTIONS", fLabel, Muted, ox, y);
        AddBtn(g, new RectangleF(ox, y + P(22), P(200), P(42)), "Show gun: " + (showGun ? "On" : "Off"), () => { showGun = !showGun; Save(); }, false, showGun);
        AddBtn(g, new RectangleF(ox + P(212), y + P(22), P(170), P(42)), "Sounds: " + (soundOn ? "On" : "Off"), () => { soundOn = !soundOn; Save(); if (soundOn) Sound(2); }, false, soundOn);
        AddBtn(g, new RectangleF(ox + P(394), y + P(22), P(190), P(42)), "Screen shake: " + (shakeOn ? "On" : "Off"), () => { shakeOn = !shakeOn; Save(); }, false, shakeOn);

        // skins
        y += P(88);
        DrawT(g, "SKINS", fLabel, Muted, m, y);
        if (bestRankEver >= 0) DrawT(g, "Best rank so far: " + RankNames[(int)bestRankEver], fSmall, RankColors[(int)bestRankEver], m + P(70), y - P(1));
        int cols = 4;
        float gap = P(12), tw = (W - 2 * m - (cols - 1) * gap) / cols, th = Math.Min(P(92), (H - y - P(30) - m) / 2 - gap);
        for (int i = 0; i < Skins.Length; i++)
        {
            var s = Skins[i];
            var r = new RectangleF(m + (i % cols) * (tw + gap), y + P(22) + (i / cols) * (th + gap), tw, th);
            bool open = Unlocked(s), sel = CurrentSkin.Id == s.Id, hover = r.Contains(mouse);
            FillRound(g, sel ? Blend(Panel, s.Glow, 0.22) : hover && open ? PanelHi : Panel, r, P(12));
            DrawRound(g, sel ? s.Glow : Line, r, P(12), sel ? 2 : 1);
            var sw3 = new RectangleF(r.X + P(14), r.Y + P(14), P(44), th - P(28));
            using (var b = new LinearGradientBrush(sw3, s.Body, s.Glow, 45f)) FillRoundBrush(g, b, sw3, P(8));
            DrawT(g, s.Name, fH3, open ? TextC : Muted, sw3.Right + P(12), r.Y + P(14));
            DrawTW(g, open ? (sel ? "Equipped" : "Click to equip") : "Locked · " + s.Need, fSmall, open ? (sel ? s.Glow : Muted) : Muted,
                new RectangleF(sw3.Right + P(12), r.Y + P(42), r.Right - sw3.Right - P(24), th - P(46)));
            var sid = s.Id;
            if (open) AddHit(r, () => { skinId = sid; Save(); });
        }
    }

    static void FillRoundBrush(Graphics g, Brush b, RectangleF r, float rad)
    {
        using (var p = Round(r, rad)) g.FillPath(b, p);
    }

    void Suggest(double m)
    {
        sugSens = double.NaN;
        if (m == 1) return;
        double cur = Sens(game), ns = game.Round(game.SensFor(game.DegPerCount(cur) * m));
        if (ns > 0 && ns != game.Round(cur)) sugSens = ns;
    }

    static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

    void FinishTracking()
    {
        double pct = totTime > 0 ? onTime / totTime * 100 : 0;
        var hist = History(TRK, playDiff);
        resPB = SaveRec(TRK, playDiff, pct, false);
        scoreCode = MakeCode("T", playDiff, pct.ToString("0.0", CultureInfo.InvariantCulture));
        resTitle = game.Name + "  ·  Tracking  ·  " + DiffNames[playDiff];
        resMain = pct.ToString("0.0") + "%";
        resLabel = "of the time on target";
        resLines = new List<string>();
        if (hist.Count > 0) resLines.Add("Previous best: " + hist.Max(r => r.Score).ToString("0.0") + "%");
        resLines.Add("Rounds played at this difficulty: " + (hist.Count + 1));
        TrackingCoach();
        ShowResults();
    }

    void FinishHealth()
    {
        double pct = totTime > 0 ? onTime / totTime * 100 : 0;
        var hist = History(HTK, playDiff);
        resPB = SaveRec(HTK, playDiff, htKills, false);
        scoreCode = MakeCode("H", playDiff, htKills.ToString(CultureInfo.InvariantCulture));
        resTitle = game.Name + "  ·  Health Track  ·  " + DiffNames[playDiff];
        resMain = htKills.ToString();
        resLabel = "targets tracked down in 30 seconds";
        resLines = new List<string>();
        resLines.Add(string.Format("Time on target: {0:0.0}%", pct));
        if (htTtk.Count > 0) resLines.Add(string.Format("Average time to kill: {0:0.00} s", htTtk.Average()));
        if (hist.Count > 0) resLines.Add("Previous best: " + hist.Max(r => r.Score).ToString("0") + " kills");
        TrackingCoach();
        ShowResults();
    }

    void TrackingCoach()
    {
        showCoach = true;
        coachHead = "SENSITIVITY COACH";
        double m = 1;
        if (leadTime < 3)
            coachText = "Not enough close tracking this round to judge your sensitivity. Try to stay on the ball, or drop the difficulty a level.";
        else
        {
            double ratio = ballPath > 0 ? crossPath / ballPath : 1, lead = leadSum / leadTime;
            if (ratio > 1.35)
            {
                m = 1 - Clamp((ratio - 1.2) * 0.5, 0.05, 0.2);
                coachText = string.Format("Your crosshair travelled {0:0.0}x as far as the ball did. All those small over-corrections usually mean your sensitivity is a bit high.", ratio);
            }
            else if (lead < -1.0 && ratio < 1.15)
            {
                m = 1 + Clamp((-lead - 0.8) * 0.15, 0.05, 0.2);
                coachText = string.Format("You were usually trailing about {0:0.0} ball-widths behind the ball. That usually means your sensitivity is a bit low, so it's hard to keep up when it changes direction.", -lead / 2);
            }
            else coachText = "Your tracking looks balanced: you're not over-correcting and not falling behind. Your sensitivity suits you here.";
        }
        Suggest(m);
    }

    void FinishFlick()
    {
        int shots = hits + misses;
        double acc = shots > 0 ? hits * 100.0 / shots : 0;
        var hist = History(FLK, playDiff);
        resPB = SaveRec(FLK, playDiff, hits, false);
        scoreCode = MakeCode("F", playDiff, hits.ToString(CultureInfo.InvariantCulture));
        resTitle = game.Name + "  ·  Flick  ·  " + DiffNames[playDiff];
        resMain = hits.ToString();
        resLabel = "targets hit in 30 seconds";
        resLines = new List<string>();
        resLines.Add(string.Format("Accuracy: {0:0}%  ({1} missed clicks)", acc, misses));
        if (hitTimes.Count > 0) resLines.Add(string.Format("Average time per target: {0:0} ms", hitTimes.Average() * 1000));
        if (expired > 0) resLines.Add(expired + " targets vanished before you hit them");
        if (hist.Count > 0) resLines.Add("Previous best: " + hist.Max(r => r.Score).ToString("0") + " hits");
        showCoach = true;
        coachHead = "SENSITIVITY COACH";
        FlickCoach("Not enough flicks this round to judge your sensitivity. Hit at least 6 targets.");
        ShowResults();
    }

    void FlickCoach(string notEnough)
    {
        double m = 1;
        if (progress.Count < 6) coachText = notEnough;
        else
        {
            double avg = progress.Average();
            int over = progress.Count(p => p > 1.05) * 100 / progress.Count;
            if (avg > 1.06)
            {
                m = 1 - Clamp((avg - 1) * 0.7, 0.05, 0.2);
                coachText = string.Format("Your first flick landed at {0:0}% of the distance on average, so you tend to fly past the target ({1}% of flicks). That usually means your sensitivity is a bit high.", avg * 100, over);
            }
            else if (avg < 0.86)
            {
                m = 1 + Clamp((1 - avg) * 0.6, 0.05, 0.2);
                coachText = string.Format("Your first flick landed at {0:0}% of the distance on average, so you usually stop short and need a second correction. That usually means your sensitivity is a bit low.", avg * 100);
            }
            else coachText = string.Format("Your first flick landed at {0:0}% of the distance on average, which is close to ideal. Your sensitivity looks right for flicking.", avg * 100);
        }
        Suggest(m);
    }

    void FinishBots()
    {
        var ci = CultureInfo.InvariantCulture;
        double acc = shots > 0 ? botHits * 100.0 / shots : 0, hs = botHits > 0 ? headHits * 100.0 / botHits : 0;
        var hist = History(BOT, playDiff);
        resPB = SaveRec(BOT, playDiff, kills, false);
        scoreCode = MakeCode("B", playDiff, kills.ToString(ci));
        resTitle = game.Name + "  ·  Bots  ·  " + DiffNames[playDiff];
        resMain = kills.ToString();
        resLabel = "bots eliminated in 30 seconds";
        resLines = new List<string>();
        resLines.Add(string.Format("Headshots: {0:0}% of hits  ({1} headshot kills)", hs, headKills));
        resLines.Add(string.Format("Accuracy: {0:0}%  ({1} shots)", acc, shots));
        if (ttk.Count > 0) resLines.Add(string.Format("Average time to kill: {0:0} ms", ttk.Average() * 1000));
        if (hist.Count > 0) resLines.Add("Previous best: " + hist.Max(r => r.Score).ToString("0") + " kills");
        showCoach = true;
        coachHead = "SENSITIVITY COACH";
        FlickCoach("Not enough kills this round to judge your sensitivity. Get at least 7 kills.");
        ShowResults();
    }

    void FinishReaction()
    {
        double avg = rTimes.Average(), best = rTimes.Min();
        var hist = History(RCT, 0);
        resPB = SaveRec(RCT, 0, avg, true);
        scoreCode = MakeCode("R", 0, avg.ToString("0", CultureInfo.InvariantCulture));
        resTitle = game.Name + "  ·  Reaction test";
        resMain = avg.ToString("0") + " ms";
        resLabel = "average over 5 tries";
        resLines = new List<string>();
        resLines.Add("Fastest: " + best.ToString("0") + " ms");
        resLines.Add("Tries: " + string.Join("  ·  ", rTimes.Select(t => t.ToString("0")).ToArray()) + " ms");
        if (hist.Count > 0) resLines.Add("Previous best average: " + hist.Min(r => r.Score).ToString("0") + " ms");
        showCoach = true;
        coachHead = "HOW YOU DID";
        string rating = avg < 170 ? "Outstanding. That's pro-level reaction speed."
            : avg < 210 ? "Great. Faster than most players."
            : avg < 250 ? "Good. Right around the typical gamer range."
            : avg < 300 ? "Average. Most people land around 200 to 280 ms."
            : "A bit slow this time. Being tired or a laggy screen adds time, so try again later.";
        coachText = rating + " These times include your mouse and monitor delay, so they run a little higher than in-game feel.";
        sugSens = double.NaN;
        ShowResults();
    }

    void FinishFinder()
    {
        var order = Enumerable.Range(0, 5).OrderBy(i => fMults[i]).ToArray();
        double[] ms = order.Select(i => fMults[i]).ToArray(), sc = order.Select(i => fScores[i]).ToArray();
        int k = Array.IndexOf(sc, sc.Max());
        double best = ms[k];
        if (k > 0 && k < 4)
        {
            // Fit a curve through the best score and its neighbours to land between the tested values.
            double den = sc[k + 1] - 2 * sc[k] + sc[k - 1];
            if (den < 0) best = ms[k] - 0.5 * 0.1 * (sc[k + 1] - sc[k - 1]) / den;
            best = Clamp(best, ms[k] - 0.1, ms[k] + 0.1);
        }
        fMults = ms; fScores = sc;
        double spread = sc.Max() - sc.Min();
        resTitle = game.Name + "  ·  Sens finder";
        scoreCode = "";
        resLines = new List<string>();
        resLabel = "the sensitivity you tracked best with";
        showCoach = true;
        coachHead = "SENSITIVITY COACH";
        if (spread < 3)
        {
            resMain = game.Fmt(Sens(game));
            coachText = "Your scores were within 3% of each other at every sensitivity, so your current sens is fine. Pick whatever feels most comfortable.";
            sugSens = double.NaN;
        }
        else
        {
            Suggest(best);
            resMain = double.IsNaN(sugSens) ? game.Fmt(Sens(game)) : game.Fmt(sugSens);
            coachText = string.Format("You scored {0:0}% at best and {1:0}% at worst across the five rounds.", sc.Max(), sc.Min())
                + (k == 0 || k == 4 ? " Your best round was at the edge of the range tested, so switch to this sens and run the finder again to fine-tune it." : " The chart shows how you did at each sensitivity.");
        }
        resPB = false;
        ShowResults();
    }

    void ApplySuggestion()
    {
        if (double.IsNaN(sugSens)) return;
        settings[game.Id] = new[] { sugSens, Dpi(game) };
        Save();
        sugApplied = true;
    }

    // ---------- drawing helpers ----------
    float P(float v) { return v * S; }

    static Color Blend(Color a, Color b, double t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static Color OnColor(Color c)
    {
        return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.62 ? Color.FromArgb(20, 20, 22) : Color.White;
    }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void FillRound(Graphics g, Color c, RectangleF r, float rad)
    {
        using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    static void DrawRound(Graphics g, Color c, RectangleF r, float rad, float w)
    {
        using (var p = Round(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
    }

    static void DrawT(Graphics g, string s, Font f, Color c, float x, float y)
    {
        using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
    }

    static void DrawTC(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        using (var b = new SolidBrush(c))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(s, f, b, r, sf);
    }

    static void DrawTW(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r);
    }

    Color Accent { get { return game != null ? game.Accent : Color.FromArgb(60, 196, 176); } }

    void AddHit(RectangleF r, Action a) { btns.Add(new Btn { R = r, Click = a }); }

    void AddBtn(Graphics g, RectangleF r, string text, Action a, bool primary, bool selected)
    {
        bool hover = r.Contains(mouse);
        Color fill = primary || selected ? Accent : (hover ? PanelHi : Panel);
        if ((primary || selected) && hover) fill = Blend(fill, Color.White, 0.12);
        FillRound(g, fill, r, P(8));
        if (!primary && !selected) DrawRound(g, hover ? Blend(Line, Accent, 0.6) : Line, r, P(8), 1);
        DrawTC(g, text, fBtn, primary || selected ? OnColor(fill) : TextC, r);
        AddHit(r, a);
    }

    // ---------- painting ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        btns.Clear();
        if (scr == Scr.Setup) PaintSetup(g);
        else if (scr == Scr.Home) PaintHome(g);
        else if (scr == Scr.Game) PaintGame(g);
        else if (scr == Scr.Play) PaintPlay(g);
        else if (scr == Scr.Loadout) PaintLoadout(g);
        else PaintResults(g);
        if (MusicVisible && scr == Scr.Play) PaintMusicInRound(g);
        else if (MusicVisible && scr != Scr.Setup) PaintMusicBar(g);
        if (scr == Scr.Game || scr == Scr.Results || scr == Scr.Loadout) { PaintUpdateToast(g); PaintNoticeToast(g); }
    }

    // The latest notice as a pill at the top of the other menu screens (under the update notice, if there is one).
    void PaintNoticeToast(Graphics g)
    {
        if (noticeText == "") return;
        float W = ClientSize.Width, h = P(46), y = P(14) + (updState == "available" || updState == "downloading" ? P(56) : 0);
        Color c = noticeImportant ? Color.FromArgb(255, 70, 85) : Accent;
        float w = Math.Min(W - P(40), Math.Max(P(360), g.MeasureString(noticeText, fBody).Width + P(110)));
        w = Math.Min(w, P(900));
        var r = new RectangleF(W / 2 - w / 2, y, w, h);
        AddHit(r, () => { });
        FillRound(g, Blend(Panel, c, 0.28), r, h / 2);
        DrawRound(g, c, r, h / 2, 1);
        using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
        using (var b = new SolidBrush(TextC))
            g.DrawString(noticeText, fBody, b, new RectangleF(r.X + P(22), r.Y, r.Width - P(90), r.Height), sf);
        AddBtn(g, new RectangleF(r.Right - P(64), r.Y + P(6), P(52), h - P(12)), "×", DismissNotice, false, false);
    }

    // A small notice at the top of the other menu screens (the home screen has the full banner). Never shown mid-round.
    void PaintUpdateToast(Graphics g)
    {
        if (updState != "available" && updState != "downloading") return;
        float W = ClientSize.Width, w = P(520), h = P(46);
        var r = new RectangleF(W / 2 - w / 2, P(14), w, h);
        AddHit(r, () => { });
        FillRound(g, Blend(Panel, Accent, 0.28), r, h / 2);
        DrawRound(g, Accent, r, h / 2, 1);
        if (updState == "downloading")
        {
            DrawTC(g, "Downloading v" + updVersion + "…  the app restarts by itself", fBody, TextC, r);
            return;
        }
        DrawT(g, "Update v" + updVersion + " is out", fBody, TextC, r.X + P(22), r.Y + P(12));
        AddBtn(g, new RectangleF(r.Right - P(222), r.Y + P(6), P(150), h - P(12)), "Update now", DoUpdate, true, false);
        AddBtn(g, new RectangleF(r.Right - P(64), r.Y + P(6), P(52), h - P(12)), "×", () => updState = "later", false, false);
    }

    static string Clock(double s) { int t = (int)Math.Max(0, s); return t / 60 + ":" + (t % 60).ToString("00"); }

    void PaintMusicBar(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height, m = scr == Scr.Home ? P(48) : P(40);
        float w = Math.Min(P(560), W - 2 * m), h = P(76);
        var r = new RectangleF(W - m - w, H - m - h - (scr == Scr.Home ? P(26) : 0), w, h);
        AddHit(r, () => { });
        FillRound(g, Panel, r, P(12));
        DrawRound(g, Line, r, P(12), 1);
        var n = np;
        if (n == null)
        {
            DrawT(g, "MUSIC", fLabel, Accent, r.X + P(18), r.Y + P(16));
            DrawTW(g, "Nothing playing. Start Spotify, YouTube or any music app and it shows up here.", fSmall, Muted, new RectangleF(r.X + P(18), r.Y + P(36), r.Width - P(36), P(36)));
            return;
        }
        var art = new RectangleF(r.X + P(10), r.Y + P(10), P(56), P(56));
        var img = songArt;
        if (img != null) { try { g.DrawImage(img, art); } catch { } }
        else { FillRound(g, PanelHi, art, P(6)); DrawTC(g, "MUSIC", fLabel, Muted, art); }

        float tx0 = art.Right + P(12), tw = r.Right - tx0 - P(222);
        using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
        using (var b1 = new SolidBrush(TextC)) using (var b2 = new SolidBrush(Muted))
        {
            g.DrawString(n.Title != "" ? n.Title : "Unknown song", fH3, b1, new RectangleF(tx0, r.Y + P(10), tw, P(24)), sf);
            g.DrawString((n.Artist != "" ? n.Artist + "  ·  " : "") + Clock(n.Current) + (n.Dur > 0 ? " / " + Clock(n.Dur) : ""), fSmall, b2, new RectangleF(tx0, r.Y + P(34), tw, P(20)), sf);
        }
        var bar = new RectangleF(tx0, r.Y + P(60), tw, P(4));
        FillRound(g, Line, bar, P(2));
        if (n.Dur > 0) FillRound(g, Accent, new RectangleF(bar.X, bar.Y, bar.Width * (float)(n.Current / n.Dur), bar.Height), P(2));

        float bx2 = r.Right - P(216), by3 = r.Y + P(19);
        AddIconBtn(g, new RectangleF(bx2, by3, P(38), P(38)), "prev", () => SendMusic(3), n.CanPrev);
        AddIconBtn(g, new RectangleF(bx2 + P(44), by3, P(38), P(38)), n.Playing ? "pause" : "play", () => SendMusic(1), true);
        AddIconBtn(g, new RectangleF(bx2 + P(88), by3, P(38), P(38)), "next", () => SendMusic(2), n.CanNext);
        AddBtn(g, new RectangleF(bx2 + P(136), by3, P(70), P(38)), "Lyrics", () => lyricsOpen = !lyricsOpen, false, lyricsOpen);

        if (lyricsOpen) PaintLyricsPanel(g, new RectangleF(r.X, r.Y - P(340), r.Width, P(328)), n);
    }

    void AddIconBtn(Graphics g, RectangleF r, string kind, Action a, bool enabled)
    {
        bool hover = enabled && r.Contains(mouse);
        bool main = kind == "play" || kind == "pause";
        Color fill = main ? (hover ? Blend(Accent, Color.White, 0.12) : Accent) : (hover ? PanelHi : Panel);
        using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
        if (!main) using (var pen = new Pen(Line, 1)) g.DrawEllipse(pen, r);
        Color ic = main ? OnColor(Accent) : (enabled ? TextC : Line);
        float cx2 = r.X + r.Width / 2, cy2 = r.Y + r.Height / 2, s = r.Width * 0.2f;
        using (var b = new SolidBrush(ic))
        {
            if (kind == "play") g.FillPolygon(b, new[] { new PointF(cx2 - s * 0.7f, cy2 - s), new PointF(cx2 - s * 0.7f, cy2 + s), new PointF(cx2 + s, cy2) });
            else if (kind == "pause") { g.FillRectangle(b, cx2 - s * 0.85f, cy2 - s, s * 0.6f, s * 2); g.FillRectangle(b, cx2 + s * 0.25f, cy2 - s, s * 0.6f, s * 2); }
            else
            {
                float dir = kind == "next" ? 1 : -1;
                g.FillPolygon(b, new[] { new PointF(cx2 - dir * s * 0.8f, cy2 - s), new PointF(cx2 - dir * s * 0.8f, cy2 + s), new PointF(cx2 + dir * s * 0.6f, cy2) });
                float barX = kind == "next" ? cx2 + s * 0.6f : cx2 - s * 0.6f - s * 0.35f;
                g.FillRectangle(b, barX, cy2 - s, s * 0.35f, s * 2);
            }
        }
        if (enabled) AddHit(r, a);
    }

    void PaintLyricsPanel(Graphics g, RectangleF r, NowPlaying n)
    {
        AddHit(r, () => { });
        FillRound(g, Color.FromArgb(250, Panel), r, P(12));
        DrawRound(g, Line, r, P(12), 1);
        DrawT(g, "LYRICS", fLabel, Accent, r.X + P(18), r.Y + P(16));
        AddBtn(g, new RectangleF(r.Right - P(84), r.Y + P(10), P(70), P(32)), "Close", () => lyricsOpen = false, false, false);
        AddBtn(g, new RectangleF(r.Right - P(272), r.Y + P(10), P(180), P(32)), "In rounds: " + (lyricsInRound ? "On" : "Off"),
            () => { lyricsInRound = !lyricsInRound; Save(); }, false, lyricsInRound);

        var body = new RectangleF(r.X + P(18), r.Y + P(52), r.Width - P(36), r.Height - P(80));
        var ls = lyrics;
        string st = lyricsKey == n.Key ? lyricsState : "loading";
        if (st != "ok" || ls == null)
        {
            string msg = st == "loading" ? "Looking up lyrics…" : st == "instrumental" ? "This song is instrumental." : st == "error" ? "Couldn't reach the lyrics site. Check your internet connection." : "No lyrics found for this song.";
            DrawTC(g, msg, fBody, Muted, body);
        }
        else
        {
            int idx = LyricIndex(n, ls);
            float lineH = P(34), mid = body.Y + body.Height / 2 - lineH / 2;
            for (int k = -3; k <= 3; k++)
            {
                int i = idx + k;
                if (i < 0 || i >= ls.Count) continue;
                bool cur = k == 0;
                int alpha = cur ? 255 : Math.Max(60, 200 - Math.Abs(k) * 50);
                string text = ls[i].Text != "" ? ls[i].Text : "• • •";
                DrawTC(g, text, cur ? fH3 : fBody, cur ? Color.FromArgb(alpha, Accent) : Color.FromArgb(alpha, Muted), new RectangleF(body.X, mid + k * lineH, body.Width, lineH));
            }
            if (idx < 0) DrawTC(g, "• • •", fH3, Muted, new RectangleF(body.X, mid, body.Width, lineH));
        }
        DrawT(g, (lyricsSynced || st != "ok" ? "" : "Not timed to the music  ·  ") + "Lyrics from LRCLIB", fSmall, Muted, r.X + P(18), r.Bottom - P(28));
    }

    void PaintMusicInRound(Graphics g)
    {
        var n = np;
        if (n == null) return;
        float W = ClientSize.Width, m = P(28);
        bool rct = playMode == RCT;
        Color main = rct ? Color.White : TextC, sub = rct ? Color.FromArgb(210, 255, 255, 255) : Muted;

        // Song card in the top-left corner: art, title, artist and time, thin progress bar.
        var art = new RectangleF(m, m, P(44), P(44));
        var img = songArt;
        if (img != null) { try { g.DrawImage(img, art); } catch { } }
        else FillRound(g, Color.FromArgb(120, PanelHi), art, P(6));
        float tx0 = art.Right + P(10), tw = Math.Min(P(320), W / 3);
        using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
        using (var b1 = new SolidBrush(main)) using (var b2 = new SolidBrush(sub))
        {
            g.DrawString(n.Title != "" ? n.Title : "Unknown song", fH3, b1, new RectangleF(tx0, art.Y - P(2), tw, P(22)), sf);
            g.DrawString((n.Playing ? "" : "Paused  ·  ") + (n.Artist != "" ? n.Artist + "  ·  " : "") + Clock(n.Current) + (n.Dur > 0 ? " / " + Clock(n.Dur) : ""),
                fSmall, b2, new RectangleF(tx0, art.Y + P(20), tw, P(18)), sf);
        }
        var bar = new RectangleF(tx0, art.Bottom - P(3), tw, P(3));
        FillRound(g, Color.FromArgb(90, 128, 128, 128), bar, P(1.5f));
        if (n.Dur > 0) FillRound(g, rct ? Color.White : Accent, new RectangleF(bar.X, bar.Y, bar.Width * (float)(n.Current / n.Dur), bar.Height), P(1.5f));

        // Current lyric right under it, in a rounded see-through gray bubble that only wraps the text.
        if (!lyricsInRound) return;
        var ls = lyrics;
        if (lyricsState != "ok" || ls == null || lyricsKey != n.Key) return;
        int idx = LyricIndex(n, ls);
        if (idx < 0 || ls[idx].Text == "") return;
        string text = ls[idx].Text;
        SizeF size = g.MeasureString(text, fBody);
        float padX = P(16), padY = P(7);
        float bw = Math.Min(size.Width + padX * 2, W * 0.45f), bh = size.Height + padY * 2;
        var bubble = new RectangleF(m, art.Bottom + P(12), bw, bh);
        FillRound(g, Color.FromArgb(77, 128, 128, 128), bubble, bh / 2);            // gray at 30% opacity
        DrawRound(g, Color.FromArgb(80, 255, 255, 255), bubble, bh / 2, Math.Max(1, P(1)));
        using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center })
        using (var b = new SolidBrush(Color.White))
            g.DrawString(text, fBody, b, new RectangleF(bubble.X + P(4), bubble.Y, bubble.Width - P(8), bubble.Height), sf);
    }

    void PaintSetup(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height;
        DrawMenuBg(g);
        bool first = playerName == "";
        float pw = P(540), ph = P(480);
        var r = new RectangleF((W - pw) / 2, (H - ph) / 2, pw, ph);
        FillRound(g, Panel, r, P(14));
        float x = r.X + P(32), y = r.Y, w = pw - P(64);
        DrawT(g, first ? "WELCOME TO " + AppName.ToUpper() : "YOUR PROFILE", fLabel, Accent, x, y + P(30));
        DrawT(g, first ? "Let's set you up" : "Edit your profile", fH2, TextC, x - P(2), y + P(52));
        DrawT(g, "Player name", fSmall, TextC, x, y + P(108));
        Place(nameBox, x, y + P(132), w);
        DrawTW(g, "Shown on the leaderboard next to your scores. Letters, numbers, spaces, _ and - only.", fSmall, Muted, new RectangleF(x, y + P(178), w, P(40)));
        DrawT(g, "Mouse DPI", fSmall, TextC, x, y + P(226));
        Place(setupDpiBox, x, y + P(250), P(160));
        DrawTW(g, "Find it in your mouse software (G Hub, Synapse, SteelSeries GG). Most gaming mice are 800 or 1600. You can set a different DPI for a single game later.",
            fSmall, Muted, new RectangleF(x, y + P(296), w, P(50)));
        if (setupError != "") DrawT(g, setupError, fSmall, Color.FromArgb(255, 107, 107), x, y + P(352));
        AddBtn(g, new RectangleF(x, r.Bottom - P(80), P(180), P(48)), first ? "Continue" : "Save", FinishSetup, true, false);
        if (!first) AddBtn(g, new RectangleF(x + P(196), r.Bottom - P(80), P(140), P(48)), "Cancel", LeaveSetup, false, false);
    }

    void PaintHome(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height, m = P(48);
        DrawMenuBg(g);
        DrawT(g, AppName.ToUpper(), fTitle, TextC, m, m);
        AddBtn(g, new RectangleF(m + P(400), m + P(6), P(150), P(36)), "Rate the app", () => { try { Process.Start(ReviewsUrl); } catch { } }, false, false);
        if (ownerKey != "")
        {
            AddBtn(g, new RectangleF(m + P(562), m + P(6), P(150), P(36)), "Send notice", OpenNoticeComposer, false, false);
            AddBtn(g, new RectangleF(m + P(724), m + P(6), P(120), P(36)), "Reviews", OpenReviewsWindow, false, false);
            if (noticeMsg != "") DrawT(g, noticeMsg, fSmall, Muted, m + P(858), m + P(16));
        }
        DrawT(g, "Playing as " + playerName + "  ·  " + globalDpi + " DPI.  Pick the game you want to practise for.", fBody, Muted, m + 2, m + P(56));
        AddBtn(g, new RectangleF(W - m - P(100), m, P(100), P(40)), "Exit", Close, false, false);
        AddBtn(g, new RectangleF(W - m - P(270), m, P(160), P(40)), "Leaderboard", OpenLeaderboard, false, false);
        AddBtn(g, new RectangleF(W - m - P(420), m, P(140), P(40)), "Edit profile", GoSetup, false, false);
        if (discordInvite != "")
            AddBtn(g, new RectangleF(W - m - P(870), m, P(150), P(40)), "Join Discord", () => { try { Process.Start(discordInvite); } catch { } }, true, false);
        if (DiscordAppId != "")
            AddBtn(g, new RectangleF(W - m - P(700), m, P(130), P(40)), "Discord: " + (discordOn ? "On" : "Off"),
                () => { discordOn = !discordOn; Save(); }, false, discordOn);
        if (!musicDead)
            AddBtn(g, new RectangleF(W - m - P(560), m, P(130), P(40)), "Music: " + (musicOn ? "On" : "Off"),
                () => { musicOn = !musicOn; if (!musicOn) lyricsOpen = false; Save(); }, false, musicOn);

        float bannerY = m + P(100);
        if (updState != "" && updState != "later")
        {
            var r = new RectangleF(m, bannerY, W - 2 * m, P(58));
            FillRound(g, Blend(Panel, Accent, 0.22), r, P(10));
            DrawRound(g, Accent, r, P(10), 1);
            string msg = updState == "downloading" ? "Downloading update v" + updVersion + "…  The app restarts by itself when it's done."
                : updState == "error" ? "Couldn't update automatically. Download v" + updVersion + " from the GitHub page instead."
                : "Update available: v" + updVersion + (updNotes != "" ? "  ·  " + updNotes : "");
            DrawTW(g, msg, fBody, TextC, new RectangleF(r.X + P(20), r.Y + P(17), r.Width - P(360), P(26)));
            if (updState == "available")
            {
                AddBtn(g, new RectangleF(r.Right - P(310), r.Y + P(10), P(170), P(38)), "Update now", DoUpdate, true, false);
                AddBtn(g, new RectangleF(r.Right - P(130), r.Y + P(10), P(110), P(38)), "Later", () => updState = "later", false, false);
            }
            else if (updState == "error")
                AddBtn(g, new RectangleF(r.Right - P(250), r.Y + P(10), P(230), P(38)), "Open download page", OpenReleases, false, false);
            bannerY += P(70);
        }
        if (noticeText != "")
        {
            Color c = noticeImportant ? Color.FromArgb(255, 70, 85) : Accent;
            SizeF ts = g.MeasureString(noticeText, fBody, (int)(W - 2 * m - P(260)));
            var r = new RectangleF(m, bannerY, W - 2 * m, Math.Max(P(58), ts.Height + P(30)));
            FillRound(g, Blend(Panel, c, 0.18), r, P(10));
            DrawRound(g, c, r, P(10), 1);
            DrawT(g, noticeImportant ? "IMPORTANT" : "NOTICE", fLabel, c, r.X + P(20), r.Y + P(20));
            DrawTW(g, noticeText, fBody, TextC, new RectangleF(r.X + P(120), r.Y + P(15), r.Width - P(260), r.Height - P(20)));
            AddBtn(g, new RectangleF(r.Right - P(130), r.Y + P(10), P(110), P(38)), "Got it", DismissNotice, false, false);
            bannerY += r.Height + P(12);
        }
        if (lowReviewText != "")
        {
            Color c = Color.FromArgb(255, 70, 85);
            var r = new RectangleF(m, bannerY, W - 2 * m, P(58));
            FillRound(g, Blend(Panel, c, 0.18), r, P(10));
            DrawRound(g, c, r, P(10), 1);
            DrawT(g, lowReviewCount > 1 ? lowReviewCount + " LOW REVIEWS" : "LOW REVIEW", fLabel, c, r.X + P(20), r.Y + P(20));
            using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
            using (var b = new SolidBrush(TextC))
                g.DrawString(lowReviewText, fBody, b, new RectangleF(r.X + P(170), r.Y + P(17), r.Width - P(440), P(26)), sf);
            AddBtn(g, new RectangleF(r.Right - P(250), r.Y + P(10), P(110), P(38)), "Read", OpenReviewsWindow, true, false);
            AddBtn(g, new RectangleF(r.Right - P(130), r.Y + P(10), P(110), P(38)), "Got it", DismissLowReviews, false, false);
            bannerY += P(70);
        }
        if (newsText != "")
        {
            var r = new RectangleF(m, bannerY, W - 2 * m, P(58));
            FillRound(g, Panel, r, P(10));
            DrawRound(g, Line, r, P(10), 1);
            DrawT(g, "NEWS", fLabel, Accent, r.X + P(20), r.Y + P(20));
            DrawTW(g, newsText, fBody, TextC, new RectangleF(r.X + P(80), r.Y + P(17), r.Width - P(230), P(26)));
            AddBtn(g, new RectangleF(r.Right - P(130), r.Y + P(10), P(110), P(38)), "Got it", DismissNews, false, false);
            bannerY += P(70);
        }
        DrawT(g, "v" + AppVersion, fSmall, Muted, W - m - P(40), H - m);

        int cols = W > P(1250) ? 4 : (W > P(850) ? 3 : 2);
        float gap = P(18), top = bannerY + P(16), tw = (W - 2 * m - (cols - 1) * gap) / cols, th = P(150);
        for (int i = 0; i < games.Count; i++)
        {
            var gi = games[i];
            var r = new RectangleF(m + (i % cols) * (tw + gap), top + (i / cols) * (th + gap), tw, th);
            bool hover = r.Contains(mouse);
            FillRound(g, hover ? Blend(gi.Bg, gi.Accent, 0.16) : Blend(gi.Bg, Color.White, 0.03), r, P(12));
            DrawRound(g, hover ? gi.Accent : Line, r, P(12), hover ? 2 : 1);
            FillRound(g, gi.Accent, new RectangleF(r.X + P(20), r.Y + P(22), P(28), P(5)), P(2));
            DrawT(g, gi.Name, fH2, TextC, r.X + P(18), r.Y + P(36));
            DrawT(g, gi.Tagline, fSmall, Muted, r.X + P(20), r.Y + P(72));
            string sens = settings.ContainsKey(gi.Id) ? "Sens " + gi.Fmt(Sens(gi)) + "  ·  " + Dpi(gi) + " DPI" : "Sens not set yet";
            DrawT(g, sens, fSmall, settings.ContainsKey(gi.Id) ? gi.Accent : Muted, r.X + P(20), r.Bottom - P(36));
            var cap = gi;
            AddHit(r, () => GoGame(cap));
        }
        DrawT(g, "Everything uses your real mouse movement, so set your in-game sensitivity on the next screen.", fSmall, Muted, m + 2, H - m);
    }

    void PaintGame(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height, m = P(40);
        DrawMenuBg(g);
        AddBtn(g, new RectangleF(m, m, P(100), P(38)), "‹  Back", GoHome, false, false);
        AddBtn(g, new RectangleF(m + P(112), m, P(130), P(38)), "Loadout", () => { scr = Scr.Loadout; ShowBoxes(false); }, false, false);
        DrawT(g, game.Name.ToUpper(), fTitle, game.Accent, m - P(2), m + P(54));
        DrawT(g, game.Tagline, fBody, Muted, m + 1, m + P(106));
        float top = m + P(156);

        // settings
        var sp = new RectangleF(m, top, P(330), P(410));
        FillRound(g, Panel, sp, P(12));
        DrawT(g, "YOUR SETTINGS", fLabel, Muted, sp.X + P(20), sp.Y + P(18));
        DrawT(g, "In-game sensitivity" + (game.Suffix != "" ? " (" + game.Suffix + ")" : ""), fSmall, TextC, sp.X + P(20), sp.Y + P(48));
        Place(sensBox, sp.X + P(20), sp.Y + P(72), P(160));
        DrawT(g, "Mouse DPI" + (Dpi(game) == globalDpi ? " (from your profile)" : " (this game only)"), fSmall, TextC, sp.X + P(20), sp.Y + P(126));
        Place(dpiBox, sp.X + P(20), sp.Y + P(150), P(160));
        double cm = 360.0 / (game.DegPerCount(Sens(game)) * Dpi(game)) * 2.54;
        DrawT(g, cm.ToString("0.0") + " cm", fH2, game.Accent, sp.X + P(18), sp.Y + P(206));
        DrawT(g, "of mouse movement for a full 360° turn", fSmall, Muted, sp.X + P(20), sp.Y + P(238));
        if (game.Note != "") DrawTW(g, game.Note, fSmall, Muted, new RectangleF(sp.X + P(20), sp.Y + P(268), sp.Width - P(40), P(60)));

        // Mouse acceleration checkbox
        var row = new RectangleF(sp.X + P(14), sp.Y + P(332), sp.Width - P(28), P(62));
        bool rowHover = row.Contains(mouse);
        if (rowHover) FillRound(g, PanelHi, row, P(8));
        var box = new RectangleF(row.X + P(8), row.Y + P(10), P(20), P(20));
        if (accelOn) FillRound(g, game.Accent, box, P(4));
        DrawRound(g, accelOn ? game.Accent : Muted, box, P(4), P(1.5f));
        if (accelOn)
            using (var pen = new Pen(OnColor(game.Accent), P(2.5f)))
                g.DrawLines(pen, new[] { new PointF(box.X + P(4), box.Y + P(10)), new PointF(box.X + P(8.5f), box.Y + P(14.5f)), new PointF(box.X + P(16), box.Y + P(5.5f)) });
        DrawT(g, "Mouse acceleration", fBody, TextC, box.Right + P(10), row.Y + P(7));
        DrawT(g, "Faster swipes travel further", fSmall, Muted, box.Right + P(11), row.Y + P(33));
        AddHit(row, () => { accelOn = !accelOn; Save(); });

        // modes
        float x2 = sp.Right + P(28), w2 = W - x2 - m;
        DrawT(g, "MODE", fLabel, Muted, x2, top);
        string[] modes = { TRK, HTK, FLK, BOT, RCT, FND };
        string[] desc = {
            "Keep your crosshair on a moving ball. Score is the % of time on target.",
            "No clicking: stay on the ball to drain its health. Kill it and a new one appears.",
            "Snap to targets as they pop up and click. 30 seconds.",
            "Strafing and standing bots with 3 HP. Headshots kill instantly.",
            "Click the moment the screen turns green. 5 tries.",
            "5 short tracking rounds at hidden sens values. Finds the one you aim best with." };
        float gap = P(12), cw = (w2 - 5 * gap) / 6, ch = P(182);
        for (int i = 0; i < modes.Length; i++)
        {
            var r = new RectangleF(x2 + i * (cw + gap), top + P(26), cw, ch);
            bool sel = mode == modes[i], hover = r.Contains(mouse);
            FillRound(g, sel ? Blend(Panel, game.Accent, 0.18) : (hover ? PanelHi : Panel), r, P(12));
            if (sel) DrawRound(g, game.Accent, r, P(12), 2);
            DrawT(g, modes[i], fH3, sel ? game.Accent : TextC, r.X + P(16), r.Y + P(14));
            DrawTW(g, desc[i], fSmall, Muted, new RectangleF(r.X + P(16), r.Y + P(46), r.Width - P(28), r.Height - P(84)));
            // your best rank in this mode at the chosen difficulty
            int rk = modes[i] == FND ? -2 : BestRank(modes[i], diff);
            float ry = r.Bottom - P(36);
            if (rk >= 0)
            {
                DrawRankBadge(g, r.X + P(12), ry, P(26), rk, 1);
                DrawT(g, RankNames[rk].ToUpper(), fLabel, RankColors[rk], r.X + P(44), ry + P(5));
            }
            else DrawT(g, rk == -2 ? "No rank" : "Unranked", fSmall, Muted, r.X + P(16), ry + P(4));
            var mi = modes[i];
            AddHit(r, () => mode = mi);
        }
        float y = top + P(26) + ch + P(26);
        if (mode == TRK || mode == HTK || mode == FLK || mode == BOT)
        {
            DrawT(g, "DIFFICULTY", fLabel, Muted, x2, y);
            for (int i = 0; i < 4; i++)
            {
                int di = i;
                AddBtn(g, new RectangleF(x2 + i * P(120), y + P(24), P(110), P(40)), DiffNames[i], () => diff = di, false, diff == i);
            }
            y += P(24) + P(40) + P(26);
        }
        AddBtn(g, new RectangleF(x2, y, P(220), P(54)), "Start   (Space)", () => StartPlay(mode), true, false);
        if (mode == FLK || mode == BOT)
            AddBtn(g, new RectangleF(x2 + P(236), y, P(190), P(54)), "Gun: " + (showGun ? "On" : "Off") + "   (G)", () => { showGun = !showGun; Save(); }, false, showGun);
        y += P(54) + P(30);

        // progress
        var pr = new RectangleF(x2, y, w2, Math.Max(P(170), H - y - m - (MusicVisible ? P(92) : 0)));
        FillRound(g, Panel, pr, P(12));
        if (mode == FND)
        {
            DrawT(g, "HOW THE SENS FINDER WORKS", fLabel, Muted, pr.X + P(20), pr.Y + P(18));
            DrawTW(g, "You play 5 tracking rounds of 10 seconds each. Each round secretly uses a different sensitivity, from 80% to 120% of yours, in a random order. "
                + "At the end you see which one you tracked best with, converted to a " + game.Name + " sens number.",
                fBody, TextC, new RectangleF(pr.X + P(20), pr.Y + P(46), Math.Min(pr.Width - P(40), P(700)), pr.Height - P(60)));
            return;
        }
        var hist = History(mode, diff);
        bool lower = mode == RCT;
        DrawT(g, "YOUR PROGRESS  ·  " + mode.ToUpper() + (mode == RCT ? "" : "  ·  " + DiffNames[diff].ToUpper()), fLabel, Muted, pr.X + P(20), pr.Y + P(18));
        if (hist.Count == 0)
        {
            DrawT(g, "No rounds yet. Your scores will show up here.", fBody, Muted, pr.X + P(20), pr.Y + P(50));
            return;
        }
        double best = lower ? hist.Min(r => r.Score) : hist.Max(r => r.Score);
        string bestLine = "Best  " + FmtScore(mode, best) + "      Last  " + FmtScore(mode, hist.Last().Score) + "      Rounds  " + hist.Count + "      Rank";
        DrawT(g, bestLine, fH3, TextC, pr.X + P(20), pr.Y + P(44));
        int prk = RankIndex(mode, diff, best);
        if (prk >= 0) DrawT(g, RankNames[prk], fH3, RankColors[prk], pr.X + P(20) + g.MeasureString(bestLine, fH3).Width + P(4), pr.Y + P(44));
        var chart = new RectangleF(pr.X + P(60), pr.Y + P(90), pr.Width - P(90), pr.Height - P(120));
        if (chart.Height > P(50)) DrawChart(g, chart, hist.Skip(Math.Max(0, hist.Count - 20)).Select(r => r.Score).ToList(), mode);
    }

    static string FmtScore(string m, double v)
    {
        if (m == TRK) return v.ToString("0.0") + "%";
        if (m == FLK) return v.ToString("0") + " hits";
        if (m == BOT || m == HTK) return v.ToString("0") + " kills";
        return v.ToString("0") + " ms";
    }

    void DrawChart(Graphics g, RectangleF r, List<double> vals, string m)
    {
        double lo = vals.Min(), hi = vals.Max();
        double pad = Math.Max((hi - lo) * 0.15, m == TRK ? 2 : (m == FLK ? 1 : 10));
        lo -= pad; hi += pad;
        using (var gridPen = new Pen(Line, 1))
        {
            for (int i = 0; i <= 2; i++)
            {
                float y = r.Bottom - r.Height * i / 2f;
                g.DrawLine(gridPen, r.X, y, r.Right, y);
                DrawT(g, FmtScore(m, lo + (hi - lo) * i / 2).Replace(" hits", "").Replace(" kills", "").Replace(" ms", ""), fSmall, Muted, r.X - P(52), y - P(9));
            }
        }
        var pts = new PointF[vals.Count];
        for (int i = 0; i < vals.Count; i++)
        {
            float x = vals.Count == 1 ? r.X + r.Width / 2 : r.X + r.Width * i / (vals.Count - 1);
            pts[i] = new PointF(x, (float)(r.Bottom - (vals[i] - lo) / (hi - lo) * r.Height));
        }
        if (pts.Length > 1)
        {
            var area = new List<PointF>(pts) { new PointF(pts[pts.Length - 1].X, r.Bottom), new PointF(pts[0].X, r.Bottom) };
            using (var b = new SolidBrush(Color.FromArgb(40, game.Accent))) g.FillPolygon(b, area.ToArray());
            using (var pen = new Pen(game.Accent, P(2.5f))) g.DrawLines(pen, pts);
        }
        using (var b = new SolidBrush(game.Accent))
            foreach (var p in pts) g.FillEllipse(b, p.X - P(3), p.Y - P(3), P(6), P(6));
        var lp = pts[pts.Length - 1];
        using (var b = new SolidBrush(TextC)) g.FillEllipse(b, lp.X - P(5), lp.Y - P(5), P(10), P(10));
        DrawT(g, m == RCT ? "lower is better" : "higher is better", fSmall, Muted, r.Right - P(110), r.Bottom + P(6));
    }

    // The round's background (color, soft glow, arena panel) never changes during a round, but painting the
    // full-screen gradient took most of each frame. It's drawn once into a bitmap and copied in every frame.
    Bitmap playBg;
    string playBgKey = "";

    void DrawPlayBackground(Graphics g, float W, float H)
    {
        string key = W + "x" + H + game.Id + arena;
        if (playBg == null || playBgKey != key)
        {
            if (playBg != null) playBg.Dispose();
            playBg = new Bitmap((int)W, (int)H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            playBgKey = key;
            using (var gb = Graphics.FromImage(playBg))
            {
                gb.SmoothingMode = SmoothingMode.AntiAlias;
                gb.Clear(game.Bg);
                using (var glow = new GraphicsPath())
                {
                    glow.AddEllipse(W * 0.15f, H * 0.05f, W * 0.7f, H * 0.9f);
                    using (var pb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(34, game.Accent), SurroundColors = new[] { Color.FromArgb(0, game.Accent) } })
                        gb.FillPath(pb, glow);
                }
                FillRound(gb, Blend(game.Bg, Color.White, 0.025f), arena, P(18));
                DrawRound(gb, Blend(game.Bg, Color.White, 0.07f), arena, P(18), 1);
            }
        }
        var mode = g.CompositingMode; g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(playBg, 0, 0);
        g.CompositingMode = mode;
    }

    void PaintPlay(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height, m = P(28);
        if (playMode == RCT) { PaintReaction(g); return; }
        DrawPlayBackground(g, W, H);

        // everything in the world shakes a little on kills
        var saved = g.Save();
        if (shakeT > 0)
        {
            float amp = (float)(shakeT * 40 * S);
            g.TranslateTransform((float)((rnd.NextDouble() - 0.5) * amp), (float)((rnd.NextDouble() - 0.5) * amp));
        }

        if (playMode == FLK)
        {
            if (countdown <= 0)
            {
                DrawTarget(g, tx, ty, tr, false);
                double to = FlickTimeout[playDiff];
                if (to > 0)
                {
                    float frac = (float)Math.Max(0, 1 - (sw.Elapsed.TotalSeconds - spawnT) / to);
                    float rr = (float)tr + P(7);
                    using (var pen = new Pen(Color.FromArgb(160, TextC), P(2)))
                        g.DrawArc(pen, (float)tx - rr, (float)ty - rr, rr * 2, rr * 2, -90, 360 * frac);
                }
            }
        }
        else if (playMode == BOT) foreach (var b in bots.OrderBy(x => x.U)) DrawBot(g, b);
        else
        {
            bool onBall = countdown <= 0 && Hit(cx - bx, cy - (by + jumpOff), br);
            DrawTarget(g, bx, by + jumpOff, br, onBall);
            if (playMode == HTK) DrawHealthBar(g, bx, by + jumpOff - br - P(14), br * 2.4, ballHp / 100, onBall);
        }

        foreach (var p in fx)
        {
            float k = (float)(p.Life / p.MaxLife), s = (float)p.Size * (0.5f + k * 0.5f);
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * k), p.C))) g.FillEllipse(b, (float)p.X - s, (float)p.Y - s, s * 2, s * 2);
        }
        foreach (var t in floats)
        {
            int a = (int)(255 * Math.Min(1, t.Life / 0.4));
            DrawTC(g, t.Text, fH3, Color.FromArgb(a, t.C), new RectangleF((float)t.X - P(120), (float)t.Y - P(14), P(240), P(28)));
        }
        g.Restore(saved);

        if ((playMode == FLK || playMode == BOT) && showGun && countdown <= 0) DrawGun(g, W, H, sw.Elapsed.TotalSeconds);
        if (flashT > 0) using (var b = new SolidBrush(Color.FromArgb((int)(flashT / 0.08 * 60), Color.White))) g.FillRectangle(b, 0, 0, W, H);

        // HUD
        string left = playMode == FND
            ? game.Name.ToUpper() + "  ·  SENS FINDER  ·  ROUND " + Math.Min(fIdx + 1, 5) + " OF 5"
            : game.Name.ToUpper() + "  ·  " + playMode.ToUpper() + "  ·  " + DiffNames[playDiff].ToUpper();
        DrawT(g, left, fLabel, Muted, m, H - m - P(16));
        var bar = new RectangleF(W / 2 - P(200), m + P(4), P(400), P(6));
        FillRound(g, Blend(game.Bg, Color.White, 0.1f), bar, P(3));
        FillRound(g, game.Accent, new RectangleF(bar.X, bar.Y, bar.Width * (float)Math.Max(0, timeLeft / roundLen), bar.Height), P(3));
        DrawTC(g, Math.Max(0, timeLeft).ToString("0.0"), fH3, TextC, new RectangleF(bar.X, bar.Bottom + P(6), bar.Width, P(28)));
        string score = playMode == FLK
            ? hits + " hits  ·  " + (hits + misses > 0 ? (hits * 100 / (hits + misses)) + "% accuracy" : "–")
            : playMode == BOT
            ? kills + " kills  ·  " + (botHits > 0 ? (headHits * 100 / botHits) + "% headshots" : "–")
            : playMode == HTK
            ? htKills + " kills  ·  " + (totTime > 0 ? (onTime / totTime * 100).ToString("0") : "0") + "% on target"
            : (totTime > 0 ? (onTime / totTime * 100).ToString("0") : "0") + "% on target";
        float sw2 = g.MeasureString(score, fH3).Width;
        DrawT(g, score, fH3, TextC, W - m - sw2, m - P(4));
        DrawTC(g, "Esc to stop", fSmall, Muted, new RectangleF(0, H - m - P(20), W, P(20)));

        // combo counter under the timer
        if (combo >= 3)
        {
            float scale = 1 + (float)comboPop * 0.35f;
            using (var f = new Font("Bahnschrift", 30 * scale, FontStyle.Bold))
                DrawTC(g, "×" + combo, f, Blend(game.Accent, Color.White, comboPop * 0.6), new RectangleF(0, bar.Bottom + P(36), W, P(60) * scale));
            DrawTC(g, "COMBO", fLabel, Muted, new RectangleF(0, bar.Bottom + P(36) + P(56) * scale, W, P(18)));
        }

        // kill feed under the score
        float fy = m + P(30);
        foreach (var it in feed)
        {
            int a = (int)(255 * Math.Min(1, it.Life / 0.5));
            string txt = (it.Head ? "HS  " : "»  ") + it.Text;
            float tw = g.MeasureString(txt, fSmall).Width + P(20);
            var r = new RectangleF(W - m - tw, fy, tw, P(26));
            FillRound(g, Color.FromArgb(a * 150 / 255, 0, 0, 0), r, P(6));
            if (it.Head) FillRound(g, Color.FromArgb(a, 255, 70, 85), new RectangleF(r.X, r.Y, P(3), r.Height), P(1.5f));
            DrawTC(g, txt, fSmall, Color.FromArgb(a, it.Head ? Color.FromArgb(255, 120, 130) : TextC), r);
            fy += P(30);
        }

        if (countdown > 0)
        {
            string sub = playMode == FND && fIdx > 0 ? "Next round, new hidden sensitivity" : (playMode == FLK ? "Click the targets as they appear" : playMode == BOT ? "Body shots do 1 damage. Headshots kill instantly." : playMode == HTK ? "No clicking. Stay on the ball to drain its health." : "Keep your crosshair on the ball");
            DrawTC(g, Math.Ceiling(countdown).ToString("0"), fHuge, TextC, new RectangleF(0, H * 0.18f, W, P(130)));
            DrawTC(g, sub, fBody, Muted, new RectangleF(0, H * 0.18f + P(130), W, P(30)));
        }
        DrawCrosshair(g, cx, cy);
        if (playMode == BOT && hitMarkT > 0)
        {
            float a = P(7), b2 = P(13);
            using (var pen = new Pen(hitMarkHead ? Color.FromArgb(255, 90, 90) : Color.White, P(2)))
            {
                g.DrawLine(pen, cx - b2, cy - b2, cx - a, cy - a);
                g.DrawLine(pen, cx + a, cy - a, cx + b2, cy - b2);
                g.DrawLine(pen, cx - b2, cy + b2, cx - a, cy + a);
                g.DrawLine(pen, cx + a, cy + a, cx + b2, cy + b2);
            }
        }
    }

    void DrawBot(Graphics g, Bot b)
    {
        float k = b.Dead ? (float)Math.Max(0, b.DeadT / 0.25) : 1f;   // fades and shrinks when it dies
        float u = (float)b.U * (0.6f + 0.4f * k), x = (float)b.X, ground = (float)b.Ground;
        float bodyH = 1.9f * u, headR = 0.31f * u, bodyTop = ground - bodyH, headCy = bodyTop - 0.08f * u - headR;
        int alpha = (int)(255 * k);
        var body = new RectangleF(x - u / 2, bodyTop, u, bodyH);
        var head = new RectangleF(x - headR, headCy - headR, headR * 2, headR * 2);
        using (var main = new SolidBrush(Color.FromArgb(alpha, 0, 224, 226)))
        using (var shade = new SolidBrush(Color.FromArgb((int)(alpha * 0.45), 0, 120, 140)))
        using (var lite = new SolidBrush(Color.FromArgb((int)(alpha * 0.35), 255, 255, 255)))
        using (var bodyPath = Round(body, u / 2))
        {
            // soft shadow on the floor
            using (var sh = new SolidBrush(Color.FromArgb((int)(alpha * 0.25), 0, 0, 0)))
                g.FillEllipse(sh, x - u * 0.6f, ground - u * 0.08f, u * 1.2f, u * 0.2f);
            g.FillPath(main, bodyPath);
            g.FillEllipse(main, head);
            // right-side shading and a highlight give the flat shapes some roundness
            g.SetClip(bodyPath);
            g.FillRectangle(shade, x + u * 0.12f, bodyTop, u / 2, bodyH);
            g.ResetClip();
            using (var hp = new GraphicsPath())
            {
                hp.AddEllipse(head);
                g.SetClip(hp);
                g.FillRectangle(shade, x + headR * 0.25f, head.Y, headR, head.Height);
                g.ResetClip();
            }
            g.FillEllipse(lite, x - u * 0.32f, bodyTop + u * 0.18f, u * 0.18f, u * 0.5f);
            g.FillEllipse(lite, x - headR * 0.6f, headCy - headR * 0.7f, headR * 0.5f, headR * 0.5f);
            if (b.Flash > 0)
                using (var fl = new SolidBrush(Color.FromArgb(170, 255, 255, 255))) { g.FillPath(fl, bodyPath); g.FillEllipse(fl, head); }
        }
        if (!b.Dead)
        {
            float bw = u * 0.9f, bh = Math.Max(P(3), u * 0.07f), seg = (bw - 2 * P(2)) / 3, by2 = headCy - headR - bh - P(6);
            for (int i = 0; i < 3; i++)
                using (var br2 = new SolidBrush(i < b.Hp ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0)))
                    g.FillRectangle(br2, x - bw / 2 + i * (seg + P(2)), by2, seg, bh);
        }
    }

    void DrawTarget(Graphics g, double x, double y, double r, bool on)
    {
        var rect = new RectangleF((float)(x - r), (float)(y - r), (float)(2 * r), (float)(2 * r));
        Color c = on ? Blend(game.Accent, Color.White, 0.25) : game.Accent;
        using (var b = new SolidBrush(c))
        {
            if (game.Square)
            {
                g.FillRectangle(b, rect);
                using (var dark = new SolidBrush(Blend(c, Color.Black, 0.25)))
                    g.FillRectangle(dark, rect.X + rect.Width * 0.55f, rect.Y + rect.Height * 0.15f, rect.Width * 0.3f, rect.Height * 0.3f);
                using (var lite = new SolidBrush(Color.FromArgb(70, Color.White)))
                    g.FillRectangle(lite, rect.X + rect.Width * 0.12f, rect.Y + rect.Height * 0.55f, rect.Width * 0.3f, rect.Height * 0.3f);
            }
            else
            {
                g.FillEllipse(b, rect);
                using (var lite = new SolidBrush(Color.FromArgb(60, Color.White)))
                    g.FillEllipse(lite, rect.X + rect.Width * 0.2f, rect.Y + rect.Height * 0.14f, rect.Width * 0.38f, rect.Height * 0.32f);
            }
        }
        if (on)
            using (var pen = new Pen(Color.White, P(3)))
            {
                var o = RectangleF.Inflate(rect, P(5), P(5));
                if (game.Square) g.DrawRectangle(pen, o.X, o.Y, o.Width, o.Height); else g.DrawEllipse(pen, o);
            }
    }

    void DrawCrosshair(Graphics g, float x, float y)
    {
        float gap = P(4), len = P(9);
        using (var outline = new Pen(Color.FromArgb(200, 0, 0, 0), P(4)))
        using (var pen = new Pen(Color.White, P(2)))
        {
            foreach (var p in new[] { outline, pen })
            {
                g.DrawLine(p, x - gap - len, y, x - gap, y);
                g.DrawLine(p, x + gap, y, x + gap + len, y);
                g.DrawLine(p, x, y - gap - len, x, y - gap);
                g.DrawLine(p, x, y + gap, x, y + gap + len);
            }
        }
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x - P(1.5f), y - P(1.5f), P(3), P(3));
    }

    void PaintReaction(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height;
        Color bg; string big, small;
        int n = rTimes.Count;
        switch (rState)
        {
            case 1: bg = Color.FromArgb(176, 38, 52); big = "Wait for green…"; small = "Try " + (n + 1) + " of 5"; break;
            case 2: bg = Color.FromArgb(34, 170, 88); big = "CLICK!"; small = ""; break;
            case 3: bg = game.Bg; big = lastReaction.ToString("0") + " ms"; small = "Click or press Space for try " + (n + 1) + " of 5"; break;
            case 4: bg = Color.FromArgb(200, 120, 36); big = "Too soon!"; small = "Wait for the green screen. Click to try again."; break;
            default: bg = game.Bg; big = "Reaction test"; small = "Click or press Space to start. When the screen turns green, click as fast as you can. 5 tries."; break;
        }
        g.Clear(bg);
        DrawTC(g, big, rState == 3 ? fHuge : fBig, Color.White, new RectangleF(0, H / 2 - P(110), W, P(140)));
        DrawTC(g, small, fBody, Color.FromArgb(225, 255, 255, 255), new RectangleF(P(40), H / 2 + P(40), W - P(80), P(60)));
        if (n > 0) DrawTC(g, "So far: " + string.Join("  ·  ", rTimes.Select(t => t.ToString("0")).ToArray()) + " ms", fH3, Color.FromArgb(220, 255, 255, 255), new RectangleF(0, H - P(110), W, P(30)));
        DrawTC(g, "Esc to stop", fSmall, Color.FromArgb(180, 255, 255, 255), new RectangleF(0, H - P(48), W, P(20)));
    }

    void PaintResults(Graphics g)
    {
        float W = ClientSize.Width, H = ClientSize.Height, m = P(48);
        DrawMenuBg(g);
        DrawT(g, resTitle.ToUpper(), fLabel, Muted, m, m);
        // the score rolls up, then lands with a flash
        double since = sw.Elapsed.TotalSeconds - resAnimStart, k = Math.Min(1, since / 0.9), ease = 1 - Math.Pow(1 - k, 3);
        string shown = resMain;
        if (k < 1 && playMode != FND)
        {
            double v = resScore * ease;
            shown = playMode == TRK ? v.ToString("0.0") + "%" : playMode == RCT ? v.ToString("0") + " ms" : v.ToString("0");
        }
        double land = since - 0.9;
        Color mainC = land > 0 && land < 0.25 ? Blend(game.Accent, Color.White, 1 - land / 0.25) : game.Accent;
        DrawT(g, shown, fHuge, mainC, m - P(6), m + P(24));
        float mainW = g.MeasureString(resMain, fHuge).Width;
        float bx4 = m + mainW + P(10);
        if (resRank >= 0)
        {
            double reveal = Math.Max(0, (since - 0.8) / 0.35);
            DrawRankBadge(g, bx4, m + P(18), P(120), resRank, reveal);
            if (reveal > 0)
            {
                DrawT(g, RankNames[resRank].ToUpper(), fH2, RankColors[resRank], bx4 + P(124), m + P(44));
                bool lower; var steps = RankSteps(playMode, playDiff, out lower);
                string next = resRank < 7 ? "Next: " + RankNames[resRank + 1] + " at " + FmtScore(playMode, steps[resRank]) : "Top rank!";
                DrawT(g, next, fSmall, Muted, bx4 + P(126), m + P(80));
            }
            bx4 += P(124) + P(150);
        }
        if (resPB)
        {
            var badge = new RectangleF(bx4, m + P(110), P(180), P(34));
            FillRound(g, Good, badge, P(17));
            DrawTC(g, "NEW PERSONAL BEST", fLabel, Color.FromArgb(10, 30, 16), badge);
        }
        DrawT(g, resLabel, fBody, Muted, m, m + P(150));
        float y = m + P(196);
        foreach (var l in resLines) { DrawT(g, l, fBody, TextC, m, y); y += P(30); }
        if (playMode == FLK || playMode == BOT || playMode == HTK) { if (bestCombo >= 3) { DrawT(g, "Best combo: ×" + bestCombo, fBody, TextC, m, y); y += P(30); } }
        foreach (var u in newUnlocks)
        {
            var s = Skins.First(x => x.Name == u);
            DrawT(g, "NEW SKIN UNLOCKED: " + u.ToUpper() + "  ·  equip it in Loadout", fH3, Blend(s.Glow, Color.White, 0.2 + 0.2 * Math.Sin(since * 6)), m, y + P(4));
            y += P(34);
        }

        float colW = Math.Min(P(560), W / 2 - m * 1.5f);
        if (playMode == FND && fScores != null) DrawFinderChart(g, new RectangleF(m, y + P(20), colW, P(220)));

        // coach panel
        if (showCoach)
        {
            float px = Math.Max(W / 2, m + colW + P(40)), pw = W - px - m;
            var panel = new RectangleF(px, m + P(24), pw, P(330));
            FillRound(g, Panel, panel, P(12));
            DrawT(g, coachHead, fLabel, Muted, panel.X + P(24), panel.Y + P(22));
            DrawTW(g, coachText, fBody, TextC, new RectangleF(panel.X + P(24), panel.Y + P(52), panel.Width - P(48), P(130)));
            float cy2 = panel.Y + P(190);
            if (!double.IsNaN(sugSens))
            {
                if (!sugApplied)
                {
                    DrawT(g, "Try " + game.Fmt(sugSens) + "  (now " + game.Fmt(Sens(game)) + ")", fH2, game.Accent, panel.X + P(22), cy2);
                    AddBtn(g, new RectangleF(panel.X + P(24), cy2 + P(48), P(170), P(42)), "Use " + game.Fmt(sugSens), ApplySuggestion, true, false);
                    AddBtn(g, new RectangleF(panel.X + P(206), cy2 + P(48), P(170), P(42)), "Keep " + game.Fmt(Sens(game)), () => sugSens = double.NaN, false, false);
                }
                else
                    DrawTW(g, "Saved. The trainer now uses " + game.Fmt(Sens(game)) + ". Set the same number in " + game.Name + " so they match.",
                        fBody, Good, new RectangleF(panel.X + P(24), cy2, panel.Width - P(48), P(80)));
            }
            else if (playMode != RCT)
                DrawT(g, "Current sens: " + game.Fmt(Sens(game)), fH3, Muted, panel.X + P(24), cy2);

            if (scoreCode != "")
            {
                var cp = new RectangleF(px, panel.Bottom + P(18), pw, P(178));
                FillRound(g, Panel, cp, P(12));
                DrawT(g, "LEADERBOARD SCORE CODE", fLabel, Muted, cp.X + P(24), cp.Y + P(20));
                using (var mono = new Font("Consolas", 9.5f))
                    DrawTW(g, scoreCode, mono, TextC, new RectangleF(cp.X + P(24), cp.Y + P(46), cp.Width - P(48), P(44)));
                AddBtn(g, new RectangleF(cp.X + P(24), cp.Y + P(98), P(180), P(42)), "Copy score code", CopyCode, true, false);
                AddBtn(g, new RectangleF(cp.X + P(216), cp.Y + P(98), P(180), P(42)), "Open leaderboard", OpenLeaderboard, false, false);
                DrawT(g, copyNote != "" ? copyNote : "Record your run so a mod can verify it.", fSmall, copyNote != "" ? Good : Muted, cp.X + P(24), cp.Y + P(148));
            }
        }

        float by2 = H - m - P(54);
        AddBtn(g, new RectangleF(m, by2, P(220), P(54)), "Play again   (Space)", () => StartPlay(playMode), true, false);
        AddBtn(g, new RectangleF(m + P(236), by2, P(160), P(54)), "Back   (Esc)", () => GoGame(game), false, false);
    }

    void DrawFinderChart(Graphics g, RectangleF r)
    {
        DrawT(g, "% ON TARGET AT EACH SENSITIVITY", fLabel, Muted, r.X, r.Y);
        float top = r.Y + P(30), h = r.Height - P(70), bw = r.Width / 5;
        double max = Math.Max(1, fScores.Max());
        for (int i = 0; i < 5; i++)
        {
            float bh = (float)(fScores[i] / max * h);
            var bar = new RectangleF(r.X + i * bw + bw * 0.18f, top + h - bh, bw * 0.64f, bh);
            bool best = fScores[i] == fScores.Max();
            FillRound(g, best ? game.Accent : Blend(Panel, game.Accent, 0.35), bar, P(4));
            DrawTC(g, fScores[i].ToString("0") + "%", fSmall, TextC, new RectangleF(bar.X - P(10), bar.Y - P(22), bar.Width + P(20), P(20)));
            double s = game.SensFor(game.DegPerCount(Sens(game)) * fMults[i]);
            DrawTC(g, game.Fmt(s), fSmall, best ? game.Accent : Muted, new RectangleF(r.X + i * bw, top + h + P(6), bw, P(20)));
        }
    }

    static Icon MakeIcon()
    {
        var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var b = new SolidBrush(Color.FromArgb(15, 17, 21))) g.FillEllipse(b, 2, 2, 60, 60);
            using (var p = new Pen(Color.FromArgb(255, 84, 98), 6)) g.DrawEllipse(p, 12, 12, 40, 40);
            using (var p = new Pen(Color.White, 4)) { g.DrawLine(p, 32, 4, 32, 22); g.DrawLine(p, 32, 42, 32, 60); g.DrawLine(p, 4, 32, 22, 32); g.DrawLine(p, 42, 32, 60, 32); }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    [STAThread]
    static void Main()
    {
        try { SetProcessDPIAware(); } catch { }
        try { File.Delete(Application.ExecutablePath + ".old"); } catch { }   // left behind by the last update
        timeBeginPeriod(1);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Trainer());
    }
}
