using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

sealed class ApiError : Exception
{
    public int Code;
    /// <summary>A button that is no longer there / no longer applies (Telegram and toasts answer "already handled").</summary>
    public bool Stale { get; init; }
    /// <summary>The text for the settings page (UI language) when it differs from the API's English message.</summary>
    public string Ui { get; init; }
    public ApiError(int code, string msg) : base(msg) { Code = code; }
}

interface INotifier
{
    /// <param name="task">snapshot of the task (may be null) - used for toast buttons</param>
    /// <param name="dismissOnly">clicking the toast only closes it (no panel) - for toasts not tied to a task</param>
    /// <param name="controls">false: no control buttons / inputs (plain toast)</param>
    void Toast(string title, string msg, string level, string taskId, JsonObject task, bool dismissOnly = false, bool controls = true);
    /// <summary>About this PC, not worth a phone: not mirrored to Telegram. A click opens the panel.</summary>
    void LocalToast(string title, string msg);
    void Progress(JsonObject task);
    void ProgressEnd(string taskId);
    void Refresh(string taskId);   // a control value changed -> re-render shown toasts
    string SystemSetting { get; }  // Windows' switch for our notifications: "enabled", "disabled_app", ... (Notifier)
    int QueueLength { get; }       // notifications waiting (rate limit + toast worker)
}

/// <summary>Task registry - a port of the Python Hub class; tasks are JsonObjects with the same shape.</summary>
sealed partial class Hub
{
    public static readonly string[] Active = { "running", "paused", "stale" };
    public static readonly string[] Final = { "done", "failed", "cancelled", "lost" };
    public static readonly string[] AllStates = Active.Concat(Final).ToArray();
    static readonly string[] WidgetTypes = { "text", "progress", "gauge", "stat", "led", "kv", "table", "chart" };
    static readonly Regex IdRx = new(@"^[A-Za-z0-9_.-]{1,64}$");
    static readonly Regex CidRx = new(@"^[A-Za-z0-9_.:-]{1,48}$");

    public static bool IsActive(string s) => Array.IndexOf(Active, s) >= 0;
    public static bool IsFinal(string s) => Array.IndexOf(Final, s) >= 0;

    static readonly string[] SnapKeys = { "id", "name", "state", "status", "stage", "progress", "current", "total", "unit",
                                          "finished", "controls", "links" };

    /// <summary>What a toast / the Telegram mirror needs of a task (buttons, links, progress; charts once it has finished)
    /// - not the log or the rest: a queued notification holds this, not a copy of the whole task.</summary>
    public static JsonObject ToastSnap(JsonObject t)
    {
        var s = new JsonObject();
        foreach (var k in SnapKeys) if (t[k] is { } v) s[k] = v.DeepClone();
        if (IsFinal(J.Str(t["state"])) && t["widgets"] is JsonArray w)
            s["widgets"] = new JsonArray(w.OfType<JsonObject>().Where(x => J.Str(x["type"]) == "chart").Select(x => (JsonNode)x.DeepClone()).ToArray());
        return s;
    }

    sealed class Internal
    {
        public List<JsonObject> CmdQ = new();
        public long? Ct;
        public double LastProgressToast;
        public bool PidGone;   // the watchdog's last look found the process gone
        public readonly List<(double T, double? Cur, double? P)> Pace = new();   // recent progress samples (rate / ETA)
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
        if (!File.Exists(statePath)) return;
        try { ReadState(statePath); }
        catch (Exception e)
        {
            Log.E("hub", "state.json unreadable: " + e.Message);
            AtomicFile.Quarantine(statePath);
            tasks.Clear(); internals.Clear();
            try
            {
                ReadState(AtomicFile.Backup(statePath));
                File.Copy(AtomicFile.Backup(statePath), statePath, true);   // also keeps the damaged file from becoming the next backup
                stateDamaged = "damaged_state_restored";
            }
            catch (Exception e2)
            {
                Log.E("hub", "no good copy of state.json: " + e2.Message);
                tasks.Clear(); internals.Clear();
                stateDamaged = "damaged_state_empty";
            }
        }
    }

    string stateDamaged;

    void ReadState(string path)
    {
        var st = J.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("not a JSON object");
        Muted = J.Truthy(st["muted"]);
        foreach (var n in st["tasks"] as JsonArray ?? new JsonArray())
        {
            if (n is not JsonObject t0) continue;
            var t = (JsonObject)t0.DeepClone();
            var id = J.Str(t["id"]);
            long? ct = J.Num(t["_ct"]) is double d && d > 1e12 ? (long)d : null;
            t.Remove("_ct");
            t.Remove("rate"); t.Remove("eta_at");   // measured again from the next updates
            tasks[id] = t;
            internals[id] = new Internal { Ct = ct };
        }
        Log.I("hub", $"restored {tasks.Count} task(s)");
    }

