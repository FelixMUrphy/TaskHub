using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskHubCli;

/// <summary>
/// `taskhub mcp [--source NAME]` - a stdio MCP server (JSON-RPC 2.0, one message per line) that gives AI agents
/// (Claude Code, Codex, ...) TaskHub tools: notify, task_start / task_update / task_finish, task_commands, task_list.
/// It is a thin adapter over the local HTTP API; the hub is started on the first tool call if it is not running.
/// stdout carries protocol messages only.
/// </summary>
static class Mcp
{
    const string Proto = "2025-06-18";

    const string Instructions =
        "TaskHub shows the user's background jobs in a Windows tray panel with notifications. " +
        "Use it for work that runs longer than about a minute (builds, tests, training, renders, exports, long scripts): " +
        "task_start when it begins, task_update with progress/status as it goes (at most every few seconds), task_finish when it ends " +
        "(the user gets a notification on finish/failure automatically). Use notify for a one-off message the user should see. " +
        "Do NOT add intermediate notifications (milestones, toast=true) unless the user asks for them. " +
        "Add controls (buttons/inputs) only when the user may want to steer the job; read their clicks with task_commands. " +
        "If TaskHub is unavailable, continue the user's work without it.";

    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(Client c, List<string> args, Action ensureHub)
    {
        var source = Program.TakeOpt(args, "--source") ?? Environment.GetEnvironmentVariable("TASKHUB_SOURCE") ?? "agent";
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        bool hubChecked = false;
        string line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject req;
            try { req = JsonNode.Parse(line) as JsonObject; }
            catch { Send(stdout, Error(null, -32700, "parse error")); continue; }
            if (req == null) continue;
            var id = req["id"]?.DeepClone();
            var method = Program.S(req["method"]);
            if (id == null) continue;   // notifications (initialized, cancelled, ...) need no answer
            try
            {
                JsonNode result = method switch
                {
                    "initialize" => new JsonObject
                    {
                        ["protocolVersion"] = Program.S(req["params"]?["protocolVersion"]) is { Length: > 0 } pv ? pv : Proto,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "taskhub", ["title"] = "TaskHub", ["version"] = Program.Version },
                        ["instructions"] = Instructions,
                    },
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject { ["tools"] = Tools() },
                    "tools/call" => Call(c, req["params"] as JsonObject ?? new JsonObject(), source, () =>
                    {
                        if (!hubChecked) { hubChecked = true; ensureHub(); }
                    }),
                    _ => throw new McpError(-32601, "method not found: " + method),
                };
                Send(stdout, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (McpError e) { Send(stdout, Error(id, e.Code, e.Message)); }
            catch (Exception e) { Send(stdout, Error(id, -32603, e.Message)); }
        }
        return 0;
    }

    sealed class McpError : Exception { public int Code; public McpError(int c, string m) : base(m) { Code = c; } }

