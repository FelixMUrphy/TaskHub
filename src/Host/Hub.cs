using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

sealed class ApiError : Exception
{
    public int Code;
    public ApiError(int code, string msg) : base(msg) { Code = code; }
}

interface INotifier
{
    /// <param name="task">snapshot of the task (may be null) - used for toast buttons</param>
    /// <param name="dismissOnly">clicking the toast only closes it (no panel) - for toasts not tied to a task</param>
    /// <param name="controls">false: no control buttons / inputs (plain toast)</param>
    void Toast(string title, string msg, string level, string taskId, JsonObject task, bool dismissOnly = false, bool controls = true);
    void Progress(JsonObject task);
    void ProgressEnd(string taskId);
    void Refresh(string taskId);   // a control value changed -> re-render shown toasts
}

/// <summary>Task registry - a port of the Python Hub class; tasks are JsonObjects with the same shape.</summary>
sealed class Hub
{
    public static readonly string[] Active = { "running", "paused", "stale" };
    public static readonly string[] Final = { "done", "failed", "cancelled", "lost" };
    public static readonly string[] AllStates = Active.Concat(Final).ToArray();
    static readonly string[] WidgetTypes = { "text", "progress", "gauge", "stat", "led", "kv", "table", "chart" };
    static readonly Regex IdRx = new(@"^[A-Za-z0-9_.-]{1,64}$");
    static readonly Regex CidRx = new(@"^[A-Za-z0-9_.:-]{1,48}$");

    public static bool IsActive(string s) => Array.IndexOf(Active, s) >= 0;
    public static bool IsFinal(string s) => Array.IndexOf(Final, s) >= 0;

    sealed class Internal
    {
        public List<JsonObject> CmdQ = new();
        public long? Ct;
        public double LastProgressToast;
    }

    readonly Settings S;
    public INotifier Notifier;
    public readonly object Lock = new();
    readonly Dictionary<string, JsonObject> tasks = new();
    readonly Dictionary<string, Internal> internals = new();
    public long Version = 1;
    bool dirty;
    public bool Muted;
    JsonObject focus = new() { ["task"] = null, ["seq"] = 0 };
    TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly string statePath;
    public event Action Changed;   // fired (thread pool) after every bump - tray refresh

    public Hub(Settings s)
    {
        S = s;
        statePath = s.File_("state.json");
        LoadState();
    }

    // ------------------------------------------------------------ persistence
    void LoadState()
    {
        try
        {
            if (!File.Exists(statePath)) return;
            var st = J.Parse(File.ReadAllText(statePath)) as JsonObject;
            Muted = J.Truthy(st?["muted"]);
            foreach (var n in st?["tasks"] as JsonArray ?? new JsonArray())
            {
                if (n is not JsonObject t0) continue;
                var t = (JsonObject)t0.DeepClone();
                var id = J.Str(t["id"]);
                long? ct = J.Num(t["_ct"]) is double d && d > 1e12 ? (long)d : null;
                t.Remove("_ct");
                tasks[id] = t;
                internals[id] = new Internal { Ct = ct };
            }
            Log.W($"restored {tasks.Count} task(s)");
        }
        catch (Exception e) { Log.W("state.json unreadable: " + e.Message); }
    }

