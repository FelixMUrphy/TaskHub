using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// Runpod Pods (REST v2, api.runpod.io): every pod is a task (rp-&lt;podId&gt;). The list carries no live load
/// (runtime is null there), so each running pod is read by id for the charts. Spent so far from /v2/billing/pods
/// (last 30 days, per pod); balance from the GraphQL module below (connectors.runpod.graphql_balance, default on).
/// Config (connectors.runpod): see MachineConnector + graphql_balance. Key: Credential Manager "runpod".
/// </summary>
sealed class RunpodConnector : MachineConnector
{
    public RunpodConnector(Settings s, Hub h) : base(s, h) { }

    public override string Id => "runpod";
    public override string Title => "Runpod";
    protected override string Prefix => "rp";
    protected override string DefaultApi => "https://api.runpod.io";
    protected override string ZeroBalanceNote => L.T("rp_zero");

    bool GraphQl => RawConfig["graphql_balance"] is not JsonNode n || J.Truthy(n);   // default on

    protected override void ExtraConfig(JsonObject b, JsonObject c)
    {
        if (b.ContainsKey("graphql_balance")) c["graphql_balance"] = J.Truthy(b["graphql_balance"]);
    }

    protected override void ExtraState(JsonObject s) => s["graphql_balance"] = GraphQl;

    protected override bool BalanceSource => GraphQl;

    // clientBalance moves in steps (charges booked every ~10 min, live test 2026-10-31): a threshold can be crossed late
    protected override string GuardHint => L.T("g_hint_runpod");

    // Runpod's price list (https://docs.runpod.io/pods/pricing): a stopped pod's container disk is wiped and not billed;
    // its volume disk (mounts.persistent.size, GB) costs $0.20 / GB / month while stopped; network volumes $0.07 / GB / month
    // (up to 1 TB, $0.05 above), the same running or stopped. The API has no price field. The pod carries only a network
    // volume's id: its size comes from GET /v2/network-volumes/{id} (see VolumeSizes); a high-performance volume has no
    // published price - like an unread size it is left out with a note.
    const double VolumeStopped = 0.20, NetVolume = 0.07, NetVolumeOver1Tb = 0.05;

