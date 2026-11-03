using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// taskhub.log: `2026-11-02 18:21:05.123 INF panel    message`. D = what happened in detail, including the text of notifications and task names:
/// written only in a beta build or with "log_level": "debug" in config.json. I = events, W = something went wrong and was handled, E = a failure
/// the user can feel, with the stack. Never keys, tokens or request bodies; whatever slips in through an exception's text is masked here.
/// </summary>
static class Log
{
    public enum Level { Debug, Info, Warn, Error }

    static readonly object L = new();
    public static string Path;
    public static Level Min = Settings.IsBeta ? Level.Debug : Level.Info;
    public static long MaxBytes = Settings.IsBeta ? 20_000_000 : 4_000_000;
    public static int Keep = Settings.IsBeta ? 3 : 1;
    const int MaxLine = 16_000;

    public static string Mask(string s) => LogMask.Apply(s);

    /// <summary>A task id for the default level: a connector's id is built from a job's or instance's name, so it counts as that name.
    /// The hub's own random ids stay readable; the DBG line at registration links the hash to the id.</summary>
    public static string Id(string tid) =>
        tid == null ? null
        : tid.Length == 8 && tid.All(Uri.IsHexDigit) ? tid
        : "#" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tid)))[..8].ToLowerInvariant();

    public static void D(string area, string msg) => Write(Level.Debug, "DBG", area, msg);
    public static void I(string area, string msg) => Write(Level.Info, "INF", area, msg);
    public static void W(string area, string msg) => Write(Level.Warn, "WRN", area, msg);
    public static void E(string area, string msg) => Write(Level.Error, "ERR", area, msg);

    /// <summary>"log_level" in config.json; anything else keeps the build's default.</summary>
    public static void Configure(string level)
    {
        if (string.Equals(level, "debug", StringComparison.OrdinalIgnoreCase)) Min = Level.Debug;
        else if (string.Equals(level, "info", StringComparison.OrdinalIgnoreCase)) Min = Level.Info;
    }

    static void Write(Level level, string tag, string area, string msg)
    {
        if (level < Min || Path == null) return;
        try
        {
            var text = Mask(msg ?? "");
            if (text.Length > MaxLine) text = text[..MaxLine] + "…";
            // a stack trace stays readable, and text from outside can't pass for a line of its own
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine + "    ");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag} {area,-8} {text}";
            lock (L)
            {
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > MaxBytes) Rotate();
                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch { }
    }

    /// <summary>At the default level, what an earlier beta build or debug level wrote doesn't stay: its extra rotated files, the CLI's log,
    /// and a log holding debug lines (notification text, task names). Call once at start, after the level is known.</summary>
    public static void DropStale()
    {
        if (Path == null || Settings.IsBeta || Min == Level.Debug) return;
        static void Drop(string p) { try { File.Delete(p); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        static bool HasDebug(string p)
        {
            try { return File.Exists(p) && File.ReadLines(p).Any(l => l.Length > 28 && string.CompareOrdinal(l, 23, " DBG ", 0, 5) == 0); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
        lock (L)
        {
            for (int i = Keep + 1; i <= 3; i++) Drop(Path + "." + i);
            foreach (var p in new[] { Path, Path + ".1" })
                if (HasDebug(p)) Drop(p);
            var cli = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "cli.log");
            Drop(cli);
            Drop(cli + ".1");
        }
    }

    static void Rotate()
    {
        for (int i = Keep; i > 1; i--)
            if (File.Exists(Path + "." + (i - 1))) File.Move(Path + "." + (i - 1), Path + "." + i, true);
        File.Copy(Path, Path + ".1", true);
        File.WriteAllText(Path, "");
    }

    /// <summary>Tests only: `--selftest-log N` writes N lines of 1 KB and one of each kind that must be masked.</summary>
    public static int SelfTest(int lines)
    {
        foreach (var x in new[]
        {
            "https://api.telegram.org/bot123456789:" + new string('A', 35) + "/getMe failed", "token 123456789:" + new string('B', 35),
            "ghp_" + new string('C', 36), "github_pat_" + new string('D', 60), "AKIA" + new string('E', 16), "rpa_" + new string('F', 40), new string('L', 25) + ".GabcDe." + new string('M', 38),
            "Authorization: Bearer abcdefghijklmnop", "url?api_key=GGGGGGGG&x=1", "token=HHHHHHHH",
            "AKIA" + new string('E', 16) + ":" + new string('I', 40), "aws_secret_access_key=JJJJJJJJJJJJ", "{\"token\": \"KKKKKKKKKKKK\"}", string.Concat(Enumerable.Repeat("ab", 32)),
        })
            W("selftest", x);
        var pad = new string('x', 1024);
        for (int i = 0; i < lines; i++) I("selftest", pad);
        return 0;
    }
}

/// <summary>
/// Configuration. Lookup order for port/data dir:
///   command line (--port, --data)  >  TaskHubHost.settings.json next to the exe  >  defaults (47800, %APPDATA%\TaskHub).
/// Everything else lives in &lt;data&gt;\config.json (same keys as the Python version).
/// </summary>
sealed class Settings
{
    public const string AppId = "TaskHub";
    public const string Version = "3.1.0";
#if BETA
    public const bool IsBeta = true;
#else
    public const bool IsBeta = false;
#endif
    /// <summary>What a person reads; Version stays the plain number that the updater and the API compare.</summary>
    public static string VersionText => IsBeta ? Version + " beta" : Version;
    public const int DefaultPort = 47800;

    public int Port = DefaultPort;
    public string DataDir;
    public string ExeDir = AppContext.BaseDirectory;
    // config.json: read and changed from the UI thread, HTTP requests and the Telegram loop -> only through Get / Set
    JsonObject cfg;
    readonly object cfgLock = new();

    static readonly JsonObject Defaults = new()
    {
        ["port"] = DefaultPort,
        ["token"] = "",
        ["toasts"] = true,
        ["keep_finished"] = 60,
        ["history_days"] = 7,               // finished tasks the user has seen are kept this long (failed: until removed)
        ["log_lines"] = 400,
        ["panel_width"] = 480,              // width of the panel docked to the right edge (px at 96 dpi; drag its left edge)
        ["panel_autohide"] = true,
        ["panel_pinned"] = false,           // pin button: the panel stays open when it loses focus and keeps its screen edge
        ["panel_push_windows"] = false,     // pinned panel: also move / narrow normal windows out of its way
        ["panel_topmost"] = true,
        ["panel_transparency"] = 30,        // background transparency 0-100 % (Windows 11 acrylic behind it)
        ["card_transparency"] = 30,         // the cards' own background, 0-100 %
        ["panel_group"] = "project",        // many tasks: group them by project / source / none
        ["theme"] = "system",               // system | light | dark
        ["accent"] = "system",              // system (Windows accent colour) | taskhub
        ["toast_controls"] = true,
        ["progress_toasts"] = false,
        ["language"] = "auto",
        ["toast_seconds"] = 4,              // on-screen time of a plain toast (then it stays in the notification center)
        ["toast_seconds_interactive"] = 8,  // toasts with buttons / inputs
        ["toasts_when_panel_open"] = false, // false: no popup banners while the panel is open
        ["toast_rate"] = 10,                // notifications per minute (toasts + Telegram; bursts coalesce), 0 = no limit
        ["toast_task_gap"] = 2,             // seconds between two notifications of one task (the rest coalesce into the latest)
    };

    string cfgPath;

    /// <summary>A copy of a config value, or null.</summary>
    public JsonNode Get(string k)
    {
        lock (cfgLock) return cfg[k]?.DeepClone();
    }

    /// <summary>Sets config values (null removes the key) and saves config.json, as one step.
    /// Returns false if the file could not be written (the values stay in effect until the next start).</summary>
    public bool Set(params (string k, JsonNode v)[] kv)
    {
        lock (cfgLock)
        {
            foreach (var (k, v) in kv)
                if (v == null) cfg.Remove(k); else cfg[k] = v;
            try
            {
                var tmp = cfgPath + ".tmp";
                File.WriteAllText(tmp, cfg.ToJsonString(J.Pretty));
                File.Move(tmp, cfgPath, true);
                return true;
            }
            catch (Exception e) { Log.E("config", "config save failed: " + e.Message); return false; }
        }
    }

    public static JsonObject ReadExeSettings()
    {
        try
        {
            var p = System.IO.Path.Combine(AppContext.BaseDirectory, "TaskHubHost.settings.json");
            if (File.Exists(p)) return J.Parse(File.ReadAllText(p)) as JsonObject;
        }
        catch { }
        return null;
    }

    public static Settings Load(string[] args)
    {
        var s = new Settings();
        var exe = ReadExeSettings();
        string data = null;
        int? port = null;
        if (exe != null)
        {
            if (J.Num(exe["port"]) is double p) port = (int)p;
            if (J.IsStr(exe["data"])) data = J.Str(exe["data"]);
        }
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var pp)) port = pp;
            if (args[i] == "--data" && i + 1 < args.Length) data = args[i + 1];
        }
        data = Environment.ExpandEnvironmentVariables(data ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId));
        Directory.CreateDirectory(data);
        s.DataDir = data;
        Log.Path = System.IO.Path.Combine(data, "taskhub.log");

        var cfgPath = s.cfgPath = System.IO.Path.Combine(data, "config.json");
        var cfg = (JsonObject)Defaults.DeepClone();
        if (File.Exists(cfgPath))
        {
            try
            {
                if (J.Parse(File.ReadAllText(cfgPath)) is JsonObject user)
                    foreach (var kv in user) cfg[kv.Key] = J.C(kv.Value);
            }
            catch (Exception e) { Log.E("config", "config.json unreadable: " + e.Message); }
        }
        else
        {
            try { File.WriteAllText(cfgPath, Defaults.ToJsonString(J.Pretty)); } catch { }
        }
        s.cfg = cfg;
        Log.Configure(J.Str(cfg["log_level"]));
        s.Port = port ?? (J.Num(cfg["port"]) is double cp ? (int)cp : DefaultPort);
        return s;
    }

    public string Token => J.Str(Get("token"));
    public bool Bool(string k, bool def = false) { lock (cfgLock) return cfg.ContainsKey(k) ? J.Truthy(cfg[k]) : def; }
    public int Int(string k, int def) => J.Num(Get(k)) is double d ? (int)d : def;
    public string Url => $"http://127.0.0.1:{Port}";
    public string File_(string name) => System.IO.Path.Combine(DataDir, name);

    /// <summary>The key taskhub.exe uses to tell this hub from another program on its port (hub.key in the data folder, this user only).
    /// It is never sent: the hub only signs what the CLI asks with it.</summary>
    public byte[] HubKey => hubKey ??= LoadHubKey();
    byte[] hubKey;

    byte[] LoadHubKey()
    {
        var path = File_("hub.key");
        try { if (HexKey(File.ReadAllText(path)) is { } saved) return saved; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var text = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(key));
        try
        {
            File.Delete(path);
            var acl = new System.Security.AccessControl.FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            using var f = new FileInfo(path).Create(FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, acl);
            f.Write(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { Log.W("hubkey", "not saved: " + e.GetType().Name); }
        return key;
    }

    static byte[] HexKey(string s)
    {
        s = s.Trim();
        if (s.Length != 64 || !s.All(Uri.IsHexDigit)) return null;
        return Convert.FromHexString(s);
    }
}
