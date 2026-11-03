using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>What the HTTP API refuses, with a 400 that says why, so that a mistake never looks like success. Only for requests
/// from outside: connectors and hooks call the operations themselves. Fields not known yet beside known ones are no error.</summary>
sealed partial class Hub
{
    static readonly HashSet<string> UpdateFields = new()
    {
        "total", "unit", "current", "percent", "progress", "status", "stage", "name", "description", "project", "attention", "metrics", "meta", "links",
        "expected_duration", "state", "controls", "add_controls", "remove_controls", "add_widgets", "remove_widgets", "widgets", "log", "level", "toast",
    };

    const int MaxWidgets = 40;

    static ApiError Bad(string why) => new(400, why);

    static void CheckNumbers(JsonObject b, params string[] keys)
    {
        foreach (var k in keys)
            if (b[k] != null && J.Num(b[k]) == null && !J.NonFinite(b[k]))
                throw Bad($"{k} must be a number, not {J.Cut(b[k].ToJsonString(), 40)}");
    }

    static void CheckObjects(JsonObject b, params string[] keys)
    {
        foreach (var k in keys)
            if (b[k] != null && b[k] is not JsonObject) throw Bad($"{k} must be an object");
    }

    static void CheckLists(JsonObject b, params string[] keys)
    {
        foreach (var k in keys)
            if (b[k] != null && b[k] is not JsonArray) throw Bad($"{k} must be a list");
    }

    static void CheckLevel(JsonObject b)
    {
        if (J.Truthy(b["level"]) && J.Str(b["level"]) is not ("info" or "warn" or "error"))
            throw Bad($"level must be info, warn or error, not {J.Cut(J.Str(b["level"]), 40)}");
    }

    public JsonObject RegisterApi(JsonObject b)
    {
        if (!J.IsStr(b["name"]) || J.Str(b["name"]).Trim().Length == 0) throw Bad("name is required: a text that isn't empty");
        CheckNumbers(b, "total", "heartbeat_timeout", "expected_duration");
        CheckObjects(b, "notify", "meta");
        CheckLists(b, "controls", "widgets", "links");
        return Register(b);
    }

    public JsonObject UpdateApi(string tid, JsonObject b)
    {
        if (b.Count > 0 && !b.Any(f => UpdateFields.Contains(f.Key)))
            throw Bad($"nothing to do: no known field in {J.Cut(string.Join(", ", b.Select(f => f.Key)), 80)}. Known: {string.Join(", ", UpdateFields)}");
        CheckNumbers(b, "total", "current", "percent", "progress", "expected_duration");
        CheckObjects(b, "metrics", "meta", "widgets");
        CheckLists(b, "controls", "add_controls", "remove_controls", "add_widgets", "remove_widgets", "links");
        CheckLevel(b);
        if (b["state"] != null && J.Str(b["state"]) is not ("running" or "paused"))
            throw Bad($"state can be running or paused here; POST /api/tasks/{tid}/finish ends a task");
        if (b["widgets"] is JsonObject || b["add_widgets"] != null)
            lock (Lock)
            {
                var known = (Get(tid)["widgets"] as JsonArray ?? new JsonArray()).Concat(b["add_widgets"] as JsonArray ?? new JsonArray())
                    .Select(w => J.Str((w as JsonObject)?["id"])).ToHashSet();
                if (b["add_widgets"] != null && known.Count > MaxWidgets) throw Bad($"a task has at most {MaxWidgets} widgets");
                foreach (var (id, _) in b["widgets"] as JsonObject ?? new JsonObject())
                    if (!known.Contains(id)) throw Bad($"unknown widget '{J.Cut(id, 64)}': declare it in widgets or add_widgets first");
            }
        return Update(tid, b);
    }

    public JsonObject FinishApi(string tid, JsonObject b)
    {
        if (J.Str(b["state"]) is not ("done" or "failed" or "cancelled")) throw Bad("state is required: done, failed or cancelled");
        // the first end stands: the runner's "exit code -1" after a Kill doesn't turn cancelled into failed. Only "lost" gives way, it is the hub's
        // guess and the process has reported after all. Connectors go past this: a stopped pod that gets deleted does change from "stopped" to "terminated".
        lock (Lock)
        {
            var st = St(Get(tid));
            if (IsFinal(st) && st != "lost") return new JsonObject { ["ok"] = true, ["state"] = st, ["already_finished"] = true };
        }
        return Finish(tid, b);
    }

    public static void CheckNotify(JsonObject b)
    {
        if (!J.Truthy(b["title"]) && !J.Truthy(b["message"]) && !J.Truthy(b["msg"])) throw Bad("title or message is required");
        CheckLevel(b);
    }
}
