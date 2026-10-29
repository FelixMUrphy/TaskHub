using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskHubCli;

sealed class HubError : Exception
{
    public int Code;
    public HubError(int code, string msg) : base(msg) { Code = code; }
}

/// <summary>Tiny HTTP client for the TaskHub API (no dependencies).</summary>
sealed class Client
{
    public readonly string Url;
    readonly string token;
    readonly HttpClient http;

    public Client(string url)
    {
        Url = (url ?? DefaultUrl()).TrimEnd('/');
        token = Environment.GetEnvironmentVariable("TASKHUB_TOKEN");
        // the agents' MCP server and hooks run this exe without TASKHUB_TOKEN: use the hub's own token
        if (string.IsNullOrEmpty(token) && HubConfig()?["token"] is JsonValue tv && tv.TryGetValue<string>(out var t)) token = t;
        http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>TASKHUB_URL, else the hub's port as the hub itself picks it: TaskHubHost.settings.json next to this exe,
    /// then "port" in its config.json, else 47800.</summary>
    public static string DefaultUrl()
    {
        var env = Environment.GetEnvironmentVariable("TASKHUB_URL");
        if (!string.IsNullOrEmpty(env)) return env;
        foreach (var o in new[] { ExeSettings(), HubConfig() })
            if (o?["port"] is JsonValue pv && pv.TryGetValue<double>(out var port) && port is > 0 and < 65536)
                return "http://127.0.0.1:" + (int)port;
        return "http://127.0.0.1:47800";
    }

    static JsonObject ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch { return null; }
    }

    /// <summary>TaskHubHost.settings.json next to this exe (installed next to the hub): {"port", "data"}.</summary>
    static JsonObject ExeSettings() => ReadJson(Path.Combine(AppContext.BaseDirectory, "TaskHubHost.settings.json"));

    /// <summary>The hub's config.json (data folder from TaskHubHost.settings.json, else %APPDATA%\TaskHub).</summary>
    static JsonObject HubConfig()
    {
        var data = ExeSettings()?["data"] is JsonValue dv && dv.TryGetValue<string>(out var d) ? Environment.ExpandEnvironmentVariables(d)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskHub");
        return ReadJson(Path.Combine(data, "config.json"));
    }

    public JsonNode Call(string method, string path, JsonNode body = null, double timeoutSec = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        var req = new HttpRequestMessage(new HttpMethod(method), Url + path);
        if (method != "GET")
        {
            req.Headers.Add("X-TaskHub", "1");
            req.Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrEmpty(token)) req.Headers.Add("Authorization", "Bearer " + token);
        HttpResponseMessage r;
        try { r = http.Send(req, cts.Token); }
        catch (Exception e) { throw new HubError(0, $"hub not reachable at {Url}: {e.GetBaseException().Message}"); }
        using (r)
        {
            var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JsonNode n = null;
            if (r.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
            {
                try { n = JsonNode.Parse(text); } catch { }
            }
            else n = JsonValue.Create(text);
            if (!r.IsSuccessStatusCode)
                throw new HubError((int)r.StatusCode, Program.S((n as JsonObject)?["error"]) is { Length: > 0 } err ? err : text);
            return n;
        }
    }

    public bool Available()
    {
        try { return Program.S((Call("GET", "/api/health", null, 2) as JsonObject)?["app"]) == "TaskHub"; }
        catch { return false; }
    }
}

static class Program
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "";

    const string Help = @"TaskHub CLI - talks to the TaskHub hub over HTTP (default http://127.0.0.1:47800, env TASKHUB_URL / TASKHUB_TOKEN)

