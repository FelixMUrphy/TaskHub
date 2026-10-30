using System.Text.Json.Nodes;

namespace TaskHub;

static class Log
{
    static readonly object L = new();
    public static string Path;

    public static void W(string msg)
    {
        var line = DateTime.Now.ToString("HH:mm:ss ") + msg;
        if (Path == null) return;
        lock (L)
        {
            try
            {
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > 4_000_000)
                {
                    File.Copy(Path, Path + ".1", true);
                    File.WriteAllText(Path, "");
                }
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch { }
        }
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
    public const string Version = "2.8.0";
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
        ["log_lines"] = 400,
        ["panel_width"] = 480,              // width of the panel docked to the right edge (px at 96 dpi; drag its left edge)
        ["panel_autohide"] = true,
        ["panel_pinned"] = false,           // pin button: the panel stays open when it loses focus
        ["panel_topmost"] = true,
        ["panel_transparency"] = 30,        // background transparency 0-100 % (Windows 11 acrylic behind it)
        ["theme"] = "system",               // system | light | dark
        ["accent"] = "system",              // system (Windows accent colour) | taskhub
        ["toast_controls"] = true,
        ["progress_toasts"] = false,
        ["language"] = "auto",
        ["toast_seconds"] = 4,              // on-screen time of a plain toast (then it stays in the notification center)
        ["toast_seconds_interactive"] = 8,  // toasts with buttons / inputs
        ["toasts_when_panel_open"] = false, // false: no popup banners while the panel is open
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
            catch (Exception e) { Log.W("config save failed: " + e.Message); return false; }
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
            catch (Exception e) { Log.W("config.json unreadable: " + e.Message); }
        }
        else
        {
            try { File.WriteAllText(cfgPath, Defaults.ToJsonString(J.Pretty)); } catch { }
        }
        s.cfg = cfg;
        s.Port = port ?? (J.Num(cfg["port"]) is double cp ? (int)cp : DefaultPort);
        return s;
    }

    public string Token => J.Str(Get("token"));
    public bool Bool(string k, bool def = false) { lock (cfgLock) return cfg.ContainsKey(k) ? J.Truthy(cfg[k]) : def; }
    public int Int(string k, int def) => J.Num(Get(k)) is double d ? (int)d : def;
    public string Url => $"http://127.0.0.1:{Port}";
    public string File_(string name) => System.IO.Path.Combine(DataDir, name);
}
