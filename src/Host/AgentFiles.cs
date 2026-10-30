using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// The AI agents' settings files TaskHub edits and how it recognises its own entries in them. Shared by the hub
/// (Integrations: add / remove) and the CLI (StoreCleanup: remove after the Store edition was uninstalled), so it
/// depends on nothing else in either project.
/// </summary>
static class AgentFiles
{
    public static string Home => Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 } h
        ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);   // tests use a temp home

    public static string ClaudeHooks => Path.Combine(Home, ".claude", "settings.json");
    public static string ClaudeMcp => Path.Combine(Home, ".claude.json");
    public static string CodexHooks => Path.Combine(Home, ".codex", "hooks.json");
    public static string CodexMcp => Path.Combine(Home, ".codex", "config.toml");

    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    // ------------------------------------------------------------ hooks (JSON)
    public static bool IsOurHook(bool claude, JsonObject h)
    {
        if (claude) return Str(h["type"]) == "http" && Str(h["url"]).Contains("/api/hooks/claude");
        var c = Str(h["command"]);
        return Str(h["type"]) == "command" && c.Contains("hook codex") && c.Contains("taskhub", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Remove TaskHub's hooks from a parsed settings file, dropping groups and events left empty.</summary>
    public static void StripHooks(JsonObject root, bool claude)
    {
        if (root["hooks"] is not JsonObject old) return;
        foreach (var ev in old.Select(kv => kv.Key).ToList())
        {
            if (old[ev] is not JsonArray arr) continue;
            foreach (var g in arr.OfType<JsonObject>().ToList())
            {
                if (g["hooks"] is not JsonArray hs) continue;
                foreach (var h in hs.OfType<JsonObject>().Where(h => IsOurHook(claude, h)).ToList()) hs.Remove(h);
                if (hs.Count == 0) arr.Remove(g);
            }
            if (arr.Count == 0) old.Remove(ev);
        }
        if (old.Count == 0) root.Remove("hooks");
    }

    public static bool HasHooks(JsonObject root, bool claude) =>
        root["hooks"] is JsonObject hooks && hooks.Select(kv => kv.Value).OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>())
            .SelectMany(g => (g["hooks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()).Any(h => IsOurHook(claude, h));

    public static bool RemoveHooks(bool claude)
    {
        var path = claude ? ClaudeHooks : CodexHooks;
        if (!File.Exists(path)) return false;
        var root = LoadJson(path);
        if (!HasHooks(root, claude)) return false;
        StripHooks(root, claude);
        SaveJson(path, root);
        return true;
    }

    // ------------------------------------------------------------ MCP: Claude Code (~/.claude.json)
    public static bool IsOurMcp(JsonObject server) =>
        server != null && Str(server["command"]).Contains("taskhub", StringComparison.OrdinalIgnoreCase)
        && (server["args"] as JsonArray)?.Any(x => Str(x) == "mcp") == true;

    public static bool RemoveClaudeMcp()
    {
        if (!File.Exists(ClaudeMcp)) return false;
        var root = LoadJson(ClaudeMcp);
        var servers = root["mcpServers"] as JsonObject;
        var cur = servers?["taskhub"] as JsonObject;
        if (cur == null) return false;
        if (!IsOurMcp(cur)) throw new InvalidOperationException("an MCP server named 'taskhub' that TaskHub did not add already exists");
        servers.Remove("taskhub");
        SaveJson(ClaudeMcp, root);
        return true;
    }

    // ------------------------------------------------------------ MCP: Codex (~/.codex/config.toml)
    static readonly Regex TomlHeader = new(@"^\s*\[\s*mcp_servers\s*\.\s*(?:taskhub|""taskhub""|'taskhub')\s*(?:\.[^\]]*)?\]\s*(?:#.*)?$", RegexOptions.IgnoreCase);
    static readonly Regex AnyHeader = new(@"^\s*\[");

    /// <summary>Lines of the [mcp_servers.taskhub] table (+ its sub-tables): start index, count, whether TaskHub owns it.</summary>
    public static (int start, int count, bool exists, bool ours, List<string> lines) TomlBlock(List<string> lines)
    {
        int start = lines.FindIndex(l => TomlHeader.IsMatch(l));
        if (start < 0) return (-1, 0, false, false, new());
        int end = start + 1;
        while (end < lines.Count && (!AnyHeader.IsMatch(lines[end]) || TomlHeader.IsMatch(lines[end]))) end++;
        while (end > start + 1 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        var block = lines.GetRange(start, end - start);
        bool ours = block.Any(l => Regex.IsMatch(l, @"^\s*command\s*=.*taskhub", RegexOptions.IgnoreCase))
                    && block.Any(l => Regex.IsMatch(l, @"^\s*args\s*=.*""mcp"""));
        return (start, end - start, true, ours, block);
    }

    /// <summary>Replace TaskHub's table in config.toml with want (null: just remove it), keeping everything else verbatim.</summary>
    public static void WriteToml(List<string> lines, (int start, int count, bool exists) cur, List<string> want, string nl)
    {
        if (cur.exists)
        {
            lines.RemoveRange(cur.start, cur.count);
            if (cur.start < lines.Count && cur.start > 0 && string.IsNullOrWhiteSpace(lines[cur.start]) && string.IsNullOrWhiteSpace(lines[cur.start - 1]))
                lines.RemoveAt(cur.start);
        }
        if (want != null)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(want);
        }
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        Backup(CodexMcp);
        WriteAtomic(CodexMcp, string.Join(nl, lines) + nl);
    }

    public static string TomlNewline() => File.Exists(CodexMcp) && File.ReadAllText(CodexMcp, Encoding.UTF8).Contains("\r\n") ? "\r\n" : "\n";

    public static bool RemoveCodexMcp()
    {
        if (!File.Exists(CodexMcp)) return false;
        var lines = ReadText(CodexMcp);
        var (start, count, exists, ours, _) = TomlBlock(lines);
        if (!exists) return false;
        if (!ours) throw new InvalidOperationException("an MCP server [mcp_servers.taskhub] that TaskHub did not add already exists");
        WriteToml(lines, (start, count, true), null, TomlNewline());
        return true;
    }

    /// <summary>Every TaskHub entry in every agent file; the files' other content stays. Errors are collected, not thrown.</summary>
    public static List<string> RemoveAll()
    {
        var errors = new List<string>();
        foreach (var (name, remove) in new (string, Func<bool>)[]
                 {
                     (ClaudeHooks, () => RemoveHooks(true)), (CodexHooks, () => RemoveHooks(false)),
                     (ClaudeMcp, RemoveClaudeMcp), (CodexMcp, RemoveCodexMcp),
                 })
            try { remove(); } catch (Exception e) { errors.Add(name + ": " + e.Message); }
        return errors;
    }

    // ------------------------------------------------------------ file io
    public static List<string> ReadText(string path) =>
        File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n').ToList() : new List<string>();

    public static JsonObject LoadJson(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var n = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return n as JsonObject ?? throw new InvalidDataException($"{path}: not a JSON object");
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void SaveJson(string path, JsonObject root)
    {
        Backup(path);
        WriteAtomic(path, root.ToJsonString(Pretty) + "\n");
    }

    static void Backup(string path)
    {
        var bak = path + ".taskhub.bak";
        if (File.Exists(path) && !File.Exists(bak)) File.Copy(path, bak);   // the version from before TaskHub touched it
    }

    static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".taskhub.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}