  taskhub health
  taskhub notify ""Title"" [""message""] [--level info|warn|error] [--task ID]
  taskhub list [--all] [--json]
  taskhub show ID [--json]
  taskhub register --name NAME [--id ID] [--pid N] [--total N] [--unit U] [--source S] [--heartbeat SEC] [--json '{...}'|@file]   (prints id)
  taskhub update ID [--percent P] [--current N] [--total N] [--status S] [--stage S] [--log L] [--level L] [--toast] [--json '{...}'|@file]
  taskhub log ID ""line"" [--level info|warn|error] [--toast]
  taskhub finish ID [--state done|failed|cancelled] [--message M]
  taskhub control ID CONTROL [--value V]           (V is parsed as JSON when possible)
  taskhub commands ID [--wait SEC]                 (pending signals for a task, JSON)
  taskhub ack ID CMD [--fail] [--message M]
  taskhub wait ID [--timeout SEC]                  (block until finished; exit 0 done, 1 failed/lost, 2 cancelled, 124 timeout)
  taskhub panel [ID]                               (open the panel, optionally focused on a task)
  taskhub delete ID [--force]
  taskhub api                                      (print API.md served by the hub)
  taskhub mcp [--source NAME]                      (MCP server over stdio for AI agents: notify, task_start/update/finish,
                                                    task_commands, task_list; starts the hub on first use)
  taskhub hook claude|codex                        (agent hook: event JSON on stdin or as the last argument -> toast;
                                                    prints nothing, always exits 0, never starts the hub)
  taskhub run [--name N] [--id ID] [--source S] [--description D] [--cwd DIR] [--heartbeat SEC]
              [--auto-progress] [--progress-regex RX] [--status-regex RX] [--milestones 25,50,75]
              [--progress-toast] [--toast-controls] [--encoding utf8|oem|ansi] [--quiet] -- COMMAND [ARGS...]
      Runs COMMAND, echoes its output, streams it into the task log, parses progress, and understands
      these directives printed by the command on their own line:
        ::progress 5/20   ::progress 5   ::percent 40   ::total 20   ::status text   ::stage text
        ::log text   ::warn text   ::error text   ::toast text   ::metric key=value   ::widget id=<json|text>
      Adds Ctrl+C, 'Send input' (stdin) and Kill controls; exits with the command's exit code.
      The child gets TASKHUB_URL and TASKHUB_TASK_ID in its environment (so it can call the API itself).

Global option: --url URL";

