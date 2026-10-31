using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// Vast.ai instances (console.vast.ai): every instance is a task (va-&lt;id&gt;). The v1 list pages by after_token (25 a
/// page) and carries the load (often null); each running instance is also read by id for fresher charts. Spent so far
/// from /api/v0/charges (source "instance-&lt;id&gt;", last 30 days); balance and credit from /api/v0/users/current (which
/// one is spendable is undocumented: both are shown, both raw values kept). Vast sends no rate-limit headers: a 429
/// pauses everything 60 s, doubling (the base). Config (connectors.vastai): see MachineConnector. Key: Credential
/// Manager "vastai".
/// </summary>
sealed class VastAiConnector : MachineConnector
{
    public VastAiConnector(Settings s, Hub h) : base(s, h) { }

    public override string Id => "vastai";
    public override string Title => "Vast.ai";
    protected override string Prefix => "va";
    protected override string DefaultApi => "https://console.vast.ai";
    protected override bool HasDisk => true;
    protected override string RestartAction => "reboot";
    protected override string ZeroBalanceNote => L.T("va_zero");

    // a start that waits for the host's GPU ("scheduling"): since when (machine id -> unix s)
    readonly Dictionary<string, double> scheduling = new();
    double SchedulingAfter => ApiUri.IsLoopback ? 2 : 60;

    // ------------------------------------------------------------ reads
    protected override Task<Resp> CheckKey(string key, CancellationToken ct) => Call(HttpMethod.Get, "/api/v1/instances/?limit=1", null, key, ct);

    static readonly string Order = Uri.EscapeDataString("[{\"col\":\"id\",\"dir\":\"asc\"}]");