    protected override (double? PerDay, string Note) StoppedStorage(Machine m)
    {
        var mounts = m.Raw?["mounts"] as JsonObject;
        double month = 0;
        bool known = true;
        if (J.Num(mounts?["persistent"]?["size"]) is double gb && gb > 0) month += gb * VolumeStopped;
        else if (J.Num(m.Raw?["volumeInGb"]) is double gb2 && gb2 > 0) month += gb2 * VolumeStopped;
        else if (m.Raw == null) known = false;
        var note = "";
        foreach (var nv in (mounts?["network"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            double? size = J.Num(nv["size"]);
            if (size == null && volumes.TryGetValue(J.Str(nv["volumeId"]), out var v) && v.Gb != null && v.Type is "" or "STANDARD") size = v.Gb;
            if (size is double ns && ns > 0) month += Math.Min(ns, 1000) * NetVolume + Math.Max(0, ns - 1000) * NetVolumeOver1Tb;
            else note = L.T("g_netvol");   // its size / price isn't known: billed as before, not counted here
        }
        return known ? (month / 30, note) : (null, note);
    }

    /// <summary>Network volume id -> size (GB) and type, read when needed; a failed read is tried again after 10 min, a
    /// known size re-read hourly (volumes can be resized).</summary>
    readonly ConcurrentDictionary<string, (double? Gb, string Type, double At)> volumes = new();

    async Task VolumeSizes(List<Machine> ms, string key, CancellationToken ct)
    {
        var ids = ms.Where(m => m.State != "terminated")
            .SelectMany(m => (m.Raw?["mounts"]?["network"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Where(n => J.Num(n["size"]) == null).Select(n => J.Str(n["volumeId"]))
            .Where(id => id.Length is > 0 and <= 100).Distinct().ToList();
        int n = 0;
        foreach (var id in ids)
        {
            if (volumes.TryGetValue(id, out var v) && J.Now() - v.At < (v.Gb != null ? 3600 : Mock ? 2 : 600)) continue;
            if (++n > 5) return;   // a few per poll
            try
            {
                var r = await Call(HttpMethod.Get, "/v2/network-volumes/" + Uri.EscapeDataString(id), null, key, ct);
                var gb = r.Code == 200 ? J.Num(r.Body?["size"]) : null;
                volumes[id] = gb > 0 ? (gb, J.Str(r.Body?["type"]).ToUpperInvariant(), J.Now()) : (v.Gb, v.Type ?? "", J.Now());
                if (gb == null) Log.W($"runpod: network volume size: HTTP {r.Code}");
                if (r.Limited || r.Code == 401) return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                volumes[id] = (v.Gb, v.Type ?? "", J.Now());
                Log.W("runpod: network volume size: " + e.GetBaseException().Message);
            }
        }
    }

    // ------------------------------------------------------------ reads
    protected override Task<Resp> CheckKey(string key, CancellationToken ct) => Call(HttpMethod.Get, "/v2/pods?limit=1", null, key, ct);

    protected override async Task<List<Machine>> List(string key, CancellationToken ct)
    {
        var res = new List<Machine>();
        string cursor = null;
        for (int page = 0; page < 50; page++)
        {
            var r = await Call(HttpMethod.Get, "/v2/pods?limit=100" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : ""), null, key, ct);
            if (r.Code == 401) { KeyRejected(); return null; }
            if (r.Code == 403) { status = "no access to pods (pods:read)"; return null; }
            if (r.Code != 200) { if (!r.Limited) status = $"Runpod: HTTP {r.Code}"; return null; }
            foreach (var p in (r.Body?["pods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) res.Add(Map(p));
            var pg = r.Body?["pagination"];
            cursor = J.Truthy(pg?["hasNextPage"]) && J.Str(pg?["nextCursor"]) is { Length: > 0 } nc ? nc : null;
            if (cursor == null)
            {
                await VolumeSizes(res, key, ct);
                return res;
            }
        }
        return null;   // more than 50 pages: not complete - don't declare anything gone
    }

    protected override async Task<Machine> Detail(Machine m, string key, CancellationToken ct)
    {
        var r = await Call(HttpMethod.Get, "/v2/pods/" + Uri.EscapeDataString(m.Id), null, key, ct);
        return r.Code == 200 && r.Body is JsonObject p ? Map(p) : null;
    }

    static Machine Map(JsonObject p)
    {
        var id = J.Str(p["id"]);
        var st = J.Str(p["status"]).ToUpperInvariant();
        var m = new Machine
        {
            Id = id, Name = J.Str(p["name"], id), RawStatus = st, Raw = p,
            State = st switch
            {
                "RUNNING" => "running",
                "PROVISIONING" or "STARTING" => "starting",
                "ERROR" => "error",
                "TERMINATED" => "terminated",
                _ => "stopped",   // EXITED (and anything unknown: not billed for compute as far as we know)
            },
            Region = string.Join(" ", new[] { J.Str(p["dataCenterId"]), J.Str(p["cloud"]) is { Length: > 0 } cl ? cl.ToLowerInvariant() : "" }.Where(x => x.Length > 0)),
            Image = J.Str(p["image"]),
            Price = J.Num(p["cost"]),
            Started = Ts(p["startedAt"]),
        };
        if (p["gpu"] is JsonObject g)
        {
            m.Gpu = J.Str(g["id"]);
            m.GpuCount = (int)(J.Num(g["count"]) ?? 1);
        }
        else if (p["cpu"] is JsonObject c) m.Gpu = "CPU " + J.Str(c["id"]);
        m.Gpu = m.Gpu.Trim();
        if (p["actions"] is JsonArray acts) m.Allowed = acts.Select(x => J.Str(x)).ToHashSet();
        m.Volume = p["mounts"]?["network"] is JsonArray nv && nv.Count > 0;
        // live load: only on GET by id, only while running (null in the list)
        if (p["runtime"] is JsonObject rt)
        {
            m.Uptime = J.Num(rt["uptime"]);
            foreach (var gu in (rt["gpus"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) m.GpuUtil.Add(J.Num(gu["util"]));
            var mems = (rt["gpus"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(x => J.Num(x["memoryUtil"])).Where(x => x != null).ToList();
            m.GpuMem = mems.Count > 0 ? mems.Max() : null;
            m.Cpu = J.Num(rt["cpu"]?["util"]);
            m.Ram = J.Num(rt["memory"]?["util"]);
        }
        var ssh = p["ssh"];
        m.Ssh = J.Str(ssh?["direct"]?["command"]) is { Length: > 0 } d ? d : J.Str(ssh?["proxy"]?["command"]) is { Length: > 0 } px ? px : null;
        // HTTP ports through Runpod's proxy (https only): https://{podId}-{port}.proxy.runpod.net
        if (Regex.IsMatch(id, @"^[A-Za-z0-9]{1,40}$"))
            foreach (var port in (p["ports"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)))
            {
                var mp = Regex.Match(port, @"^(\d{1,5})/http$");
                if (!mp.Success) continue;
                var n = mp.Groups[1].Value;
                m.Links.Add((n == "8888" ? "Jupyter" : "HTTP " + n, $"https://{id.ToLowerInvariant()}-{n}.proxy.runpod.net"));
            }
        return m;
    }

    /// <summary>Spent per pod over the last 30 days (daily buckets) - billing lags a little, so shown with "≈".</summary>
    protected override async Task<Dictionary<string, double>> Spend(string key, CancellationToken ct)
    {
        var r = await Call(HttpMethod.Get, "/v2/billing/pods?bucketSize=day&lastN=30", null, key, ct);
        if (r.Code != 200) return null;
        var d = new Dictionary<string, double>();
        foreach (var rec in (r.Body?["records"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var id = J.Str(rec["podId"]);
            if (id.Length == 0 || J.Num(rec["totalAmount"]) is not double a) continue;
            d[id] = d.GetValueOrDefault(id) + a;
        }
        return d;
    }

    string billingMode;      // myself.billingMode: PREPAID | POSTPAID (null = unknown), read hourly, per key
    double billingModeAt;
    int billingModeKey;

    /// <summary>The balance; on a post-paid account (invoiced afterwards, pods aren't stopped at $0) clientBalance isn't
    /// money left before a stop: the guard only warns (<see cref="Balance.NoStop"/>, see .ai/gotchas.md).</summary>
    protected override async Task<Balance> FetchBalance(string key, CancellationToken ct)
    {
        if (!GraphQl) return null;
        Task<Resp> Post(JsonNode body, CancellationToken c) => Call(HttpMethod.Post, "/graphql", body, key, c);
        var b = await RunpodGraphQlBalance.Fetch(Post, ct);
        if (b is not { Error: null }) return b;
        var kh = key.GetHashCode();
        if (kh != billingModeKey || J.Now() - billingModeAt > (Mock ? 2 : 3600))
        {
            if (kh != billingModeKey) billingMode = null;
            billingModeKey = kh;
            billingModeAt = J.Now();
            try { billingMode = await RunpodGraphQlBalance.BillingMode(Post, ct) ?? billingMode; }
            catch (Exception e) when (!ct.IsCancellationRequested) { Log.W("runpod: billingMode: " + e.GetBaseException().Message); }
        }
        if (billingMode != null) b.Raw["billingMode"] = billingMode;
        if (billingMode == "POSTPAID") b.NoStop = L.T("g_nostop_postpaid");
        return b;
    }

    // ------------------------------------------------------------ actions
    /// <summary>An action on a pod that can't exist: 403 = the key may not run actions (pods:write), 404 / 409 / 400 / 422 =
    /// it may (to be verified with a real read-only key).</summary>
    protected override Task<Resp> Probe(string key, CancellationToken ct) =>
        Call(HttpMethod.Post, "/v2/pods/pod_taskhub_probe/action", new JsonObject { ["action"] = "stop" }, key, ct);

    protected override Task<Resp> Act(Machine m, string action, string key, CancellationToken ct)
    {
        var path = "/v2/pods/" + Uri.EscapeDataString(m.Id);
        return action == "delete"
            ? Call(HttpMethod.Delete, path, null, key, ct)   // terminate: the pod and its disk are gone
            : Call(HttpMethod.Post, path + "/action", new JsonObject { ["action"] = action }, key, ct);
    }

    protected override string ActionError(Resp r, string action)
    {
        var detail = ErrText(r);
        // start of a stopped pod whose host has no free GPU any more
        if (action == "start" && r.Code is 400 or 409 &&
            Regex.IsMatch(detail, @"gpu|not enough|no longer|available|capacity", RegexOptions.IgnoreCase))
            return L.T("m_err_gpu") + (detail.Length > 0 ? " · " + detail : "");
        return null;
    }

    /// <summary>RateLimit: "minute";r=0;t=12, "hour";r=2800;t=1812 - a used-up window pauses everything until it resets.</summary>
    protected override void OnHeaders(HttpResponseMessage r)
    {
        if (!r.Headers.TryGetValues("RateLimit", out var vals)) return;
        foreach (Match mm in Regex.Matches(string.Join(",", vals), @"""?([A-Za-z]+)""?\s*;\s*r=(\d+)\s*;\s*t=(\d+)"))
            if (mm.Groups[2].Value == "0" && double.TryParse(mm.Groups[3].Value, out var t) && (int)r.StatusCode != 429)
                Pause(Math.Max(1, t), "RateLimit " + mm.Groups[1].Value);
    }
}

/// <summary>Runpod balance through GraphQL `myself { clientBalance currentSpendPerHr spendLimit }` - REST v2 has no balance
/// yet. Kept apart so it can be switched off (connectors.runpod.graphql_balance) or dropped when GraphQL retires (early
/// 2027) or REST gets a balance endpoint.</summary>
static class RunpodGraphQlBalance
{
    const string Query = "query TaskHubBalance { myself { clientBalance currentSpendPerHr spendLimit } }";

    public static async Task<Balance> Fetch(Func<JsonNode, CancellationToken, Task<MachineConnector.Resp>> post, CancellationToken ct)
    {
        var r = await post(new JsonObject { ["query"] = Query }, ct);
        if (r.Code != 200 || r.Body?["data"]?["myself"] is not JsonObject me)
        {
            if (r.Code != 200 || r.Body?["errors"] != null) Log.W($"runpod: balance (GraphQL) HTTP {r.Code}");
            return r.Code != 200 ? Balance.Failed(r.Code) : new Balance { Error = "no balance in the answer", Code = 200 };
        }
        var b = new Balance
        {
            Amount = J.Num(me["clientBalance"]), SpendPerHr = J.Num(me["currentSpendPerHr"]), SpendLimit = J.Num(me["spendLimit"]),
        };
        foreach (var k in new[] { "clientBalance", "currentSpendPerHr", "spendLimit" }) b.Raw[k] = J.C(me[k]);
        return b.Amount == null && b.SpendPerHr == null ? new Balance { Error = "no balance in the answer", Code = 200 } : b;
    }

    /// <summary>`myself { billingMode }` (PREPAID | POSTPAID, GraphQL spec) in its own query, so an unknown field can't
    /// break the balance; null when not answered.</summary>
    public static async Task<string> BillingMode(Func<JsonNode, CancellationToken, Task<MachineConnector.Resp>> post, CancellationToken ct)
    {
        var r = await post(new JsonObject { ["query"] = "query TaskHubBillingMode { myself { billingMode } }" }, ct);
        var v = r.Code == 200 ? J.Str(r.Body?["data"]?["myself"]?["billingMode"]).ToUpperInvariant() : "";
        return v is "PREPAID" or "POSTPAID" ? v : null;
    }
}