    static JsonObject Error(JsonNode id, int code, string msg) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = msg } };

    static void Send(StreamWriter w, JsonObject o) => w.WriteLine(o.ToJsonString(Compact));

    // ------------------------------------------------------------ tools
    static JsonObject Obj(params (string k, JsonNode v)[] p) { var o = new JsonObject(); foreach (var (k, v) in p) o[k] = v; return o; }
    static JsonObject Str(string d) => Obj(("type", "string"), ("description", d));
    static JsonObject Num(string d) => Obj(("type", "number"), ("description", d));
    static JsonObject Tool(string name, string title, string desc, JsonObject props, params string[] required) => Obj(
        ("name", name), ("title", title), ("description", desc),
        ("inputSchema", Obj(("type", "object"), ("properties", props), ("required", new JsonArray(required.Select(r => (JsonNode)r).ToArray())))));

    static JsonArray Tools() => new()
    {
        Tool("notify", "Show a notification",
            "Show a one-off Windows notification to the user (e.g. something needs their attention). Not needed for task finish - task_finish notifies.",
            Obj(("title", Str("Short title")), ("message", Str("One or two sentences")),
                ("level", Obj(("type", "string"), ("enum", new JsonArray("info", "warn", "error"))))),
            "title"),
        Tool("task_start", "Start a task",
            "Register a long-running job in the TaskHub panel. Returns its id. Call task_finish when it ends.",
            Obj(("name", Str("What is being done, e.g. 'Run test suite'")),
                ("description", Str("Optional details")),
                ("total", Num("Optional number of steps/items for progress")),
                ("unit", Str("Unit of total, e.g. 'tests', 'files'")),
                ("heartbeat_timeout", Num("Seconds without task_update after which the task is shown as stalled (0 = off)")),
                ("controls", Obj(("type", "array"), ("items", Obj(("type", "object"))),
                    ("description", "Optional buttons the user can press, e.g. [{\"id\":\"stop\",\"label\":\"Stop\"}] or with an input " +
                        "{\"id\":\"lr\",\"label\":\"Set LR\",\"input\":{\"type\":\"number\",\"default\":0.001}}. Read presses with task_commands."))),
                ("widgets", Obj(("type", "array"), ("items", Obj(("type", "object"))),
                    ("description", "Optional widgets: {\"id\",\"type\":chart|gauge|stat|led|kv|table|text|progress,\"label\"}; values via task_update.widgets")))),
            "name"),
        Tool("task_update", "Update a task",
            "Report progress/status of a task. Send at most every few seconds.",
            Obj(("id", Str("Task id from task_start")),
                ("current", Num("Steps done (with total)")), ("total", Num("Total steps")), ("percent", Num("0..100")),
                ("status", Str("Current status line")), ("stage", Str("Current stage")),
                ("log", Str("Line to append to the task log")),
                ("widgets", Obj(("type", "object"), ("description", "Widget values {widget_id: value}")))),
            "id"),
        Tool("task_finish", "Finish a task",
            "Mark a task done, failed or cancelled; the user gets a notification.",
            Obj(("id", Str("Task id")),
                ("state", Obj(("type", "string"), ("enum", new JsonArray("done", "failed", "cancelled")))),
                ("message", Str("Result summary"))),
            "id"),
        Tool("task_commands", "Read button presses",
            "Get the user's pending button presses / input values for a task (they are acknowledged as received). " +
            "Optionally wait up to wait_seconds for the next one.",
            Obj(("id", Str("Task id")), ("wait_seconds", Num("0..50, default 0"))),
            "id"),
        Tool("task_list", "List tasks",
            "List tasks in TaskHub (active ones by default).",
            Obj(("all", Obj(("type", "boolean"), ("description", "Include finished tasks"))))),
    };

    static JsonObject Text(string t, bool error = false)
    {
        var r = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = t }) };
        if (error) r["isError"] = true;
        return r;
    }

    static JsonObject Call(Client c, JsonObject p, string source, Action ensureHub)
    {
        var name = Program.S(p["name"]);
        var a = p["arguments"] as JsonObject ?? new JsonObject();
        string Id() => Program.S(a["id"]) is { Length: > 0 } s ? s : throw new McpError(-32602, "id is required");
        try
        {
            ensureHub();
            switch (name)
            {
                case "notify":
                    c.Call("POST", "/api/notify", new JsonObject
                    {
                        ["title"] = a["title"]?.DeepClone(), ["message"] = a["message"]?.DeepClone() ?? "", ["level"] = a["level"]?.DeepClone() ?? "info",
                    });
                    return Text("shown");
                case "task_start":
                {
                    var b = new JsonObject { ["source"] = source };
                    foreach (var k in new[] { "name", "description", "total", "unit", "heartbeat_timeout", "controls", "widgets" })
                        if (a[k] != null) b[k] = a[k].DeepClone();
                    b["notify"] = new JsonObject { ["milestones"] = new JsonArray() };
                    var r = c.Call("POST", "/api/tasks", b);
                    return Text(Program.S(r?["id"]));
                }
                case "task_update":
                {
                    var b = new JsonObject();
                    foreach (var k in new[] { "current", "total", "percent", "status", "stage", "log", "widgets" })
                        if (a[k] != null) b[k] = a[k].DeepClone();
                    c.Call("POST", $"/api/tasks/{Id()}/update", b);
                    return Text("ok");
                }
                case "task_finish":
                    c.Call("POST", $"/api/tasks/{Id()}/finish", new JsonObject
                    {
                        ["state"] = a["state"]?.DeepClone() ?? "done", ["message"] = a["message"]?.DeepClone() ?? "",
                    });
                    return Text("finished");
                case "task_commands":
                {
                    var tid = Id();
                    var wait = Math.Clamp(Program.Num(a["wait_seconds"]), 0, 50);
                    var r = c.Call("GET", $"/api/tasks/{tid}/commands?wait={wait.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null, wait + 15) as JsonObject;
                    var cmds = r?["commands"] as JsonArray ?? new JsonArray();
                    var outp = new JsonArray();
                    foreach (var cmd in cmds.OfType<JsonObject>())
                    {
                        outp.Add(new JsonObject { ["control"] = cmd["control"]?.DeepClone(), ["value"] = cmd["value"]?.DeepClone() });
                        try { c.Call("POST", $"/api/tasks/{tid}/commands/{Program.S(cmd["id"])}/ack", new JsonObject { ["ok"] = true, ["message"] = "received" }); } catch { }
                    }
                    return Text(outp.Count == 0 ? "no pending commands" : outp.ToJsonString(Compact));
                }
                case "task_list":
                {
                    bool all = a["all"] is JsonValue v && v.TryGetValue<bool>(out var x) && x;
                    var snap = c.Call("GET", "/api/tasks?brief=1" + (all ? "" : "&active=1")) as JsonObject;
                    var list = new JsonArray();
                    foreach (var t in (snap?["tasks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        list.Add(new JsonObject
                        {
                            ["id"] = t["id"]?.DeepClone(), ["name"] = t["name"]?.DeepClone(), ["state"] = t["state"]?.DeepClone(),
                            ["progress"] = t["progress"]?.DeepClone(), ["status"] = t["status"]?.DeepClone(),
                        });
                    return Text(list.ToJsonString(Compact));
                }
                default: throw new McpError(-32602, "unknown tool: " + name);
            }
        }
        catch (HubError e) { return Text("TaskHub: " + e.Message, true); }
    }
}