    public void Save()
    {
        string text;
        lock (Lock)
        {
            if (!dirty) return;
            dirty = false;
            var arr = new JsonArray();
            foreach (var (id, t) in tasks)
            {
                var d = (JsonObject)t.DeepClone();
                if (internals.TryGetValue(id, out var it) && it.Ct is long ct) d["_ct"] = ct;
                arr.Add(d);
            }
            text = new JsonObject { ["muted"] = Muted, ["tasks"] = arr }.ToJsonString(J.Out);
        }
        try
        {
            var tmp = statePath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, statePath, true);
        }
        catch (Exception e) { Log.W("save failed: " + e.Message); }
    }

    public void MarkDirty() { lock (Lock) dirty = true; }

    // ------------------------------------------------------------ change signalling
    void Bump()   // call under Lock
    {
        Version++;
        dirty = true;
        var old = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
        ThreadPool.QueueUserWorkItem(_ => { try { Changed?.Invoke(); } catch { } });
    }

    public async Task<long> WaitChange(long since, double timeoutSec, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (true)
        {
            Task w;
            lock (Lock)
            {
                if (Version > since) return Version;
                w = changed.Task;
            }
            var rem = deadline - DateTime.UtcNow;
            if (rem <= TimeSpan.Zero) lock (Lock) return Version;
            try { await w.WaitAsync(rem, ct); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { lock (Lock) return Version; }
        }
    }

    JsonObject Get(string tid)
    {
        if (tid == null || !tasks.TryGetValue(tid, out var t)) throw new ApiError(404, $"task '{tid}' not found");
        return t;
    }

    Internal In(string tid)
    {
        if (!internals.TryGetValue(tid, out var i)) internals[tid] = i = new Internal();
        return i;
    }

    static string St(JsonObject t) => J.Str(t["state"]);

    void ToastFor(JsonObject t, string title, string msg, string level = "info", string kind = null)
    {
        if (Muted) return;   // "toasts": false only disables Windows toasts (checked in the Notifier); Telegram still gets them
        if (kind != null && kind != "milestones")
        {
            var nt = t["notify"] as JsonObject;
            if (nt != null && nt.ContainsKey(kind) && !J.Truthy(nt[kind])) return;
        }
        // milestone toasts are plain unless the task asks for its controls there (notify.milestone_controls)
        bool controls = kind != "milestones" || J.Truthy((t["notify"] as JsonObject)?["milestone_controls"]);
        Notifier?.Toast(title, msg, level, J.Str(t["id"]), (JsonObject)t.DeepClone(), controls: controls);
    }

    void AddLog(JsonObject t, JsonNode msg, string level = "info")
    {
        var log = t["log"] as JsonArray;
        if (log == null) t["log"] = log = new JsonArray();
        IEnumerable<JsonNode> lines = msg is JsonArray a ? a.ToList() : new List<JsonNode> { msg };
        foreach (var m0 in lines)
        {
            var m = J.Str(m0).TrimEnd('\r', '\n');
            if (m.Length == 0) continue;
            log.Add(new JsonObject { ["t"] = J.Now(), ["level"] = level, ["msg"] = J.Cut(m, 2000) });
        }
        int over = log.Count - S.Int("log_lines", 400);
        for (int i = 0; i < over; i++) log.RemoveAt(0);
    }
    void AddLog(JsonObject t, string msg, string level = "info") => AddLog(t, JsonValue.Create(msg), level);

    // ------------------------------------------------------------ validation
    static JsonObject NormInput(JsonNode inp)
    {
        if (!J.Truthy(inp)) return null;
        var o = inp as JsonObject ?? new JsonObject { ["type"] = "text" };
        var typ = J.Str(o["type"], "text");
        if (typ is not ("text" or "number" or "select" or "bool")) throw new ApiError(400, $"bad input type '{typ}'");
        var r = new JsonObject
        {
            ["type"] = typ,
            ["label"] = J.Cut(J.Str(o["label"]), 80),
            ["default"] = J.C(o["default"]),
            ["placeholder"] = J.C(o["placeholder"]),
        };
        if (typ == "select")
        {
            if (o["options"] is not JsonArray opts || opts.Count == 0) throw new ApiError(400, "select input needs options[]");
            r["options"] = J.StrArray(opts.Take(50).Select(x => J.Str(x)));
        }
        if (typ == "number")
            foreach (var k in new[] { "min", "max", "step" })
                if (o[k] != null) r[k] = J.C(o[k]);
        return r;
    }

    static JsonObject NormControl(JsonNode cn)
    {
        if (cn is not JsonObject c) throw new ApiError(400, "control must be an object");
        var cid = J.Str(c["id"]).Trim();
        if (!CidRx.IsMatch(cid)) throw new ApiError(400, $"bad control id '{cid}'");
        var kind = J.Str(c["kind"], "signal");
        if (kind is not ("signal" or "shell" or "open" or "kill")) throw new ApiError(400, $"bad control kind '{kind}'");
        var statesN = c["states"];
        List<string> states;
        if (J.IsStr(statesN)) states = new() { J.Str(statesN) };
        else if (statesN is JsonArray sa && sa.Count > 0) states = sa.Select(x => J.Str(x)).ToList();
        else states = Active.ToList();
        states = states.Where(x => AllStates.Contains(x)).ToList();
        if (states.Count == 0) states = Active.ToList();
        var style = J.Str(c["style"]);
        var r = new JsonObject
        {
            ["id"] = cid,
            ["label"] = J.Cut(J.Truthy(c["label"]) ? J.Str(c["label"]) : cid, 60),
            ["kind"] = kind,
            ["style"] = style is "primary" or "danger" or "default" ? style : "default",
            ["confirm"] = J.Truthy(c["confirm"]) ? J.Cut(J.Str(c["confirm"]), 200) : null,
            ["hint"] = J.Truthy(c["hint"]) ? J.Cut(J.Str(c["hint"]), 200) : null,
            ["states"] = J.StrArray(states),
            ["input"] = NormInput(c["input"]),
            ["toast"] = J.Truthy(c["toast"]),
        };
        if (kind == "shell")
        {
            var cmd = c["command"];
            bool ok = J.IsStr(cmd) || (cmd is JsonArray ca && ca.Count > 0 && ca.All(J.IsStr));
            if (!ok) throw new ApiError(400, "shell control needs command (string or list of strings)");
            r["command"] = J.C(cmd);
            r["cwd"] = J.C(c["cwd"]);
            r["timeout"] = J.Num(c["timeout"]) is double to && to > 0 ? to : 300;
        }
        if (kind == "open")
        {
            if (!J.Truthy(c["target"])) throw new ApiError(400, "open control needs target");
            r["target"] = J.Str(c["target"]);
        }
        return r;
    }

    JsonObject NormWidget(JsonNode wn)
    {
        if (wn is not JsonObject w) throw new ApiError(400, "widget must be an object");
        var wid = J.Str(w["id"]).Trim();
        if (!CidRx.IsMatch(wid)) throw new ApiError(400, $"bad widget id '{wid}'");
        var typ = J.Str(w["type"], "text");
        if (!WidgetTypes.Contains(typ)) throw new ApiError(400, $"bad widget type '{typ}'; one of {string.Join(", ", WidgetTypes)}");
        JsonNode span = null;
        if (w.ContainsKey("span") || w.ContainsKey("wide"))
            span = (J.Num(w["span"]) == 2 || J.Truthy(w["wide"])) ? 2 : 1;
        var r = new JsonObject
        {
            ["id"] = wid, ["type"] = typ,
            ["label"] = J.Cut(J.Str(w["label"]), 80),
            ["unit"] = J.Cut(J.Str(w["unit"]), 16),
            ["span"] = span,
            ["value"] = J.C(w["value"]),
        };
        foreach (var k in new[] { "min", "max", "warn", "crit", "decimals", "invert", "mono", "color", "columns", "kind",
                     "series", "max_points", "y_min", "y_max", "total", "height", "text" })
            if (w.ContainsKey(k)) r[k] = J.C(w[k]);
        if (typ == "chart")
        {
            var kind = J.Str(w["kind"]);
            r["kind"] = kind is "line" or "bar" or "area" ? kind : "line";
            var ser = w["series"];
            List<string> sl = ser is JsonArray sa && sa.Count > 0 ? sa.Select(x => J.Cut(J.Str(x), 32)).ToList()
                : J.Truthy(ser) ? new() { J.Cut(J.Str(ser), 32) } : new() { wid };
            r["series"] = J.StrArray(sl.Take(6));
            var mp = J.Num(w["max_points"]) is double m && m > 0 ? (int)m : 120;
            r["max_points"] = Math.Clamp(mp, 2, 2000);
            r["points"] = new JsonArray();
            r.Remove("value");
            if (J.Truthy(w["points"])) ChartAdd(r, w["points"]);
        }
        if (typ == "table")
        {
            r["columns"] = J.StrArray((w["columns"] as JsonArray ?? new JsonArray()).Take(12).Select(x => J.Cut(J.Str(x), 40)));
            r["value"] = J.C(w["value"]) ?? new JsonArray();
        }
        return r;
    }

    static JsonNode NumOrNull(JsonNode x) => J.IsNum(x) ? JsonValue.Create(J.Num(x).Value) : null;

    /// <summary>v: number | {series: number} | [number,...] (one per series) | list of such (batch).</summary>
    static void ChartAdd(JsonObject w, JsonNode v)
    {
        var now = J.Now();
        var series = (w["series"] as JsonArray).Select(x => J.Str(x)).ToList();
        var pts = w["points"] as JsonArray;
        if (pts == null) w["points"] = pts = new JsonArray();
        List<JsonNode> batch = v is JsonArray va && va.Count > 0 && (va[0] is JsonArray || va[0] is JsonObject)
            ? va.ToList() : new List<JsonNode> { v };
        foreach (var item in batch)
        {
            var row = new JsonArray();
            if (item is JsonObject io)
            {
                row.Add(J.Num(io["t"]) is double ts ? ts : now);
                foreach (var s in series) row.Add(NumOrNull(io[s]));
            }
            else if (item is JsonArray ia)
            {
                row.Add(now);
                for (int i = 0; i < series.Count; i++) row.Add(i < ia.Count ? NumOrNull(ia[i]) : null);
            }
            else
            {
                row.Add(now);
                row.Add(NumOrNull(item));
                for (int i = 1; i < series.Count; i++) row.Add(null);
            }
            pts.Add(row);
        }
        int max = J.Num(w["max_points"]) is double m ? (int)m : 120;
        int over = pts.Count - max;
        for (int i = 0; i < over; i++) pts.RemoveAt(0);
    }

    static void ApplyWidgetValues(JsonObject t, JsonObject vals)
    {
        var idx = new Dictionary<string, JsonObject>();
        foreach (var n in t["widgets"] as JsonArray ?? new JsonArray())
            if (n is JsonObject w) idx[J.Str(w["id"])] = w;
        foreach (var (wid, v) in vals)
        {
            if (!idx.TryGetValue(wid, out var w)) continue;
            var typ = J.Str(w["type"]);
            if (typ == "chart")
            {
                if (v == null) w["points"] = new JsonArray();
                else ChartAdd(w, v);
            }
            else if (v is JsonObject vo && typ is "progress" or "gauge" or "stat" or "led" or "text"
                     && (vo.ContainsKey("value") || vo.ContainsKey("label") || vo.ContainsKey("text") || vo.ContainsKey("total")))
            {
                foreach (var k in new[] { "value", "label", "text", "total", "unit", "max", "color" })
                    if (vo.ContainsKey(k)) w[k] = J.C(vo[k]);
            }
            else if (typ == "stat" && J.IsNum(v) && J.IsNum(w["value"]))
            {
                w["prev"] = J.C(w["value"]);
                w["value"] = J.C(v);
            }
            else w["value"] = J.C(v);
        }
    }

    // ------------------------------------------------------------ API operations
    public JsonObject Register(JsonObject b)
    {
        var name = J.Cut(J.Truthy(b["name"]) ? J.Str(b["name"]) : "task", 200);
        var tid = J.Truthy(b["id"]) ? J.Str(b["id"]) : Guid.NewGuid().ToString("N")[..8];
        if (!IdRx.IsMatch(tid)) throw new ApiError(400, "id must match [A-Za-z0-9_.-]{1,64}");
        var controls = (b["controls"] as JsonArray ?? new JsonArray()).Select(NormControl).ToList();
        var widgets = (b["widgets"] as JsonArray ?? new JsonArray()).Select(NormWidget).Take(40).ToList();
        int? pid = null;
        if (b["pid"] != null)
        {
            if (J.Num(b["pid"]) is double pd) pid = (int)pd;
            else throw new ApiError(400, "pid must be int");
        }
        bool killCtl = !b.ContainsKey("kill_control") || J.Truthy(b["kill_control"]);
        if (pid is > 0 && killCtl && !controls.Any(c => J.Str(c["id"]) == "kill"))
            controls.Add(NormControl(new JsonObject
            {
                ["id"] = "kill", ["label"] = "Kill", ["kind"] = "kill", ["style"] = "danger",
                ["confirm"] = L.T("kill_confirm", pid),
            }));
        var notify = new JsonObject
        {
            ["start"] = false, ["finish"] = true, ["fail"] = true, ["stale"] = true, ["milestones"] = new JsonArray(),
        };
        if (b["notify"] is JsonObject bn) foreach (var (k, v) in bn) notify[k] = J.C(v);
        bool progressToast = b.ContainsKey("progress_toast") ? J.Truthy(b["progress_toast"]) : S.Bool("progress_toasts");
        var now = J.Now();
        JsonObject t;
        bool resumed;
        lock (Lock)
        {
            tasks.TryGetValue(tid, out var old);
            resumed = old != null;
            t = new JsonObject
            {
                ["id"] = tid, ["name"] = name,
                ["source"] = J.Cut(J.Str(b["source"]), 40),
                ["description"] = J.Cut(J.Str(b["description"]), 1000),
                ["state"] = "running",
                ["created"] = old != null ? J.C(old["created"]) : now,
                ["started"] = old == null || IsFinal(St(old)) ? now : J.C(old["started"]),
                ["updated"] = now, ["last_seen"] = now, ["finished"] = null,
                ["progress"] = null, ["current"] = null,
                ["total"] = J.C(b["total"]), ["unit"] = J.Cut(J.Str(b["unit"]), 20),
                ["status"] = "", ["stage"] = "",
                ["metrics"] = old != null ? J.C(old["metrics"]) ?? new JsonObject() : new JsonObject(),
                ["log"] = old != null ? J.C(old["log"]) ?? new JsonArray() : new JsonArray(),
                ["pid"] = pid, ["cwd"] = J.C(b["cwd"]), ["host"] = J.C(b["host"]),
                ["controls"] = new JsonArray(controls.ToArray<JsonNode>()),
                ["widgets"] = new JsonArray(widgets.ToArray<JsonNode>()),
                ["notify"] = notify,
                ["progress_toast"] = progressToast,
                ["heartbeat_timeout"] = J.Num(b["heartbeat_timeout"]) ?? 0,
                ["meta"] = J.C(b["meta"]) ?? new JsonObject(),
                ["links"] = J.C(b["links"]) ?? new JsonArray(),
                ["result"] = null, ["message"] = "", ["seen"] = true,
                ["milestones_hit"] = new JsonArray(),
                ["commands"] = old != null ? J.C(old["commands"]) ?? new JsonArray() : new JsonArray(),
            };
            if (old != null)
            {
                var prev = (old["widgets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .GroupBy(w => J.Str(w["id"])).ToDictionary(g => g.Key, g => g.First());
                foreach (var w in widgets)
                {
                    if (!prev.TryGetValue(J.Str(w["id"]), out var p)) continue;
                    if (J.Str(p["type"]) == "chart" && J.Str(w["type"]) == "chart" && J.Dump(p["series"]) == J.Dump(w["series"]))
                    {
                        var pp = p["points"] as JsonArray ?? new JsonArray();
                        int mp = (int)(J.Num(w["max_points"]) ?? 120);
                        w["points"] = new JsonArray(pp.Skip(Math.Max(0, pp.Count - mp)).Select(x => x.DeepClone()).ToArray());
                    }
                }
            }
            tasks[tid] = t;
            internals[tid] = new Internal { Ct = pid is > 0 ? Native.CreateTime(pid.Value) : null };
            AddLog(t, (resumed ? "re-registered" : "registered") + (pid != null ? $" (pid {pid})" : ""), "sys");
            Prune();
            Bump();
        }
        Log.W($"register {tid} '{name}' pid={pid}");
        if (J.Truthy(notify["start"]))
            ToastFor(t, "▶ " + name, J.Truthy(t["description"]) ? J.Str(t["description"]) : L.T("started"));
        if (progressToast && !Muted && S.Bool("toasts", true)) lock (Lock) Notifier?.Progress((JsonObject)t.DeepClone());
        return new JsonObject { ["id"] = tid, ["panel"] = "/ui#" + tid, ["resumed"] = resumed };
    }

    static double Clamp01(double x) => Math.Max(0, Math.Min(1, x));

    public JsonObject Update(string tid, JsonObject b)
    {
        lock (Lock)
        {
            var t = Get(tid);
            var now = J.Now();
            t["updated"] = now; t["last_seen"] = now;
            if (St(t) == "stale")
            {
                t["state"] = "running";
                AddLog(t, "alive again", "sys");
            }
            if (b.ContainsKey("total")) t["total"] = J.C(b["total"]);
            if (b.ContainsKey("unit")) t["unit"] = J.Cut(J.Str(b["unit"]), 20);
            if (b.ContainsKey("current"))
            {
                t["current"] = J.C(b["current"]);
                if (J.Num(t["total"]) is double tot && tot != 0 && J.Num(b["current"]) is double cur)
                    t["progress"] = Clamp01(cur / tot);
            }
            if (J.Num(b["percent"]) is double pct) t["progress"] = Clamp01(pct / 100.0);
            if (b.ContainsKey("progress"))
                t["progress"] = J.Num(b["progress"]) is double pr ? Clamp01(pr) : null;
            foreach (var k in new[] { "status", "stage", "name", "description" })
                if (b[k] != null) t[k] = J.Cut(J.Str(b[k]), 1000);
            if (b["metrics"] is JsonObject bm)
            {
                var m = t["metrics"] as JsonObject;
                if (m == null) t["metrics"] = m = new JsonObject();
                foreach (var (k, v) in bm)
                {
                    if (v == null) m.Remove(k);
                    else m[J.Cut(k, 60)] = J.IsNum(v) || J.IsBool(v) ? J.C(v) : J.Cut(J.Str(v), 200);
                }
            }
            if (b["meta"] is JsonObject bmeta)
            {
                var m = t["meta"] as JsonObject;
                if (m == null) t["meta"] = m = new JsonObject();
                foreach (var (k, v) in bmeta) m[k] = J.C(v);
            }
            if (b["links"] is JsonArray bl) t["links"] = J.C(bl);
            var bs = J.Str(b["state"]);
            if (bs is "running" or "paused" && IsActive(St(t)))
            {
                if (St(t) != bs) AddLog(t, "state -> " + bs, "sys");
                t["state"] = bs;
            }
            if (b["controls"] is JsonArray bc)
                t["controls"] = new JsonArray(bc.Select(NormControl).ToArray<JsonNode>());
            if (b["add_controls"] is JsonArray ac)
                foreach (var c in ac)
                {
                    var nc = NormControl(c);
                    var cur = (t["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Where(x => J.Str(x["id"]) != J.Str(nc["id"])).Select(x => (JsonNode)x.DeepClone()).ToList();
                    cur.Add(nc);
                    t["controls"] = new JsonArray(cur.ToArray());
                }
            if (b["remove_controls"] is JsonArray rc)
            {
                var rm = rc.Select(x => J.Str(x)).ToHashSet();
                t["controls"] = new JsonArray((t["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Where(x => !rm.Contains(J.Str(x["id"]))).Select(x => (JsonNode)x.DeepClone()).ToArray());
            }
            if (b["add_widgets"] is JsonArray aw)
                foreach (var w in aw)
                {
                    var nw = NormWidget(w);
                    var ws = t["widgets"] as JsonArray;
                    if (ws == null) t["widgets"] = ws = new JsonArray();
                    int i = -1;
                    for (int k = 0; k < ws.Count; k++) if (J.Str(ws[k]?["id"]) == J.Str(nw["id"])) { i = k; break; }
                    if (i < 0) ws.Add(nw); else ws[i] = nw;
                }
            if (b["remove_widgets"] is JsonArray rw)
            {
                var rm = rw.Select(x => J.Str(x)).ToHashSet();
                t["widgets"] = new JsonArray((t["widgets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Where(x => !rm.Contains(J.Str(x["id"]))).Select(x => (JsonNode)x.DeepClone()).ToArray());
            }
            if (b["widgets"] is JsonObject wv) ApplyWidgetValues(t, wv);
            var level = J.Truthy(b["level"]) ? J.Str(b["level"]) : "info";
            if (J.Truthy(b["log"])) AddLog(t, b["log"], level);
            // milestones
            var ms = (t["notify"] as JsonObject)?["milestones"] as JsonArray;
            if (J.Num(t["progress"]) is double prog && ms != null && ms.Count > 0)
            {
                var hit = t["milestones_hit"] as JsonArray;
                if (hit == null) t["milestones_hit"] = hit = new JsonArray();
                foreach (var mn in ms)
                {
                    if (J.Num(mn) is not double m) continue;
                    if (prog * 100 + 1e-9 >= m && !hit.Any(h => J.Num(h) == m))
                    {
                        hit.Add(m);
                        ToastFor(t, $"{J.Str(t["name"])} — {J.Fmt(m)}%",
                            J.Truthy(t["status"]) ? J.Str(t["status"]) : J.Str(t["stage"]), "info", "milestones");
                    }
                }
            }
            if (J.Truthy(b["toast"]))
            {
                string msg;
                if (J.IsStr(b["toast"])) msg = J.Str(b["toast"]);
                else if (b["log"] is JsonArray la && la.Count > 0) msg = J.Str(la[^1]);
                else if (J.Truthy(b["log"])) msg = J.Str(b["log"]);
                else msg = J.Str(t["status"]);
                ToastFor(t, J.Str(t["name"]), msg, level);
            }
            // in-place progress toast (throttled)
            if (J.Truthy(t["progress_toast"]) && !Muted && S.Bool("toasts", true))
            {
                var it = In(tid);
                if (now - it.LastProgressToast >= 1.0)
                {
                    it.LastProgressToast = now;
                    Notifier?.Progress((JsonObject)t.DeepClone());
                }
            }
            Bump();
        }
        return new JsonObject { ["ok"] = true };
    }

    public JsonObject AddLogApi(string tid, JsonObject b)
    {
        var c = (JsonObject)b.DeepClone();
        JsonNode msg = null;
        foreach (var k in new[] { "msg", "lines", "message" })
            if (J.Truthy(c[k])) { msg = J.C(c[k]); break; }
        c.Remove("msg"); c.Remove("lines"); c.Remove("message");
        c["log"] = msg;
        return Update(tid, c);
    }

    public static string FmtDur(double s)
    {
        long n = (long)Math.Max(0, s);
        if (n < 60) return $"{n}s";
        if (n < 3600) return $"{n / 60}m {n % 60:00}s";
        return $"{n / 3600}h {n % 3600 / 60:00}m";
    }

    public JsonObject Finish(string tid, JsonObject b)
    {
        var state = J.Truthy(b["state"]) ? J.Str(b["state"]) : "done";
        if (!IsFinal(state)) throw new ApiError(400, "state must be one of " + string.Join(", ", Final));
        JsonObject t;
        bool already;
        double now;
        lock (Lock)
        {
            t = Get(tid);
            already = IsFinal(St(t));
            now = J.Now();
            t["state"] = state;
            t["finished"] = now;
            t["updated"] = now; t["last_seen"] = now;
            t["message"] = J.Cut(J.Str(b["message"]), 2000);
            if (b.ContainsKey("result")) t["result"] = J.C(b["result"]);
            if (b["metrics"] is JsonObject bm)
            {
                var m = t["metrics"] as JsonObject;
                if (m == null) t["metrics"] = m = new JsonObject();
                foreach (var (k, v) in bm) if (v != null) m[k] = J.C(v);
            }
            if (b["widgets"] is JsonObject wv) ApplyWidgetValues(t, wv);
            if (state == "done" && t["progress"] != null) t["progress"] = 1.0;
            t["seen"] = state == "done";
            In(tid).CmdQ.Clear();
            var message = J.Str(t["message"]);
            AddLog(t, "finished: " + state + (message.Length > 0 ? " — " + message : ""),
                state is "failed" or "lost" ? "error" : "sys");
            Prune();
            Bump();
            t = (JsonObject)t.DeepClone();
        }
        if (J.Truthy(t["progress_toast"])) Notifier?.ProgressEnd(tid);
        if (!already)
        {
            var dur = FmtDur(now - (J.Num(t["started"]) ?? now));
            var name = J.Str(t["name"]);
            var msg = J.Str(t["message"]);
            if (state == "done") ToastFor(t, "✅ " + name, (msg.Length > 0 ? msg : L.T("done")) + "  ·  " + dur, "info", "finish");
            else if (state == "cancelled") ToastFor(t, "⏹ " + name, (msg.Length > 0 ? msg : L.T("cancelled")) + "  ·  " + dur, "warn", "finish");
            else ToastFor(t, "❌ " + name, (msg.Length > 0 ? msg : state) + "  ·  " + dur, "error", "fail");
        }
        Log.W($"finish {tid} {state}");
        return new JsonObject { ["ok"] = true };
    }

    public JsonObject Heartbeat(string tid) => Update(tid, new JsonObject());

    public JsonObject Delete(string tid, bool force)
    {
        bool hadProgress;
        lock (Lock)
        {
            var t = Get(tid);
            if (IsActive(St(t)) && !force) throw new ApiError(409, "task is active; finish it first or use ?force=1");
            hadProgress = J.Truthy(t["progress_toast"]);
            tasks.Remove(tid);
            internals.Remove(tid);
            Bump();
        }
        if (hadProgress) Notifier?.ProgressEnd(tid);
        return new JsonObject { ["ok"] = true };
    }

    public JsonObject ClearFinished()
    {
        lock (Lock)
        {
            foreach (var id in tasks.Where(kv => IsFinal(St(kv.Value))).Select(kv => kv.Key).ToList())
            {
                tasks.Remove(id);
                internals.Remove(id);
            }
            Bump();
        }
        return new JsonObject { ["ok"] = true };
    }

    void Prune()
    {
        var fin = tasks.Values.Where(t => IsFinal(St(t)))
            .OrderByDescending(t => J.Num(t["finished"]) ?? 0).Skip(S.Int("keep_finished", 60)).ToList();
        foreach (var t in fin)
        {
            var id = J.Str(t["id"]);
            tasks.Remove(id);
            internals.Remove(id);
        }
    }

    public void MarkSeen()
    {
        lock (Lock)
        {
            bool ch = false;
            foreach (var t in tasks.Values)
                if (!J.Truthy(t["seen"])) { t["seen"] = true; ch = true; }
            if (ch) Bump();
        }
    }

    public void SetMuted(bool m)
    {
        lock (Lock) { Muted = m; Bump(); }
    }

    /// <summary>"auto" or one of L.Langs; persisted in config.json.</summary>
    public void SetLanguage(string lang)
    {
        lang = (lang ?? "auto").Trim().ToLowerInvariant();
        if (lang != "auto" && !L.Langs.Contains(lang)) throw new ApiError(400, "language must be auto or one of " + string.Join(", ", L.Langs));
        lock (Lock)
        {
            S.Cfg["language"] = lang;
            Bump();
        }
        S.SaveCfg();
    }

    // ------------------------------------------------------------ controls & commands
    public JsonObject Trigger(string tid, string cid, JsonObject b, string via = null)
    {
        var value = J.C(b["value"]);
        JsonObject c;
        int? pid;
        lock (Lock)
        {
            var t = Get(tid);
            c = (t["controls"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(x => J.Str(x["id"]) == cid);
            if (c == null) throw new ApiError(404, $"control '{cid}' not found");
            var states = (c["states"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).ToList();
            if (!states.Contains(St(t))) throw new ApiError(409, $"control '{cid}' not available in state '{St(t)}'");
            var label = J.Str(c["label"]) + (value != null ? " = " + J.Dump(value) : "") + (via != null ? $" ({via})" : "");
            if (value != null)
            {
                c["last_value"] = J.C(value);   // next toast / panel form starts from the current value
                Notifier?.Refresh(tid);         // and toasts already on screen / in the notification center follow it
            }
            if (J.Str(c["kind"]) == "signal")
            {
                var cmd = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("N")[..10], ["control"] = cid, ["value"] = J.C(value),
                    ["t"] = J.Now(), ["status"] = "pending", ["reply"] = null,
                };
                In(tid).CmdQ.Add((JsonObject)cmd.DeepClone());
                var hist = (t["commands"] as JsonArray ?? new JsonArray()).Select(x => x.DeepClone()).ToList();
                hist = hist.Skip(Math.Max(0, hist.Count - 19)).ToList();
                hist.Add(cmd.DeepClone());
                t["commands"] = new JsonArray(hist.ToArray());
                AddLog(t, "⇢ " + label, "cmd");
                Bump();
                return new JsonObject { ["ok"] = true, ["command"] = cmd };
            }
            AddLog(t, "⇢ " + label, "cmd");
            Bump();
            pid = J.Num(t["pid"]) is double pd ? (int)pd : null;
            c = (JsonObject)c.DeepClone();
        }
        switch (J.Str(c["kind"]))
        {
            case "kill":
                if (pid is not > 0) throw new ApiError(400, "task has no pid");
                bool ok = KillTree(pid.Value);
                Finish(tid, new JsonObject
                {
                    ["state"] = "cancelled",
                    ["message"] = ok ? $"killed from panel (pid {pid})" : $"kill attempted, pid {pid}",
                });
                return new JsonObject { ["ok"] = ok };
            case "open":
                try { OpenTarget(J.Str(c["target"])); }
                catch (Exception e) { throw new ApiError(500, "open failed: " + e.Message); }
                return new JsonObject { ["ok"] = true };
            case "shell":
                var v = value;
                _ = Task.Run(() => RunShell(tid, c, v, pid));
                return new JsonObject { ["ok"] = true, ["started"] = true };
        }
        throw new ApiError(400, "unknown kind");
    }

    public static bool KillTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (Exception e)
        {
            Log.W($"kill {pid} failed: {e.Message}; trying taskkill");
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F") { CreateNoWindow = true, UseShellExecute = false };
                using var tk = Process.Start(psi);
                tk.WaitForExit(10000);
                return tk.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    public static void OpenTarget(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    void RunShell(string tid, JsonObject c, JsonNode value, int? pid)
    {
        var subs = new Dictionary<string, string>
        {
            ["value"] = value == null ? "" : J.Str(value), ["pid"] = pid?.ToString() ?? "", ["task_id"] = tid,
        };
        string Sub(string s)
        {
            foreach (var (k, v) in subs) s = s.Replace("{" + k + "}", v);
            return s;
        }
        var cid = J.Str(c["id"]);
        List<string> lines;
        string level;
        try
        {
            ProcessStartInfo psi;
            var cmd = c["command"];
            if (cmd is JsonArray ca)
            {
                var parts = ca.Select(x => Sub(J.Str(x))).ToList();
                psi = new ProcessStartInfo(parts[0]);
                foreach (var a in parts.Skip(1)) psi.ArgumentList.Add(a);
            }
            else
            {
                psi = new ProcessStartInfo("cmd.exe");
                psi.Arguments = "/d /s /c \"" + Sub(J.Str(cmd)) + "\"";
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["TASKHUB_TASK_ID"] = tid;
            psi.Environment["TASKHUB_URL"] = S.Url;
            if (J.Truthy(c["cwd"])) psi.WorkingDirectory = J.Str(c["cwd"]);
            using var p = Process.Start(psi);
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            var timeout = (int)((J.Num(c["timeout"]) ?? 300) * 1000);
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(true); } catch { }
                throw new Exception($"timeout after {timeout / 1000}s");
            }
            p.WaitForExit();
            var outp = (so.Result ?? "") + (se.Result ?? "");
            lines = outp.Trim().Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).ToList();
            lines = lines.Skip(Math.Max(0, lines.Count - 25)).Select(x => $"[{cid}] {x}").ToList();
            lines.Add($"[{cid}] exit {p.ExitCode}");
            level = p.ExitCode == 0 ? "info" : "error";
        }
        catch (Exception e)
        {
            lines = new() { $"[{cid}] failed: {e.Message}" };
            level = "error";
        }
        lock (Lock)
        {
            if (tasks.TryGetValue(tid, out var t))
            {
                AddLog(t, J.StrArray(lines), level);
                Bump();
            }
        }
    }

    public async Task<JsonObject> PollCommands(string tid, double wait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(wait, 60)));
        while (true)
        {
            Task w;
            lock (Lock)
            {
                Get(tid);
                var q = In(tid).CmdQ;
                if (q.Count > 0 || DateTime.UtcNow >= deadline || ct.IsCancellationRequested)
                {
                    var outp = new JsonArray(q.Select(x => (JsonNode)x.DeepClone()).ToArray());
                    var t = tasks[tid];
                    if (q.Count > 0)
                    {
                        var ids = q.Select(x => J.Str(x["id"])).ToHashSet();
                        foreach (var h in (t["commands"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                            if (ids.Contains(J.Str(h["id"]))) h["status"] = "delivered";
                        q.Clear();
                        Bump();
                    }
                    return new JsonObject { ["commands"] = outp, ["state"] = J.Str(t["state"]) };
                }
                w = changed.Task;
            }
            var rem = deadline - DateTime.UtcNow;
            if (rem < TimeSpan.FromMilliseconds(50)) rem = TimeSpan.FromMilliseconds(50);
            try { await w.WaitAsync(rem, ct); } catch (TimeoutException) { } catch (OperationCanceledException) { }
        }
    }

    public JsonObject Ack(string tid, string cmdId, JsonObject b)
    {
        lock (Lock)
        {
            var t = Get(tid);
            bool ok = !b.ContainsKey("ok") || J.Truthy(b["ok"]);
            var msg = J.Cut(J.Truthy(b["message"]) ? J.Str(b["message"]) : (ok ? "ok" : "failed"), 500);
            foreach (var h in (t["commands"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (J.Str(h["id"]) != cmdId) continue;
                h["status"] = ok ? "ok" : "error";
                h["reply"] = msg;
                AddLog(t, $"⇠ {J.Str(h["control"])}: {msg}", ok ? "cmd" : "error");
                break;
            }
            Bump();
        }
        return new JsonObject { ["ok"] = true };
    }

    // ------------------------------------------------------------ misc
    public JsonObject Notify(JsonObject b)
    {
        var title = J.Truthy(b["title"]) ? J.Str(b["title"]) : Settings.AppId;
        var msg = J.Truthy(b["message"]) ? J.Str(b["message"]) : J.Str(b["msg"]);
        var level = J.Truthy(b["level"]) ? J.Str(b["level"]) : "info";
        string tid = J.Truthy(b["task"]) ? J.Str(b["task"]) : null;
        JsonObject snap = null;
        lock (Lock)
            if (tid != null && tasks.TryGetValue(tid, out var t)) snap = (JsonObject)t.DeepClone();
        // "force" overrides the user's mute, but not "toasts": false in config.json (notifications disabled entirely)
        bool dismiss = snap == null && J.Str(b["click"]) == "dismiss";
        if (!Muted || J.Truthy(b["force"])) Notifier?.Toast(title, msg, level, tid, snap, dismiss);
        return new JsonObject { ["ok"] = true };
    }

    public void SetFocus(string tid)
    {
        lock (Lock)
        {
            focus = new JsonObject { ["task"] = string.IsNullOrEmpty(tid) ? null : tid, ["seq"] = (J.Num(focus["seq"]) ?? 0) + 1 };
            Bump();
        }
    }

    public JsonObject Snapshot(bool activeOnly = false, bool brief = false)
    {
        lock (Lock)
        {
            var list = tasks.Values
                .OrderBy(t => IsActive(St(t)) ? 0 : 1)
                .ThenByDescending(t => J.Num(t["finished"]) ?? J.Num(t["started"]) ?? 0)
                .Where(t => !activeOnly || IsActive(St(t)))
                .Select(t =>
                {
                    var c = (JsonObject)t.DeepClone();
                    if (brief && c["log"] is JsonArray lg && lg.Count > 5)
                        c["log"] = new JsonArray(lg.Skip(lg.Count - 5).Select(x => x.DeepClone()).ToArray());
                    return (JsonNode)c;
                }).ToArray();
            return new JsonObject
            {
                ["version"] = Version, ["now"] = J.Now(), ["muted"] = Muted,
                ["lang"] = L.Lang, ["language"] = L.Setting,
                ["focus"] = focus.DeepClone(),
                ["server"] = new JsonObject { ["version"] = Settings.Version, ["pid"] = Environment.ProcessId, ["impl"] = "dotnet" },
                ["tasks"] = new JsonArray(list),
            };
        }
    }

    public JsonObject GetTask(string tid)
    {
        lock (Lock) return (JsonObject)Get(tid).DeepClone();
    }

    public (int total, int active) Counts()
    {
        lock (Lock) return (tasks.Count, tasks.Values.Count(t => IsActive(St(t))));
    }

    public sealed class Summary
    {
        public List<JsonObject> ActiveT = new(), Failed = new(), Stale = new(), RecentDone = new();
        public double? Progress;
    }

    public Summary GetSummary()
    {
        lock (Lock)
        {
            var s = new Summary();
            var now = J.Now();
            foreach (var t in tasks.Values)
            {
                var st = St(t);
                if (IsActive(st)) s.ActiveT.Add((JsonObject)t.DeepClone());
                if (st == "stale") s.Stale.Add((JsonObject)t.DeepClone());
                if (st is "failed" or "lost" && !J.Truthy(t["seen"])) s.Failed.Add((JsonObject)t.DeepClone());
                if (st == "done" && now - (J.Num(t["finished"]) ?? 0) < 600) s.RecentDone.Add((JsonObject)t.DeepClone());
            }
            var pr = s.ActiveT.Select(t => J.Num(t["progress"])).Where(x => x != null).Select(x => x.Value).ToList();
            s.Progress = pr.Count > 0 ? pr.Average() : null;
            return s;
        }
    }

    // ------------------------------------------------------------ watchdog
    public async Task Watchdog(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch { break; }
            try
            {
                var lost = new List<string>();
                var stale = new List<JsonObject>();
                lock (Lock)
                {
                    var now = J.Now();
                    foreach (var (tid, t) in tasks.ToList())
                    {
                        if (!IsActive(St(t))) continue;
                        if (J.Num(t["pid"]) is double pd && pd > 0 && !Native.PidAlive((int)pd, In(tid).Ct))
                            lost.Add(tid);
                        else if (J.Num(t["heartbeat_timeout"]) is double hb && hb > 0 && St(t) == "running"
                                 && now - (J.Num(t["last_seen"]) ?? now) > hb)
                        {
                            t["state"] = "stale";
                            AddLog(t, "no update for " + FmtDur(now - (J.Num(t["last_seen"]) ?? now)), "warn");
                            stale.Add((JsonObject)t.DeepClone());
                            Bump();
                        }
                    }
                }
                foreach (var tid in lost)
                    try { Finish(tid, new JsonObject { ["state"] = "lost", ["message"] = "process exited without reporting" }); } catch { }
                foreach (var t in stale)
                    ToastFor(t, "⚠ " + J.Str(t["name"]), L.T("no_updates", FmtDur(J.Num(t["heartbeat_timeout"]) ?? 0)), "warn", "stale");
                Save();
            }
            catch (Exception e) { Log.W("watchdog error " + e); }
        }
        Save();
    }
}