    protected override async Task<List<Machine>> List(string key, CancellationToken ct)
    {
        var res = new List<Machine>();
        string after = null;
        for (int page = 0; page < 50; page++)
        {
            var r = await Call(HttpMethod.Get, "/api/v1/instances/?limit=25&order_by=" + Order +
                (after != null ? "&after_token=" + Uri.EscapeDataString(after) : ""), null, key, ct);
            if (r.Code == 401) { KeyRejected(); return null; }
            if (r.Code == 403) { status = "no access to instances (instance_read)"; return null; }
            if (r.Code != 200) { if (!r.Limited) status = $"Vast.ai: HTTP {r.Code}" + (ErrText(r) is { Length: > 0 } e ? " · " + e : ""); return null; }
            foreach (var p in (r.Body?["instances"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Map(p) is { } m) res.Add(m);
            after = J.Str(r.Body?["next_token"]) is { Length: > 0 } nt ? nt : null;
            if (after == null) { Scheduling(res); return res; }
        }
        return null;   // more than 50 pages: not complete - don't declare anything gone
    }

    /// <summary>GET /api/v0/instances/{id}/ answers {"instances": {...}} (an object; null when it is gone).</summary>
    protected override async Task<Machine> Detail(Machine m, string key, CancellationToken ct)
    {
        var r = await Call(HttpMethod.Get, "/api/v0/instances/" + Uri.EscapeDataString(m.Id) + "/", null, key, ct);
        if (r.Code != 200 || r.Body?["instances"] is not JsonObject p || Map(p) is not { } d) return null;
        Scheduling(new List<Machine> { d });
        return d.State == "running" ? d : null;   // a changed state comes with the next list
    }

    /// <summary>A start waiting for its GPU (the host rented it out meanwhile) says so in clear words after a while.</summary>
    void Scheduling(List<Machine> ms)
    {
        var now = J.Now();
        lock (scheduling)
            foreach (var m in ms)
            {
                bool sched = m.State == "starting" && Regex.IsMatch(m.RawStatus, "schedul", RegexOptions.IgnoreCase);
                if (!sched) { scheduling.Remove(m.Id); continue; }
                if (!scheduling.TryGetValue(m.Id, out var since)) scheduling[m.Id] = since = now;
                if (now - since >= SchedulingAfter) m.RawStatus = L.T("va_scheduling");
            }
    }

    static double? Num(JsonNode n) => n is JsonValue && !J.IsStr(n) ? J.Num(n) : null;

    /// <summary>% from a value that may be a fraction (0..1) or already a percentage - the docs and the CLI disagree.</summary>
    static double? Frac(double? v) => v is double d && d >= 0 ? (d <= 1 ? d * 100 : d) : null;

    static double? Ratio(double? used, double? total) => used is double u && total is double t && t > 0 && u >= 0 ? u / t * 100 : null;

    Machine Map(JsonObject p)
    {
        var idn = p["id"];
        var id = Num(idn) is double nid ? ((long)nid).ToString(CultureInfo.InvariantCulture) : J.Str(idn);
        if (id.Length == 0) return null;
        var actual = J.Str(p["actual_status"]).ToLowerInvariant();
        var intended = J.Str(p["intended_status"]).ToLowerInvariant();
        var cur = J.Str(p["cur_state"]).ToLowerInvariant();
        var msg = J.Str(p["status_msg"]).Trim();
        string state;
        // the value sets are undocumented: actual_status running / loading / created / exited / offline..., intended_status
        // running / stopped, cur_state running / stopped / unloaded
        if (actual == "offline" || actual.Contains("error")) state = "error";
        else if (intended == "stopped") state = "stopped";          // a stop asked for: the GPU is released
        else if (actual == "running") state = "running";
        else if (intended == "running") state = "starting";         // loading / created / scheduling (waits for its GPU)...
        else state = "stopped";   // exited / unknown with nothing asked: not billed for the GPU as far as we know
        // what the start waits for: Vast says "scheduling" in actual_status or status_msg
        var raw = actual.Length > 0 ? actual : intended;
        if (state == "starting" && (actual == "scheduling" || msg.Contains("schedul", StringComparison.OrdinalIgnoreCase) || cur == "scheduling")) raw = "Scheduling";
        var label = J.Str(p["label"]).Trim();
        var m = new Machine
        {
            Id = id, Name = label.Length > 0 ? label : "instance " + id, RawStatus = raw, Raw = p, State = state,
            Gpu = J.Str(p["gpu_name"]).Trim(), GpuCount = (int)(Num(p["num_gpus"]) ?? 1),
            Region = J.Str(p["geolocation"]).Trim(),
            Image = J.Str(p["image_uuid"]).Trim(),
            Price = Num(p["dph_total"]),
            Started = Ts(p["start_date"]),   // `duration` is not uptime
        };
        if (Num(p["uptime_mins"]) is double um && um >= 0) m.Uptime = um * 60;
        m.Allowed = state switch
        {
            "running" => new HashSet<string> { "stop", "reboot" },
            "starting" => new HashSet<string> { "stop" },
            "stopped" => new HashSet<string> { "start" },
            _ => null,   // error: let the user try
        };
        if (state == "running")
        {
            // load: null when Vast didn't measure it. gpu_util is a percentage in the CLI ("Util. %"), a fraction in the
            // docs; cpu_util a fraction; memory in MB; disk_util a fraction (the CLI multiplies by 100)
            if (Num(p["gpu_util"]) is double gu && gu >= 0) m.GpuUtil.Add(gu); else m.GpuUtil.Add(null);
            m.GpuMem = Ratio(Num(p["vmem_usage"]), Num(p["gpu_totalram"]) ?? Num(p["gpu_ram"]));
            m.Cpu = Frac(Num(p["cpu_util"]));
            m.Ram = Ratio(Num(p["mem_usage"]), Num(p["mem_limit"]) ?? Num(p["cpu_ram"]));
            m.Disk = Frac(Num(p["disk_util"])) ?? Ratio(Num(p["disk_usage"]), Num(p["disk_space"]));
            if (m.GpuUtil.All(x => x == null) && m.GpuMem == null && m.Cpu == null && m.Ram == null) m.GpuUtil.Clear();   // nothing measured
            var host = J.Str(p["ssh_host"]).Trim();
            if (Regex.IsMatch(host, @"^[A-Za-z0-9.-]{1,253}$") && Num(p["ssh_port"]) is double sp && sp is > 0 and < 65536)
                m.Ssh = $"ssh -p {(int)sp} root@{host}";
            // no https proxy URL in the API (direct ports are plain / self-signed): the console, where its links are
            m.Links.Add(("Vast.ai console", "https://cloud.vast.ai/instances/"));
        }
        return m;
    }

    /// <summary>Spent per instance over the last 30 days from the charges (source "instance-&lt;id&gt;"); trails the meter -> "≈".</summary>
    protected override async Task<Dictionary<string, double>> Spend(string key, CancellationToken ct)
    {
        var now = (long)J.Now();
        var filters = new JsonObject
        {
            ["day"] = new JsonObject { ["gte"] = now - 30 * 86400, ["lte"] = now },
            ["type"] = new JsonObject { ["in"] = new JsonArray("instance") },
        };
        var d = new Dictionary<string, double>();
        string after = null;
        for (int page = 0; page < 20; page++)
        {
            var r = await Call(HttpMethod.Get, "/api/v0/charges/?limit=100&select_filters=" + Uri.EscapeDataString(filters.ToJsonString()) +
                (after != null ? "&after_token=" + Uri.EscapeDataString(after) : ""), null, key, ct);
            if (r.Code != 200) { if (r.Code != 429) Log.W($"vastai: charges HTTP {r.Code}"); return page == 0 ? null : d; }
            foreach (var rec in (r.Body?["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var mm = Regex.Match(J.Str(rec["source"]), @"^instance-(\d{1,20})$");
                if (!mm.Success || Num(rec["amount"]) is not double a) continue;
                d[mm.Groups[1].Value] = d.GetValueOrDefault(mm.Groups[1].Value) + a;
            }
            after = J.Str(r.Body?["next_token"]) is { Length: > 0 } nt ? nt : null;
            if (after == null) break;
        }
        return d;
    }

    static string Money(double v) => "$" + v.ToString(Math.Abs(v) < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);

    /// <summary>/api/v0/users/current: `balance` (docs) and `credit` (CLI) - both shown, labelled; credit counts for the
    /// runway when it is there. The answer also carries the user's API key: only the money fields are read.</summary>
    protected override async Task<Balance> FetchBalance(string key, CancellationToken ct)
    {
        var r = await Call(HttpMethod.Get, "/api/v0/users/current", null, key, ct);
        if (r.Code != 200 || r.Body is not JsonObject u) { if (r.Code != 429) Log.W($"vastai: balance HTTP {r.Code}"); return null; }
        double? bal = Num(u["balance"]), credit = Num(u["credit"]);
        if (bal == null && credit == null) return null;
        var b = new Balance { Amount = credit ?? bal };
        foreach (var k in new[] { "balance", "credit", "can_pay", "has_billing" })
            if (u.ContainsKey(k) && u[k] is null or JsonValue) b.Raw[k] = J.C(u[k]);
        // what Amount is: "Credit" (with the balance beside it when Vast gives both)
        b.Label = credit != null
            ? (bal != null ? L.T("m_balance") + " " + Money(bal.Value) + " · " : "") + L.T("va_credit")
            : "";
        return b;
    }

    /// <summary>Storage of a stopped instance keeps billing (host-specific, often more than while running): storage_cost ×
    /// disk_space. storage_cost is $/GB/month in the CLI (the docs say "per hour" - conflict, see .ai/gotchas.md): the CLI
    /// meaning is used, shown with "≈".</summary>
    protected override (double? PerDay, string Note) StoppedStorage(Machine m) =>
        Num(m.Raw?["storage_cost"]) is double sc && sc >= 0 && Num(m.Raw?["disk_space"]) is double gb && gb >= 0
            ? (sc * gb / 30, "") : (null, "");

    // ------------------------------------------------------------ actions
    /// <summary>Vast answers some failures with 200 {"success": false, "msg": ...}: read as an error (403 when it is about
    /// permissions, else 400).</summary>
    static Resp Norm(Resp r)
    {
        if (r.Code is >= 200 and < 300 && r.Body?["success"] is JsonNode s && !J.Truthy(s))
        {
            var t = ErrText(r) + " " + J.Str(r.Body?["error"]);
            return new Resp
            {
                Code = Regex.IsMatch(t, @"permission|forbidden|not authori[sz]ed|scope|read.?only", RegexOptions.IgnoreCase) ? 403
                    : Regex.IsMatch(t, @"not found|no such|does not exist", RegexOptions.IgnoreCase) ? 404 : 400,
                Body = r.Body, Limited = r.Limited,
            };
        }
        return r;
    }

    /// <summary>A label on instance 0 (can't exist): 403 = the key may not act (instance_write), 400 / 404 = it may (to be
    /// verified with a real read-only key).</summary>
    protected override async Task<Resp> Probe(string key, CancellationToken ct) =>
        Norm(await Call(HttpMethod.Put, "/api/v0/instances/0/", new JsonObject { ["label"] = "taskhub-probe" }, key, ct));

    protected override async Task<Resp> Act(Machine m, string action, string key, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(m.Id);
        var r = action switch
        {
            "delete" => await Call(HttpMethod.Delete, "/api/v0/instances/" + id + "/", null, key, ct),   // destroy: irreversible
            "reboot" => await Call(HttpMethod.Put, "/api/v0/instances/reboot/" + id + "/", null, key, ct),
            _ => await Call(HttpMethod.Put, "/api/v0/instances/" + id + "/",
                new JsonObject { ["state"] = action == "start" ? "running" : "stopped" }, key, ct),
        };
        if (action == "start" && Norm(r).Code is >= 200 and < 300) lock (scheduling) scheduling.Remove(m.Id);
        return Norm(r);
    }

    protected override string ActionError(Resp r, string action)
    {
        var detail = ErrText(r);
        // start of a stopped instance whose host rented the GPU out meanwhile
        if (action == "start" && r.Code is 400 or 409 &&
            Regex.IsMatch(detail, @"gpu|resources|not enough|no longer|available|capacity|schedul", RegexOptions.IgnoreCase))
            return L.T("m_err_gpu") + (detail.Length > 0 ? " · " + detail : "");
        return null;
    }
}
