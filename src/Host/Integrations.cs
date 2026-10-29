using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// One-click AI-agent notifications: TaskHub adds (or removes) exactly its own hook entries in
///   Claude Code: ~/.claude/settings.json  (two HTTP hooks -> POST /api/hooks/claude)
///   Codex:       ~/.codex/hooks.json      (two command hooks -> taskhub.exe hook codex)
/// Everything else in those files is kept. Before the first change the original file is copied to *.taskhub.bak.
/// Unchanged entries are never rewritten (Codex trusts hooks by their definition, so churn would ask again).
/// </summary>
static class Integrations
{
    public enum Agent { Claude, Codex }
    public static readonly Agent[] All = { Agent.Claude, Agent.Codex };

    static string Home => Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 } h
        ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);   // tests use a temp home
    public static string FileOf(Agent a) => a == Agent.Claude
        ? Path.Combine(Home, ".claude", "settings.json")
        : Path.Combine(Home, ".codex", "hooks.json");
    public static string CfgKey(Agent a) => a == Agent.Claude ? "integration_claude" : "integration_codex";
    public static string Name(Agent a) => a == Agent.Claude ? "Claude Code" : "Codex";

    /// <summary>The agent seems to be used on this PC (its config folder or executable exists).</summary>
    public static bool Detected(Agent a)
    {
        try
        {
            return a == Agent.Claude
                ? Directory.Exists(Path.Combine(Home, ".claude")) || OnPath("claude")
                : Directory.Exists(Path.Combine(Home, ".codex")) || OnPath("codex");
        }
        catch { return false; }
    }

    static bool OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(d =>
    {
        try { return d.Trim().Length > 0 && (File.Exists(Path.Combine(d.Trim(), exe + ".exe")) || File.Exists(Path.Combine(d.Trim(), exe + ".cmd"))); }
        catch { return false; }
    });

    // ------------------------------------------------------------ desired entries
    static List<(string ev, string matcher, JsonObject handler)> Desired(Agent a, Settings s)
    {
        if (a == Agent.Claude)
        {
            var headers = new JsonObject { ["X-TaskHub"] = "1" };
            if (!string.IsNullOrEmpty(s.Token)) headers["Authorization"] = "Bearer " + s.Token;
            JsonObject H() => new()
            {
                ["type"] = "http", ["url"] = $"http://127.0.0.1:{s.Port}/api/hooks/claude",
                ["headers"] = headers.DeepClone(), ["timeout"] = 5,
            };
            return new() { ("Stop", null, H()), ("Notification", "permission_prompt|elicitation_dialog", H()) };
        }
        var cmd = Quote(CliPath()) + " hook codex";
        JsonObject C() => new() { ["type"] = "command", ["command"] = cmd, ["timeout"] = 10 };
        return new() { ("Stop", null, C()), ("PermissionRequest", null, C()) };
    }

    static string CliPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "taskhub.exe");
        if (!p.Contains(' ')) return p;
        var sb = new StringBuilder(1024);   // 8.3 name: no quoting problems in whatever shell the agent uses
        return GetShortPathName(p, sb, sb.Capacity) > 0 ? sb.ToString() : p;
    }

    static string Quote(string p) => p.Contains(' ') ? "\"" + p + "\"" : p;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetShortPathName(string longPath, StringBuilder shortPath, int size);

    static bool IsOurs(Agent a, JsonObject h)
    {
        if (a == Agent.Claude)
            return J.Str(h["type"]) == "http" && J.Str(h["url"]).Contains("/api/hooks/claude");
        var c = J.Str(h["command"]);
        return J.Str(h["type"]) == "command" && c.Contains("hook codex") && c.Contains("taskhub", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ state
    /// <summary>Our entries currently in the file (event, matcher, handler JSON), in file order.</summary>
    static List<string> Current(Agent a, JsonObject root)
    {
        var r = new List<string>();
        if (root["hooks"] is not JsonObject hooks) return r;
        foreach (var (ev, arr) in hooks)
            foreach (var g in (arr as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                foreach (var h in (g["hooks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (IsOurs(a, h)) r.Add(Sig(ev, J.Str(g["matcher"]), h));
        return r;
    }

    static string Sig(string ev, string matcher, JsonObject h) => ev + "|" + (matcher ?? "") + "|" + h.ToJsonString();

    public static bool Enabled(Agent a)
    {
        try { return Current(a, Load(FileOf(a))).Count > 0; } catch { return false; }
    }

    /// <summary>Add (on) or remove (off) TaskHub's hooks. Returns true if the file was changed.</summary>
    public static bool Apply(Agent a, bool on, Settings s)
    {
        var path = FileOf(a);
        if (!on && !File.Exists(path)) return false;
        var root = Load(path);
        var want = on ? Desired(a, s) : new();
        var have = Current(a, root);
        if (have.SequenceEqual(want.Select(w => Sig(w.ev, w.matcher, w.handler)))) return false;   // already exactly right

        Remove(a, root);
        if (on)
        {
            if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = new JsonObject();
            foreach (var (ev, matcher, handler) in want)
            {
                if (hooks[ev] is not JsonArray arr) hooks[ev] = arr = new JsonArray();
                var g = new JsonObject();
                if (matcher != null) g["matcher"] = matcher;
                g["hooks"] = new JsonArray(handler);
                arr.Add(g);
            }
        }
        Save(path, root);
        Log.W($"integration {Name(a)}: {(on ? "enabled" : "disabled")} ({path})");
        return true;
    }

    static void Remove(Agent a, JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var ev in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[ev] is not JsonArray arr) continue;
            foreach (var g in arr.OfType<JsonObject>().ToList())
            {
                if (g["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs.OfType<JsonObject>().Where(h => IsOurs(a, h)).ToList()) hs.Remove(h);
                if (hs.Count == 0) arr.Remove(g);
            }
            if (arr.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    // ------------------------------------------------------------ file io
    static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var n = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return n as JsonObject ?? throw new InvalidDataException($"{path}: not a JSON object");
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bak = path + ".taskhub.bak";
        if (File.Exists(path) && !File.Exists(bak)) File.Copy(path, bak);   // the version from before TaskHub touched it
        var tmp = path + ".taskhub.tmp";
        File.WriteAllText(tmp, root.ToJsonString(Pretty) + "\n", new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    // ------------------------------------------------------------ startup / install helpers
    /// <summary>Installed hub on start: keep enabled integrations correct (port or install folder may have changed).</summary>
    public static void Ensure(Settings s)
    {
        foreach (var a in All)
            if (s.Cfg[CfgKey(a)] is JsonValue v && v.TryGetValue<bool>(out var on) && on)
                try { Apply(a, true, s); } catch (Exception e) { Log.W($"integration {Name(a)}: {e.Message}"); }
    }

    /// <summary>Turn on/off, remember the choice. Returns an error message or null.</summary>
    public static string Set(Agent a, bool on, Settings s)
    {
        try
        {
            Apply(a, on, s);
            s.Cfg[CfgKey(a)] = on;
            s.SaveCfg();
            return null;
        }
        catch (Exception e)
        {
            Log.W($"integration {Name(a)}: {e}");
            return L.T("int_error", Name(a), FileOf(a)) + "\n" + e.Message;
        }
    }
}
