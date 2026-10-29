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
    public const string Version = "2.3.0";
    public const int DefaultPort = 47800;

    public int Port = DefaultPort;
    public string DataDir;
    public string ExeDir = AppContext.BaseDirectory;
    public JsonObject Cfg;

    static readonly JsonObject Defaults = new()
    {
        ["port"] = DefaultPort,
        ["token"] = "",
        ["toasts"] = true,
        ["keep_finished"] = 60,
        ["log_lines"] = 400,
        ["panel_width"] = 480,
        ["panel_height"] = 760,
        ["panel_autohide"] = true,
        ["panel_topmost"] = true,
        ["toast_controls"] = true,
        ["progress_toasts"] = false,
        ["language"] = "auto",
        ["toast_seconds"] = 4,              // on-screen time of a plain toast (then it stays in the notification center)
        ["toast_seconds_interactive"] = 8,  // toasts with buttons / inputs
        ["toasts_when_panel_open"] = false, // false: no popup banners while the panel is open
    };

    string cfgPath;

    /// <summary>Persist the current config (used when settings change from the UI).</summary>
    public void SaveCfg()
    {
        try { File.WriteAllText(cfgPath, Cfg.ToJsonString(J.Pretty)); }
        catch (Exception e) { Log.W("config save failed: " + e.Message); }
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
        s.Cfg = cfg;
        s.Port = port ?? (J.Num(cfg["port"]) is double cp ? (int)cp : DefaultPort);
        return s;
    }

    public string Token => J.Str(Cfg["token"]);
    public bool Bool(string k, bool def = false) => Cfg.ContainsKey(k) ? J.Truthy(Cfg[k]) : def;
    public int Int(string k, int def) => J.Num(Cfg[k]) is double d ? (int)d : def;
    public string Url => $"http://127.0.0.1:{Port}";
    public string File_(string name) => System.IO.Path.Combine(DataDir, name);
}