    /// <summary>Tells the user about a file that couldn't be read at start: a failed task in the panel, with its notification. Call once the notifier is set.</summary>
    public void ReportDamagedFiles()
    {
        void Report(string id, string title, string message)
        {
            Register(new JsonObject { ["id"] = id, ["name"] = title, ["source"] = "taskhub" });
            Finish(id, new JsonObject { ["state"] = "failed", ["message"] = message });
        }
        if (S.ConfigDamaged != null)
            Report("taskhub-config-damaged", L.T("damaged_config_title"), L.T(S.ConfigDamaged == Settings.DamagedRestored ? "damaged_config_restored" : "damaged_config_defaults"));
        if (stateDamaged != null) Report("taskhub-state-damaged", L.T("damaged_state_title"), L.T(stateDamaged));
    }

    readonly object saveLock = new();

    public void Save()
    {
        lock (saveLock)   // watchdog and shutdown may save at the same time: one writer for state.json(.tmp)
        {
            JsonObject snap;
            lock (Lock)   // only the copy under the lock: text and disk don't hold up the API and the panel
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
                snap = new JsonObject { ["muted"] = Muted, ["tasks"] = arr };
            }
            try
            {
                AtomicFile.Write(statePath, snap.ToJsonString(J.Out));
            }
            catch (Exception e)
            {
                Log.E("hub", "save failed: " + e.Message);
                lock (Lock) dirty = true;   // try again on the next round
            }
        }
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

    const double PaceWindow = 120, PaceMinSpan = 3;

    /// <summary>Current speed ("rate", units/s) and ETA ("eta_at", unix time) from the progress updates of the last
    /// ~2 minutes, not since the start: a job that resumes where it stopped, or speeds up / slows down, shows its real
    /// pace. Nothing while paused or until the samples span a few seconds.</summary>
    static void UpdatePace(JsonObject t, Internal it, double now)
    {
        var s = it.Pace;
        double? cur = J.Num(t["current"]), p = J.Num(t["progress"]);
        if (St(t) != "running" || (cur == null && p == null && t["expected_duration"] == null)) { s.Clear(); t.Remove("rate"); t.Remove("eta_at"); return; }
        if (s.Count > 0 && (cur < s[^1].Cur || p < s[^1].P)) s.Clear();   // went back: a new run of the job
        s.Add((now, cur, p));
        while (s.Count > 2 && now - s[1].T >= PaceWindow) s.RemoveAt(0);
        var (a, z) = (s[0], s[^1]);
        double dt = z.T - a.T;
        double? rate = null, eta = null;
        if (dt >= PaceMinSpan)
        {
            if (a.Cur is double c0 && z.Cur is double c1)
            {
                rate = (c1 - c0) / dt;
                if (J.Num(t["total"]) is double tot && rate > 0) eta = Math.Max(0, tot - c1) / rate;
            }
            if (eta == null && a.P is double p0 && z.P is double p1 && p1 > p0) eta = (1 - p1) * dt / (p1 - p0);
        }
        // a known usual duration (e.g. earlier runs of the same CI workflow) says more than uneven steps; none once overdue
        if (J.Num(t["expected_duration"]) is double exp && J.Num(t["started"]) is double st)
            eta = st + exp > now ? st + exp - now : null;
        if (rate is double r && double.IsFinite(r)) t["rate"] = Math.Round(r, 6); else t.Remove("rate");
        if (eta is double e && double.IsFinite(e)) t["eta_at"] = Math.Round(now + e, 1); else t.Remove("eta_at");
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
        Notifier?.Toast(title, msg, level, J.Str(t["id"]), ToastSnap(t), controls: controls);
    }

    void AddLog(JsonObject t, JsonNode msg, string level = "info")
    {
        var log = t["log"] as JsonArray;
        if (log == null) t["log"] = log = new JsonArray();
        int keep = Math.Max(0, S.Int("log_lines", 400));
        // of a batch only what the log can keep; this runs under Hub.Lock
        IEnumerable<JsonNode> lines = msg is JsonArray a ? a.Skip(Math.Max(0, a.Count - keep)).ToList() : new List<JsonNode> { msg };
        foreach (var m0 in lines)
        {
            var m = J.Str(m0).TrimEnd('\r', '\n');
            if (m.Length == 0) continue;
            log.Add(new JsonObject { ["t"] = J.Now(), ["level"] = level, ["msg"] = J.Cut(m, 2000) });
        }
        if (log.Count > keep) log.RemoveRange(0, log.Count - keep);
    }
    void AddLog(JsonObject t, string msg, string level = "info") => AddLog(t, JsonValue.Create(msg), level);
}