    static int Main(string[] argv)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        var args = argv.ToList();
        string url = TakeOpt(args, "--url");
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help" or "/?") { Console.WriteLine(Help); return 0; }
        var cmd = args[0];
        args.RemoveAt(0);
        var c = new Client(url);
        if (cmd == "hook") return Hook(c, args);
        if (cmd == "mcp") return Mcp.Run(c, args, () => EnsureHub(c));
        EnsureHub(c);
        try
        {
            switch (cmd)
            {
                case "health": Print(c.Call("GET", "/api/health")); return 0;
                case "api": Console.WriteLine(S(c.Call("GET", "/api/help"))); return 0;
                case "notify":
                {
                    var level = TakeOpt(args, "--level") ?? "info";
                    var task = TakeOpt(args, "--task");
                    if (args.Count < 1) return Fail("notify: title required");
                    c.Call("POST", "/api/notify", new JsonObject
                    {
                        ["title"] = args[0], ["message"] = args.Count > 1 ? args[1] : "", ["level"] = level, ["task"] = task,
                    });
                    return 0;
                }
                case "list":
                {
                    bool all = TakeFlag(args, "--all"), json = TakeFlag(args, "--json");
                    var snap = c.Call("GET", "/api/tasks?brief=1" + (all ? "" : "&active=1")) as JsonObject;
                    if (json) { Print(snap); return 0; }
                    var tasks = snap?["tasks"] as JsonArray ?? new JsonArray();
                    if (tasks.Count == 0) Console.WriteLine(all ? "no tasks" : "no active tasks");
                    foreach (var t in tasks.OfType<JsonObject>())
                    {
                        var p = t["progress"] != null ? $"{Num(t["progress"]) * 100,5:0.0}%" : "     -";
                        Console.WriteLine($"{S(t["id"]),-14} {S(t["state"]),-9} {p}  {S(t["name"])}" +
                                          (S(t["status"]).Length > 0 ? "  · " + S(t["status"]) : ""));
                    }
                    return 0;
                }
                case "show":
                {
                    bool json = TakeFlag(args, "--json");
                    if (args.Count < 1) return Fail("show: id required");
                    var t = c.Call("GET", "/api/tasks/" + args[0]) as JsonObject;
                    if (json) { Print(t); return 0; }
                    Console.WriteLine($"{S(t["name"])}  [{S(t["id"])}]  {S(t["state"])}");
                    if (t["progress"] != null) Console.WriteLine($"progress: {Num(t["progress"]) * 100:0.0}%");
                    if (S(t["status"]).Length > 0) Console.WriteLine("status:   " + S(t["status"]));
                    if (S(t["message"]).Length > 0) Console.WriteLine("message:  " + S(t["message"]));
                    var ctls = (t["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(x => $"{S(x["id"])} ({S(x["label"])})");
                    Console.WriteLine("controls: " + string.Join(", ", ctls));
                    foreach (var l in (t["log"] as JsonArray ?? new JsonArray()).TakeLast(15).OfType<JsonObject>())
                        Console.WriteLine("  " + DateTimeOffset.FromUnixTimeMilliseconds((long)(Num(l["t"]) * 1000)).ToLocalTime().ToString("HH:mm:ss") + "  " + S(l["msg"]));
                    return 0;
                }
                case "register":
                {
                    var b = JsonOpt(args) ?? new JsonObject();
                    SetOpt(b, "name", TakeOpt(args, "--name"));
                    SetOpt(b, "id", TakeOpt(args, "--id"));
                    SetOpt(b, "unit", TakeOpt(args, "--unit"));
                    SetOpt(b, "description", TakeOpt(args, "--description"));
                    var src = TakeOpt(args, "--source");
                    if (src != null) b["source"] = src;
                    else if (b["source"] == null) b["source"] = Environment.GetEnvironmentVariable("TASKHUB_SOURCE") ?? "cli";
                    SetNum(b, "pid", TakeOpt(args, "--pid"));
                    SetNum(b, "total", TakeOpt(args, "--total"));
                    SetNum(b, "heartbeat_timeout", TakeOpt(args, "--heartbeat"));
                    if (b["name"] == null) return Fail("register: --name required");
                    var r = c.Call("POST", "/api/tasks", b);
                    Console.WriteLine(S(r?["id"]));
                    return 0;
                }
                case "update":
                {
                    var b = JsonOpt(args) ?? new JsonObject();
                    SetNum(b, "percent", TakeOpt(args, "--percent"));
                    SetNum(b, "current", TakeOpt(args, "--current"));
                    SetNum(b, "total", TakeOpt(args, "--total"));
                    SetOpt(b, "status", TakeOpt(args, "--status"));
                    SetOpt(b, "stage", TakeOpt(args, "--stage"));
                    SetOpt(b, "log", TakeOpt(args, "--log"));
                    SetOpt(b, "level", TakeOpt(args, "--level"));
                    if (TakeFlag(args, "--toast")) b["toast"] = true;
                    if (args.Count < 1) return Fail("update: id required");
                    c.Call("POST", $"/api/tasks/{args[0]}/update", b);
                    return 0;
                }
                case "log":
                {
                    var level = TakeOpt(args, "--level") ?? "info";
                    bool toast = TakeFlag(args, "--toast");
                    if (args.Count < 2) return Fail("log: id and text required");
                    c.Call("POST", $"/api/tasks/{args[0]}/log", new JsonObject { ["msg"] = string.Join(" ", args.Skip(1)), ["level"] = level, ["toast"] = toast });
                    return 0;
                }
                case "finish":
                {
                    var state = TakeOpt(args, "--state") ?? "done";
                    var msg = TakeOpt(args, "--message") ?? "";
                    if (args.Count < 1) return Fail("finish: id required");
                    c.Call("POST", $"/api/tasks/{args[0]}/finish", new JsonObject { ["state"] = state, ["message"] = msg });
                    return 0;
                }
                case "control":
                {
                    var v = TakeOpt(args, "--value");
                    if (args.Count < 2) return Fail("control: id and control id required");
                    var b = new JsonObject();
                    if (v != null) b["value"] = ParseValue(v);
                    Print(c.Call("POST", $"/api/tasks/{args[0]}/controls/{args[1]}", b, 30));
                    return 0;
                }
                case "commands":
                {
                    var w = TakeOpt(args, "--wait") ?? "0";
                    if (args.Count < 1) return Fail("commands: id required");
                    Print(c.Call("GET", $"/api/tasks/{args[0]}/commands?wait={w}", null, 70));
                    return 0;
                }
                case "ack":
                {
                    bool fail = TakeFlag(args, "--fail");
                    var msg = TakeOpt(args, "--message") ?? "";
                    if (args.Count < 2) return Fail("ack: id and command id required");
                    c.Call("POST", $"/api/tasks/{args[0]}/commands/{args[1]}/ack", new JsonObject { ["ok"] = !fail, ["message"] = msg });
                    return 0;
                }
                case "wait": return Wait(c, args);
                case "panel":
                    try { AllowSetForegroundWindow(-1); } catch { }
                    c.Call("POST", "/api/ui/open", new JsonObject { ["task"] = args.Count > 0 ? args[0] : null });
                    return 0;
                case "delete":
                {
                    bool force = TakeFlag(args, "--force");
                    if (args.Count < 1) return Fail("delete: id required");
                    c.Call("DELETE", $"/api/tasks/{args[0]}" + (force ? "?force=1" : ""));
                    return 0;
                }
                case "run": return Runner.Run(c, args);
                default: return Fail($"unknown command '{cmd}' (taskhub --help)");
            }
        }
        catch (HubError e)
        {
            Console.Error.WriteLine("taskhub: " + e.Message);
            return e.Code == 0 ? 3 : 1;
        }
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    /// <summary>Forward an agent hook event to POST /api/hooks/{agent}. Fail-soft and silent: an agent hook must never
    /// block or slow down the agent, so no hub autostart, short timeouts, no output, exit code 0.</summary>
    static int Hook(Client c, List<string> args)
    {
        try
        {
            var agent = args.Count > 0 ? args[0] : "";
            if (agent is not ("claude" or "codex")) return 0;
            string text = null;
            if (args.Count > 1 && args[^1].TrimStart().StartsWith("{")) text = args[^1];   // Codex `notify`: JSON as last argument
            else if (Console.IsInputRedirected)
            {
                var read = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)).ReadToEndAsync();   // agents send UTF-8
                if (read.Wait(TimeSpan.FromSeconds(3))) text = read.Result;
            }
            if (string.IsNullOrWhiteSpace(text)) return 0;
            if (JsonNode.Parse(text) is JsonObject body) c.Call("POST", "/api/hooks/" + agent, body, 3);
        }
        catch { }
        return 0;
    }

