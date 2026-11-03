using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>Task operations behind the HTTP API: register, update, log, finish, delete, seen, panel settings.</summary>
sealed partial class Hub
{
    // ------------------------------------------------------------ API operations
    /// <param name="trusted">from a built-in connector: "connector" controls, "started" (when the job really began)</param>
    public JsonObject Register(JsonObject b, bool trusted = false)
    {
        var name = J.Cut(J.Truthy(b["name"]) ? J.Str(b["name"]) : "task", 200);
        var tid = J.Truthy(b["id"]) ? J.Str(b["id"]) : Guid.NewGuid().ToString("N")[..8];
        if (!IdRx.IsMatch(tid)) throw new ApiError(400, "id must match [A-Za-z0-9_.-]{1,64}");
        var controls = (b["controls"] as JsonArray ?? new JsonArray()).Select(c => NormControl(c, trusted)).ToList();
        var widgets = (b["widgets"] as JsonArray ?? new JsonArray()).Select(NormWidget).Take(40).ToList();
        int? pid = null;
        if (b["pid"] != null)
        {
            if (J.Num(b["pid"]) is double pd) pid = (int)pd;
            else throw new ApiError(400, "pid must be int");
        }
        var links = NormLinks(b["links"]);
        // the creation time tells the registered process from a later one with the same pid; without it (a pid that
        // isn't a Windows process, e.g. from WSL / MSYS) Kill could hit an unrelated process, so no Kill control then
        long? ct = pid is > 0 ? Native.CreateTime(pid.Value) : null;
        bool killCtl = !b.ContainsKey("kill_control") || J.Truthy(b["kill_control"]);
        if (pid is > 0 && ct != null && killCtl && !controls.Any(c => J.Str(c["id"]) == "kill"))
            controls.Add(NormControl(new JsonObject
            {
                ["id"] = "kill", ["label"] = "Kill", ["kind"] = "kill", ["style"] = "danger",
                ["confirm"] = L.T("kill_confirm", pid),
            }, false));
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
                // panel grouping (default: the cwd folder name) / "needs you" text that puts the task on top of the panel
                ["project"] = J.Cut(J.Str(b["project"]), 80),
                ["attention"] = J.Cut(J.Str(b["attention"]), 300),
                ["state"] = "running",
                ["created"] = old != null ? J.C(old["created"]) : now,
                ["started"] = trusted && J.Num(b["started"]) is double st0 && st0 <= now ? st0
                    : old == null || IsFinal(St(old)) ? now : J.C(old["started"]),
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
                ["expected_duration"] = J.Num(b["expected_duration"]) is double ed && ed > 0 ? ed : null,
                ["meta"] = J.C(b["meta"]) ?? new JsonObject(),
                ["links"] = links,
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
            internals[tid] = new Internal { Ct = ct };
            AddLog(t, (resumed ? "re-registered" : "registered") + (pid != null ? $" (pid {pid})" : ""), "sys");
            Prune();
            Bump();
        }
        Log.I("hub", $"register {Log.Id(tid)} pid={pid}"); Log.D("hub", $"task {Log.Id(tid)} = {tid}: '{name}'");
        if (J.Truthy(notify["start"]))
            ToastFor(t, "▶ " + name, J.Truthy(t["description"]) ? J.Str(t["description"]) : L.T("started"));
        if (progressToast && !Muted && S.Bool("toasts", true)) lock (Lock) Notifier?.Progress(ToastSnap(t));
        return new JsonObject { ["id"] = tid, ["panel"] = "/ui#" + tid, ["resumed"] = resumed };
    }

    static double Clamp01(double x) => Math.Max(0, Math.Min(1, x));

    public JsonObject Update(string tid, JsonObject b, bool trusted = false)
    {
        // validate everything that can be rejected first: a 400 must not leave the task half-updated
        var setControls = b["controls"] is JsonArray bc ? bc.Select(c => NormControl(c, trusted)).ToArray<JsonNode>() : null;
        var addControls = b["add_controls"] is JsonArray ac ? ac.Select(c => NormControl(c, trusted)).ToList() : null;
        var addWidgets = b["add_widgets"] is JsonArray aw ? aw.Select(NormWidget).ToList() : null;
        var setLinks = b["links"] is JsonArray ? NormLinks(b["links"]) : null;
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
            foreach (var k in new[] { "status", "stage", "name", "description", "project", "attention" })
                if (b[k] != null) t[k] = J.Cut(J.Str(b[k]), k == "project" ? 80 : k == "attention" ? 300 : 1000);
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
            if (setLinks != null) t["links"] = setLinks;
            if (b.ContainsKey("expected_duration")) t["expected_duration"] = J.Num(b["expected_duration"]) is double ed && ed > 0 ? ed : null;
            var bs = J.Str(b["state"]);
            if (bs is "running" or "paused" && IsActive(St(t)))
            {
                if (St(t) != bs) AddLog(t, "state -> " + bs, "sys");
                t["state"] = bs;
            }
            if (setControls != null)
                t["controls"] = new JsonArray(setControls);
            if (addControls != null)
                foreach (var nc in addControls)
                {
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
            if (addWidgets != null)
                foreach (var nw in addWidgets)
                {
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
            UpdatePace(t, In(tid), now);
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
                    Notifier?.Progress(ToastSnap(t));
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

    public JsonObject Finish(string tid, JsonObject b) => Finish(tid, b, null);

    /// <summary>Finishes without its own notification, for a mass action whose caller sends one summary instead.
    /// Internal only: the HTTP API has no way to ask for it.</summary>
    public JsonObject FinishQuietly(string tid, JsonObject b)
    {
        lock (Lock)
            if (Get(tid)["notify"] is JsonObject n) { n["finish"] = false; n["fail"] = false; }
        return Finish(tid, b, null);
    }

    /// <param name="onlyGen">watchdog: finish only if the task is still active and still the same registration
    /// (its Internal) - not one finished or re-registered with the same id meanwhile. Returns null if skipped.</param>
    JsonObject Finish(string tid, JsonObject b, Internal onlyGen)
    {
        var state = J.Truthy(b["state"]) ? J.Str(b["state"]) : "done";
        if (!IsFinal(state)) throw new ApiError(400, "state must be one of " + string.Join(", ", Final));
        JsonObject t;
        bool already;
        double now;
        lock (Lock)
        {
            t = Get(tid);
            if (onlyGen != null && (!internals.TryGetValue(tid, out var gen) || gen != onlyGen || !IsActive(St(t)))) return null;
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
            t.Remove("rate"); t.Remove("eta_at");
            t["attention"] = "";
            t["seen"] = false;   // until the user has seen it in the panel
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
            // a task that says nothing when it ends has told the user nothing: the agent's Stop still counts
            var said = state is "done" or "cancelled" ? "finish" : "fail";
            if (t["notify"] is not JsonObject told || !told.ContainsKey(said) || J.Truthy(told[said])) AgentHooks.AgentSpoke(b);
        }
        Log.I("hub", $"finish {Log.Id(tid)} {state}");
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

    /// <summary>History limits: finished tasks the user has seen go after history_days (failed ones stay until removed);
    /// above keep_finished the oldest go first, successful ones before failed ones. Returns true if any was removed.</summary>
    bool Prune()
    {
        var now = J.Now();
        var keepFor = Math.Max(0, S.Int("history_days", 7)) * 86400.0;
        bool Failed(JsonObject t) => St(t) is "failed" or "lost";
        var fin = tasks.Values.Where(t => IsFinal(St(t))).ToList();
        var drop = fin.Where(t => keepFor > 0 && J.Truthy(t["seen"]) && !Failed(t) && now - (J.Num(t["finished"]) ?? now) > keepFor).ToList();
        int excess = fin.Count - drop.Count - S.Int("keep_finished", 60);
        if (excess > 0)
            drop.AddRange(fin.Except(drop).OrderBy(t => Failed(t) ? 1 : 0).ThenBy(t => J.Num(t["finished"]) ?? 0).Take(excess));
        foreach (var t in drop)
        {
            var id = J.Str(t["id"]);
            tasks.Remove(id);
            internals.Remove(id);
        }
        return drop.Count > 0;
    }

    /// <summary>Marks finished tasks as seen by the user: the given ids (shown in the open panel), or all.</summary>
    public void MarkSeen(IEnumerable<string> ids = null)
    {
        lock (Lock)
        {
            bool ch = false;
            var only = ids?.ToHashSet();
            foreach (var (id, t) in tasks)
                if ((only == null || only.Contains(id)) && !J.Truthy(t["seen"])) { t["seen"] = true; ch = true; }
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
        S.Set(("language", lang));
        lock (Lock) Bump();
    }

    /// <summary>The panel's look and behaviour (config.json): theme, accent, panel and card transparency, grouping, pin.</summary>
    public JsonObject PanelSettings() => new()
    {
        ["theme"] = J.Str(S.Get("theme"), "system"),
        ["accent"] = J.Str(S.Get("accent"), "system"),
        ["panel_transparency"] = Math.Clamp(S.Int("panel_transparency", 30), 0, 100),
        ["card_transparency"] = Math.Clamp(S.Int("card_transparency", 30), 0, 100),
        ["panel_group"] = J.Str(S.Get("panel_group"), "project"),
        ["panel_pinned"] = S.Bool("panel_pinned", false),
        ["panel_push_windows"] = S.Bool("panel_push_windows", false),
    };

    /// <summary>Raised (on the caller's thread) after a panel setting changed.</summary>
    public event Action PanelSettingsChanged;

    public void SetPanelSettings(JsonObject b)
    {
        var kv = new List<(string, JsonNode)>();
        if (b.ContainsKey("theme"))
        {
            var v = J.Str(b["theme"]);
            if (v is not ("system" or "light" or "dark")) throw new ApiError(400, "theme must be system, light or dark");
            kv.Add(("theme", v));
        }
        if (b.ContainsKey("accent"))
        {
            var v = J.Str(b["accent"]);
            if (v is not ("system" or "taskhub")) throw new ApiError(400, "accent must be system or taskhub");
            kv.Add(("accent", v));
        }
        foreach (var key in new[] { "panel_transparency", "card_transparency" })
        {
            if (!b.ContainsKey(key)) continue;
            if (J.Num(b[key]) is not double d || !double.IsFinite(d) || d < 0 || d > 100)
                throw new ApiError(400, key + " must be a number from 0 to 100");
            kv.Add((key, (int)Math.Round(d)));
        }
        if (b.ContainsKey("panel_group"))
        {
            var v = J.Str(b["panel_group"]);
            if (v is not ("project" or "source" or "none")) throw new ApiError(400, "panel_group must be project, source or none");
            kv.Add(("panel_group", v));
        }
        if (b.ContainsKey("panel_pinned")) kv.Add(("panel_pinned", J.Truthy(b["panel_pinned"])));
        if (kv.Count == 0) return;
        S.Set(kv.ToArray());
        lock (Lock) Bump();
        PanelSettingsChanged?.Invoke();
    }

    /// <summary>Moving other apps' windows is a system-changing action: only the settings page (SettingsBridge), never HTTP.</summary>
    public void SetPushWindows(bool on)
    {
        S.Set(("panel_push_windows", on));
        lock (Lock) Bump();
        PanelSettingsChanged?.Invoke();
    }
}