    /// <summary>If the local hub is not running, start it (TaskHubHost.exe next to this CLI or the installed copy) and wait for it.
    /// Disable with TASKHUB_NO_AUTOSTART=1.</summary>
    static void EnsureHub(Client c)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TASKHUB_NO_AUTOSTART"))) return;
        if (!(c.Url.Contains("://127.0.0.1") || c.Url.Contains("://localhost"))) return;
        if (c.Available()) return;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "TaskHubHost.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TaskHub", "TaskHubHost.exe"),
        };
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--run") { UseShellExecute = true })?.Dispose();
            for (int i = 0; i < 30 && !c.Available(); i++) Thread.Sleep(500);
        }
        catch { }
    }

    static int Wait(Client c, List<string> args)
    {
        var to = TakeOpt(args, "--timeout");
        if (args.Count < 1) return Fail("wait: id required");
        var deadline = to != null ? DateTime.UtcNow.AddSeconds(double.Parse(to, CultureInfo.InvariantCulture)) : DateTime.MaxValue;
        long ver = 0;
        while (true)
        {
            var wait = Math.Clamp((deadline - DateTime.UtcNow).TotalSeconds, 0, 25);   // don't wait past --timeout
            var snap = c.Call("GET", $"/api/tasks?brief=1&since={ver}&wait={wait.ToString("0.###", CultureInfo.InvariantCulture)}", null, 70) as JsonObject;
            ver = (long)Num(snap["version"]);
            var t = (snap["tasks"] as JsonArray).OfType<JsonObject>().FirstOrDefault(x => S(x["id"]) == args[0]);
            if (t == null) return Fail($"task {args[0]} not found");
            var st = S(t["state"]);
            if (st is "done" or "failed" or "lost" or "cancelled")
            {
                Console.WriteLine($"{st}: {S(t["message"])}");
                return st == "done" ? 0 : st == "cancelled" ? 2 : 1;
            }
            if (DateTime.UtcNow > deadline) { Console.WriteLine("timeout; state " + st); return 124; }
        }
    }

    // ------------------------------------------------------------ arg helpers
    public static string TakeOpt(List<string> a, string name)
    {
        int stop = a.IndexOf("--");
        for (int i = 0; i < (stop < 0 ? a.Count : stop); i++)
        {
            if (a[i] == name && i + 1 < a.Count) { var v = a[i + 1]; a.RemoveRange(i, 2); return v; }
            if (a[i].StartsWith(name + "=")) { var v = a[i][(name.Length + 1)..]; a.RemoveAt(i); return v; }
        }
        return null;
    }

    public static bool TakeFlag(List<string> a, string name)
    {
        int stop = a.IndexOf("--");
        int i = a.IndexOf(name);
        if (i < 0 || (stop >= 0 && i > stop)) return false;
        a.RemoveAt(i);
        return true;
    }

    static JsonObject JsonOpt(List<string> a)
    {
        var j = TakeOpt(a, "--json");
        if (j == null) return null;
        if (j.StartsWith("@")) j = File.ReadAllText(j[1..]);
        return JsonNode.Parse(j) as JsonObject ?? throw new HubError(400, "--json must be a JSON object");
    }

    static void SetOpt(JsonObject b, string k, string v) { if (v != null) b[k] = v; }
    static void SetNum(JsonObject b, string k, string v)
    {
        if (v == null) return;
        b[k] = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new HubError(400, $"{k} must be a number");
    }

    public static JsonNode ParseValue(string v)
    {
        try { return JsonNode.Parse(v); } catch { return JsonValue.Create(v); }
    }

    public static string S(JsonNode n)
    {
        if (n == null) return "";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();
        }
        return n.ToJsonString();
    }

    public static double Num(JsonNode n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
            if (v.TryGetValue<double>(out var d)) return d;
        }
        return 0;
    }

    static void Print(JsonNode n) => Console.WriteLine(n?.ToJsonString(Pretty) ?? "null");
    static int Fail(string m) { Console.Error.WriteLine("taskhub: " + m); return 2; }
}
