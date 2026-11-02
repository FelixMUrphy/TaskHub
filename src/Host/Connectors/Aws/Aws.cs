using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TaskHub;

/// <summary>
/// AWS, read-only: every EC2 instance is a task (aws-&lt;instance id&gt;), active while it runs, finished when it stops or
/// is gone. Regions: DescribeRegions (the enabled ones) unless the user lists their own. DescribeInstances per Region,
/// one request at a time, every list_interval (10 min). A Region that refuses or fails is a state of that Region: it never
/// declares its instances gone and never condemns the key. A machine running longer than forgotten_hours gets a "needs
/// you" line, with a button for the ones meant to run. Never starts, stops or changes anything in the account.
/// Config (connectors.aws): enabled, regions, forgotten_hours, forgotten_exempt, list_interval, api (tests: a loopback
/// mock only). Key: Credential Manager "aws", as "access key id:secret". Own SigV4 signer, no SDK (see SigV4.cs).
/// </summary>
sealed class AwsConnector : ISettingsConnector
{
    public string Id => "aws";
    public string Title => "AWS";

    const string HomeRegion = "us-east-1";            // DescribeRegions is asked here: the docs' default Region
    const string Query = "Version=2016-11-15";
    const int PageSize = 100, MaxPages = 200, MaxRegions = 40;
    const double DefaultInterval = 600, MinInterval = 300, DefaultForgotten = 72, NoAccessRetry = 3600, RegionsEvery = 86400;
    const double KeyRetry = 1800, MaxBackoff = 1800, ClockSkew = 300, ReminderGap = 600;

    // A Region name; only this ever goes into a host name, so a config or API value can't point a signed request elsewhere.
    static readonly Regex RegionRx = new(@"^[a-z]{2}(-[a-z]+){1,3}-\d{1,2}$", RegexOptions.CultureInvariant);
    static readonly Regex KeyIdRx = new(@"^[A-Z0-9]{16,128}$", RegexOptions.CultureInvariant);
    static readonly Regex SecretRx = new(@"^[\x21-\x7E]{16,128}$", RegexOptions.CultureInvariant);
    static readonly Regex InstanceRx = new(@"^i-[0-9a-zA-Z]{1,40}$", RegexOptions.CultureInvariant);
    static readonly Regex CodeRx = new(@"^[A-Za-z0-9.]{1,80}$", RegexOptions.CultureInvariant);

    readonly Settings S;
    readonly Hub hub;
    readonly SafeHttp http;
    readonly object gate = new();
    CancellationTokenSource cts;

    enum Verdict { Ok, BadKey, Denied, Throttled, Clock, Server, Other }
    sealed record Reply(Verdict V, string Code, int Http, XElement Root);

    sealed class Instance
    {
        public string Id, Name, Region, Type, RawState = "", LaunchRaw = "";
        public double? Started;
        public bool Active => RawState is "pending" or "running" or "stopping" or "shutting-down";
        public string Shown => Name.Length > 0 ? Name : Id;
    }

    sealed class Track
    {
        public Instance I; public int Missed; public string LastSig;
        public string RemindedFor;   // the launch time the "forgotten?" toast was shown for (one per run, also across restarts)
    }

    /// <summary>The last word from one Region. state: "" (not read yet) | ok | no_access | throttled | clock | error.</summary>
    sealed class RegionState { public string State = ""; public string Code = ""; public int Count, Fails; public double NextAt, ReadAt; }

    readonly Dictionary<string, Track> machines = new();          // instance id -> what we know / show
    readonly Dictionary<string, RegionState> regions = new();     // the watched Regions
    string[] allowed = { HomeRegion };                             // Regions whose host may be called (validated names)
    string status = "off";
    string keyCheck;                                               // ok | revoked | denied | clock | network | unavailable; null: none
    int keyCheckCode;
    double nextRegions, lastReminder;
    int regionFails;

    public AwsConnector(Settings s, Hub h)
    {
        S = s; hub = h;
        http = new SafeHttp(() => Mock ? new[] { Api.Host.ToLowerInvariant() } : allowed.Select(r => $"ec2.{r}.amazonaws.com"));
    }

    // ------------------------------------------------------------ settings
    Uri Api => Uri.TryCreate(J.Str(Connectors.Config(S, Id)["api"]), UriKind.Absolute, out var u) && u.IsLoopback ? u : null;
    bool Mock => Api != null;

    JsonObject Cfg => Connectors.Config(S, Id);
    double Interval => Math.Clamp(J.Num(Cfg["list_interval"]) ?? DefaultInterval, Mock ? 0.2 : MinInterval, 3600);
    double ForgottenHours => J.Num(Cfg["forgotten_hours"]) is double d && d >= 0 ? d : DefaultForgotten;
    HashSet<string> Exempt => (Cfg["forgotten_exempt"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).ToHashSet();
    List<string> ChosenRegions => (Cfg["regions"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).Where(x => RegionRx.IsMatch(x)).ToList();

    public JsonObject SettingsChange(JsonObject page) =>
        SettingsRows.Change(page, "key", "forget_key", new[] { "regions", "forgotten_hours" });

    /// <summary>Changes the settings (settings page / API); bad values or a rejected key: nothing is changed.</summary>
    public JsonObject Configure(JsonObject b)
    {
        var c = Cfg;
        bool wasOn = J.Truthy(c["enabled"]);
        List<string> chosen = null;
        if (b.ContainsKey("forgotten_hours"))
        {
            if (J.Num(b["forgotten_hours"]) is not double d || d < 0 || d > 8760) throw new ApiError(400, "forgotten_hours must be 0-8760");
            c["forgotten_hours"] = d;
        }
        if (b.ContainsKey("list_interval"))
        {
            double lo = Mock ? 0.2 : MinInterval;
            if (J.Num(b["list_interval"]) is not double d || d < lo || d > 3600) throw new ApiError(400, $"list_interval must be {J.Fmt(lo)}-3600");
            c["list_interval"] = d;
        }
        if (b.ContainsKey("regions"))
        {
            if (b["regions"] is not JsonArray ra) throw new ApiError(400, "regions must be a list");
            chosen = new List<string>();
            foreach (var x in ra)
            {
                var r = J.Str(x).Trim().ToLowerInvariant();
                if (!RegionRx.IsMatch(r)) throw new ApiError(400, "not a Region name") { Ui = L.T("aws_err_region", J.Cut(J.Str(x), 40)) };
                if (!chosen.Contains(r)) chosen.Add(r);
            }
            if (chosen.Count > MaxRegions) throw new ApiError(400, $"at most {MaxRegions} Regions");
            c["regions"] = J.StrArray(chosen);
        }
        if (b.ContainsKey("enabled")) c["enabled"] = J.Truthy(b["enabled"]);
        bool keyGiven = J.IsStr(b["key"]);
        if (keyGiven) SetKey(J.Str(b["key"]));   // a rejected key: nothing is changed
        Connectors.SetConfig(S, Id, c);
        if (J.Truthy(b["forget_key"]))
        {
            Secrets.Delete(S, Id);
            keyCheck = null;
            Release(_ => true);
        }
        if (wasOn && !J.Truthy(c["enabled"])) Release(_ => true);
        // polls keep their schedule across other changes: a number typed in the settings causes no burst of requests
        if (keyGiven || chosen != null || J.Truthy(b["forget_key"]))
        {
            lock (gate) { regions.Clear(); nextRegions = 0; regionFails = 0; }
        }
        Start();
        return State();
    }

    /// <summary>"id:secret" -> credentials, or a 400 with the reason in words. Temporary keys (ASIA...) are refused: they
    /// need a session token and stop working within hours.</summary>
    static SigV4.Creds ParseKey(string key)
    {
        var p = (key ?? "").Trim().Split(':', 2);
        var id = p[0].Trim();
        var secret = p.Length > 1 ? p[1].Trim() : "";
        if (id.StartsWith("ASIA", StringComparison.Ordinal)) throw new ApiError(400, "temporary keys are not supported") { Ui = L.T("aws_err_temp") };
        if (!KeyIdRx.IsMatch(id) || !SecretRx.IsMatch(secret)) throw new ApiError(400, "this does not look like an AWS access key") { Ui = L.T("aws_err_format") };
        return new SigV4.Creds(id, secret);
    }

    /// <summary>Checks the key with AWS and keeps it in the Credential Manager. A key AWS rejects isn't kept (400). Saved with
    /// the reason shown when it can't be told yet: no network, AWS unavailable, a wrong clock, or a real key without
    /// ec2:DescribeRegions.</summary>
    void SetKey(string key)
    {
        var creds = ParseKey(key);
        Reply r = null;
        bool offline = false;
        try { r = Ec2(creds, HomeRegion, "Action=DescribeRegions&" + Query, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            offline = true;
            Log.W("aws: key not checked: " + e.GetBaseException().Message);
        }
        if (r?.V == Verdict.BadKey) throw new ApiError(400, "AWS rejected the key");
        string old = null;
        try { old = Secrets.Read(S, Id); } catch (Exception e) { Log.W("aws: key not read: " + e.Message); }
        try { Secrets.Write(S, Id, creds.AccessKey + ":" + creds.Secret); }
        catch (Exception e) { throw new ApiError(500, "the key was not saved: " + e.Message); }
        // another key may be another account: what was followed so far can't be told about any more
        if (old != null && old.Split(':')[0] != creds.AccessKey) Release(_ => true);
        SetCheck(offline ? "network" : VerdictCheck(r.V), r?.Http ?? 0);
    }

    static string VerdictCheck(Verdict v) => v switch
    {
        Verdict.Ok => "ok", Verdict.BadKey => "revoked", Verdict.Denied => "denied", Verdict.Clock => "clock", _ => "unavailable",
    };

    void SetCheck(string verdict, int code = 0) { keyCheck = verdict; keyCheckCode = code; }

    string CheckText() => keyCheck switch
    {
        "ok" => L.T("k_ok"),
        "revoked" => L.T("k_revoked", Title),
        "denied" => L.T("aws_denied"),
        "clock" => L.T("aws_clock"),
        "network" => L.T("k_network", Title),
        "unavailable" => L.T("k_unavailable", Title, keyCheckCode > 0 ? "HTTP " + keyCheckCode : "?"),
        _ => null,
    };

    public (string Check, string Text) KeyVerdict() => (keyCheck ?? "", CheckText() ?? "");

    bool KeySet()
    {
        try { return Secrets.Read(S, Id) is { Length: > 0 }; }
        catch (Exception e) { Log.W("aws: key not read: " + e.Message); return false; }
    }

    string RegionNote(RegionState r) => r.State switch
    {
        "ok" => L.N("aws_instances_n", r.Count),
        "no_access" => L.T("aws_region_denied"),
        "throttled" => L.T("aws_region_throttled"),
        "clock" => L.T("aws_clock"),
        "error" => L.T("aws_region_error", r.Code),
        _ => "",
    };

    public JsonObject SettingsRow()
    {
        var c = Cfg;
        bool on = J.Truthy(c["enabled"]);
        var st = status;
        var items = new JsonArray();
        lock (gate)
            foreach (var r in ChosenRegions)
                items.Add(new JsonObject { ["v"] = r, ["note"] = regions.TryGetValue(r, out var rs) ? RegionNote(rs) : "" });
        var forgotten = SettingsRows.Field("forgotten_hours", null, 0, 8760, 1, ForgottenHours);
        forgotten["text"] = L.T("aws_forgotten_field");
        return new JsonObject
        {
            ["id"] = Id, ["name"] = Title, ["kind"] = "key", ["enabled"] = on, ["uses_key"] = true,
            ["key_set"] = KeySet(), ["key_check"] = keyCheck,
            ["key_parts"] = new JsonArray(
                new JsonObject { ["ph"] = L.T("aws_key_id") },
                new JsonObject { ["ph"] = L.T("aws_key_secret") }),
            ["summary"] = !on ? "" : SettingsRows.Line(Summary(), SettingsRows.Normal(st) ? "" : st),
            ["help_url"] = SettingsRows.KeyHelpUrl(Id, "uk"),
            ["list"] = new JsonObject
            {
                ["key"] = "regions", ["label"] = L.T("aws_regions"), ["items"] = items, ["placeholder"] = HomeRegion,
                ["add"] = L.T("aws_region_add"), ["remove"] = L.T("aws_region_remove"), ["empty"] = L.T("aws_regions_all"), ["dup"] = L.T("aws_region_dup"),
            },
            ["fields"] = new JsonArray(forgotten),
            ["notes"] = new JsonArray(L.T("aws_note_readonly"), L.T("aws_note_unverified")),
        };
    }

    string Summary()
    {
        var parts = new List<string>();
        if (CheckText() is { } ct) parts.Add(ct);
        lock (gate)
        {
            int running = machines.Values.Count(t => t.I.RawState == "running");
            if (running > 0) parts.Add(L.N("m_running_n", running));
            int read = regions.Values.Count(r => r.State == "ok");
            if (read > 0) parts.Add(L.N("aws_regions_n", read));
            var bad = regions.Where(r => r.Value.State is "no_access" or "throttled" or "error").Select(r => r.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (bad.Count > 0) parts.Add("⚠ " + L.T("aws_regions_bad", string.Join(", ", bad.Take(3)) + (bad.Count > 3 ? " +" + (bad.Count - 3) : "")));
        }
        return string.Join(" · ", parts);
    }

    public JsonObject State()
    {
        var list = new JsonArray();
        var regs = new JsonObject();
        int running;
        lock (gate)
        {
            running = machines.Values.Count(t => t.I.RawState == "running");
            foreach (var t in machines.Values.OrderBy(t => t.I.Shown, StringComparer.OrdinalIgnoreCase))
                list.Add(new JsonObject { ["id"] = t.I.Id, ["name"] = t.I.Name, ["region"] = t.I.Region, ["state"] = t.I.RawState, ["task"] = Tid(t.I.Id) });
            foreach (var (k, r) in regions.OrderBy(x => x.Key, StringComparer.Ordinal))
                regs[k] = new JsonObject { ["state"] = r.State, ["code"] = r.Code, ["count"] = r.Count, ["read_at"] = r.ReadAt > 0 ? r.ReadAt : null };
        }
        return new JsonObject
        {
            ["enabled"] = J.Truthy(Cfg["enabled"]), ["key_set"] = KeySet(), ["status"] = status,
            ["key_check"] = keyCheck, ["key_check_text"] = CheckText(),
            ["regions"] = regs, ["regions_chosen"] = J.StrArray(ChosenRegions), ["machines"] = list, ["running"] = running,
            ["forgotten_hours"] = ForgottenHours, ["forgotten_exempt"] = J.StrArray(Exempt), ["list_interval"] = Interval,
            ["summary"] = Summary(),
        };
    }

    // ------------------------------------------------------------ lifecycle
    public void Start()
    {
        Stop();
        if (!J.Truthy(Cfg["enabled"])) { status = "off"; return; }
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        status = "starting";
        _ = Task.Run(() => Loop(ct));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
    }

    async Task Loop(CancellationToken ct)
    {
        Adopt();
        while (!ct.IsCancellationRequested)
        {
            try { await Tick(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                status = "offline: " + e.GetBaseException().Message;
                Log.W("aws: " + e.GetBaseException().Message);
            }
            try { await Task.Delay(Mock ? 100 : 1000, ct); } catch { return; }
        }
    }

    /// <summary>After a restart: the instances the hub still shows are followed again (and finished if they are gone).</summary>
    void Adopt()
    {
        foreach (var t in hub.Brief())
        {
            JsonObject full;
            try { full = hub.GetTask(J.Str(t["id"])); } catch { continue; }
            if (J.Str(full["source"]) != Id || full["meta"]?[Id] is not JsonObject g) continue;
            var id = J.Str(g["id"]);
            var region = J.Str(g["region"]);
            if (!InstanceRx.IsMatch(id) || !RegionRx.IsMatch(region)) continue;
            var i = new Instance
            {
                Id = id, Name = J.Str(g["name"]), Region = region, Type = J.Str(g["type"]), LaunchRaw = J.Str(g["launch"]),
                RawState = Hub.IsActive(J.Str(full["state"])) ? J.Str(g["state"], "running") : J.Str(g["state"], "stopped"),
            };
            i.Started = ParseTime(i.LaunchRaw);
            lock (gate) machines.TryAdd(id, new Track { I = i, RemindedFor = J.Str(g["reminded"]) });
        }
    }

    async Task Tick(CancellationToken ct)
    {
        var now = J.Now();
        string raw;
        try { raw = Secrets.Read(S, Id); }
        catch (Exception e) { status = e.Message; nextRegions = now + 120; return; }
        if (string.IsNullOrEmpty(raw)) { status = "no key"; return; }
        SigV4.Creds creds;
        try { creds = ParseKey(raw); }
        catch (ApiError) { status = "no key"; return; }
        if (now >= nextRegions) await RefreshRegions(creds, ct);
        if (keyCheck == "revoked") return;   // nothing is asked until the key works again (checked every 30 min)
        KeyValuePair<string, RegionState> due;
        lock (gate) due = regions.Where(r => r.Value.NextAt <= now).OrderBy(r => r.Value.NextAt).FirstOrDefault();
        if (due.Key != null) await Poll(due.Key, due.Value, creds, ct);
        RemindAll();
    }

    /// <summary>The key's verdict and the list of Regions to watch: the user's own, else the enabled ones.</summary>
    async Task RefreshRegions(SigV4.Creds creds, CancellationToken ct)
    {
        var chosen = ChosenRegions;
        Reply r;
        try { r = await Ec2(creds, HomeRegion, "Action=DescribeRegions&" + Query, ct); }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            status = "offline: " + e.GetBaseException().Message;
            if (keyCheck != "ok") SetCheck("network");
            nextRegions = J.Now() + Backoff(++regionFails);
            Watch(chosen);   // what is known or chosen is still worth asking: a Region may answer when this one doesn't
            return;
        }
        var now = J.Now();
        switch (r.V)
        {
            case Verdict.Ok:
                regionFails = 0;
                nextRegions = now + (Mock ? 5 : RegionsEvery);
                SetCheck("ok");
                if (status != "ok") status = "ok";
                if (chosen.Count > 0) { Watch(chosen); break; }
                var found = (Child(r.Root, "regionInfo")?.Elements() ?? Enumerable.Empty<XElement>())
                    .Select(x => Text(x, "regionName")).Where(x => RegionRx.IsMatch(x)).Distinct().Take(MaxRegions).ToList();
                if (found.Count == 0) { status = "AWS: no Regions in the answer"; found.Add(HomeRegion); }
                Watch(found);
                break;
            case Verdict.BadKey:
                SetCheck("revoked");
                status = "key rejected";
                nextRegions = now + (Mock ? 2 : KeyRetry);
                break;
            case Verdict.Denied:
                // a real key, but the policy lacks DescribeRegions: the chosen Regions don't need it
                regionFails = 0;
                nextRegions = now + (Mock ? 5 : RegionsEvery);
                SetCheck(chosen.Count > 0 ? "ok" : "denied");
                Watch(chosen.Count > 0 ? chosen : new List<string> { HomeRegion });
                break;
            case Verdict.Clock:
                SetCheck("clock");
                status = "clock";
                nextRegions = now + (Mock ? 2 : 300);
                break;
            default:
                SetCheck("unavailable", r.Http);
                status = "AWS: " + r.Code;
                nextRegions = now + Backoff(++regionFails);
                Watch(chosen);
                break;
        }
    }

    /// <summary>Sets the watched Regions; new ones start spread out (one request at a time anyway), and machines of Regions
    /// no longer watched are let go. Empty: keeps what is watched.</summary>
    void Watch(List<string> names)
    {
        if (names.Count == 0)
        {
            lock (gate) if (regions.Count > 0) return;
            names = new List<string> { HomeRegion };
        }
        var now = J.Now();
        lock (gate)
        {
            allowed = names.Append(HomeRegion).Distinct().ToArray();
            foreach (var old in regions.Keys.Where(k => !names.Contains(k)).ToList()) regions.Remove(old);
            int i = 0;
            foreach (var n in names)
            {
                if (!regions.ContainsKey(n)) regions[n] = new RegionState { NextAt = now + (Mock ? 0 : 2 * i) };
                i++;
            }
        }
        Release(t => !names.Contains(t.I.Region));
    }

    async Task Poll(string region, RegionState rs, SigV4.Creds creds, CancellationToken ct)
    {
        var all = new List<Instance>();
        string token = null;
        Reply r = null;
        try
        {
            for (int page = 0; ; page++)
            {
                if (page >= MaxPages) { r = new Reply(Verdict.Other, "too many pages", 200, null); break; }
                var q = $"Action=DescribeInstances&MaxResults={PageSize}&{Query}" + (token != null ? "&NextToken=" + Uri.EscapeDataString(token) : "");
                r = await Ec2(creds, region, q, ct);
                if (r.V != Verdict.Ok) break;
                if (r.Root.Name.LocalName != "DescribeInstancesResponse") { r = new Reply(Verdict.Other, "unexpected answer", 200, null); break; }
                all.AddRange(ParseInstances(r.Root, region));
                token = Text(r.Root, "nextToken") is { Length: > 0 } nt ? nt : null;
                if (token == null) break;   // an empty page with a token is not the end
            }
        }
        catch (Exception e) when (e is HttpRequestException or XmlException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            r = new Reply(e is XmlException ? Verdict.Other : Verdict.Server, e is XmlException ? "unreadable answer" : "network", 0, null);
            if (e is not XmlException) status = "offline: " + e.GetBaseException().Message;
        }
        var now = J.Now();
        lock (gate)
        {
            if (!regions.TryGetValue(region, out var cur) || cur != rs) return;   // no longer watched
            rs.Code = r.Code;
            switch (r.V)
            {
                case Verdict.Ok:
                    rs.State = "ok"; rs.Code = ""; rs.Fails = 0; rs.Count = all.Count; rs.ReadAt = now; rs.NextAt = now + Interval;
                    break;
                // AuthFailure from one Region is that Region (opted out, a policy condition), not a bad key: DescribeRegions tells that
                case Verdict.BadKey or Verdict.Denied:
                    rs.State = "no_access"; rs.Fails = 0; rs.NextAt = now + (Mock ? 2 : NoAccessRetry);
                    break;
                case Verdict.Clock:
                    rs.State = "clock"; rs.Fails = 0; rs.NextAt = now + (Mock ? 2 : 300);
                    break;
                case Verdict.Throttled:
                    rs.State = "throttled"; rs.NextAt = now + Backoff(++rs.Fails);
                    break;
                default:
                    rs.State = "error"; rs.NextAt = now + Backoff(++rs.Fails);
                    break;
            }
        }
        if (r.V == Verdict.Clock) SetCheck("clock");
        else if (r.V == Verdict.Ok && keyCheck is "clock" or "network" or "unavailable") SetCheck("ok");
        if (r.V != Verdict.Ok) { Log.W($"aws: {region}: {r.Code}"); return; }
        if (status.StartsWith("offline", StringComparison.Ordinal) || status == "starting") status = "ok";
        Reconcile(region, all);
    }

    /// <summary>Exponential with jitter, capped: no burst after a failure.</summary>
    double Backoff(int fails)
    {
        double wait = Math.Min(60 * Math.Pow(2, Math.Min(fails - 1, 10)), MaxBackoff) * (0.8 + Random.Shared.NextDouble() * 0.4);
        return Mock ? Math.Min(wait, 1.5) : wait;
    }

    // ------------------------------------------------------------ the wire
    Uri RegionUri(string region, string query)
    {
        if (!RegionRx.IsMatch(region)) throw new InvalidOperationException("not a Region name");
        return new Uri((Mock ? Api.GetLeftPart(UriPartial.Authority) : $"https://ec2.{region}.amazonaws.com") + "/?" + query);
    }

    async Task<Reply> Ec2(SigV4.Creds c, string region, string query, CancellationToken ct)
    {
        using var m = new HttpRequestMessage(HttpMethod.Get, RegionUri(region, query));
        SigV4.Apply(m, Array.Empty<byte>(), c, region, "ec2", DateTime.UtcNow);
        using var r = await http.Send(m, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        var root = ParseXml(text);
        if (r.IsSuccessStatusCode && root != null) return new Reply(Verdict.Ok, "", (int)r.StatusCode, root);
        if (r.IsSuccessStatusCode) return new Reply(Verdict.Other, "unreadable answer", (int)r.StatusCode, null);
        var code = ErrorCode(root, text);
        return new Reply(Classify(code, (int)r.StatusCode, r.Headers.Date), code.Length > 0 ? code : "HTTP " + (int)r.StatusCode, (int)r.StatusCode, null);
    }

    /// <summary>A real parser (element order varies, no newlines); no DTD, no outside resources. Null: not XML.</summary>
    static XElement ParseXml(string text)
    {
        try
        {
            using var sr = new StringReader(text);
            using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(xr).Root;
        }
        catch (XmlException) { return null; }
    }

    static XElement Child(XElement e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    static string Text(XElement e, string name) => Child(e, name)?.Value ?? "";

    /// <summary>The error's type only (never its message: a service's text stays out of logs and status lines). EC2:
    /// Response/Errors/Error/Code; a JSON service: __type or code, the part after # and before a colon.</summary>
    static string ErrorCode(XElement root, string text)
    {
        var code = Text(Child(Child(root, "Errors"), "Error"), "Code");
        if (code.Length == 0 && root != null) code = Text(Child(root, "Error"), "Code");
        if (code.Length == 0)
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject o)
                {
                    code = J.Str(o["__type"] ?? o["code"]);
                    if (code.Contains('#')) code = code[(code.IndexOf('#') + 1)..];
                    if (code.Contains(':')) code = code[..code.IndexOf(':')];
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        code = code.Trim();
        return CodeRx.IsMatch(code) ? code : "";
    }

    /// <summary>On the error's type, not the status. A wrong clock also looks like a bad signature or a bad key, so those
    /// are compared with the answer's Date first: a good key on a machine with a wrong clock isn't called revoked.</summary>
    static Verdict Classify(string code, int httpCode, DateTimeOffset? serverDate)
    {
        bool skew = serverDate is { } d && Math.Abs((DateTimeOffset.UtcNow - d).TotalSeconds) > ClockSkew;
        switch (code)
        {
            case "RequestTimeTooSkewed" or "RequestExpired" or "RequestInTheFuture":
                return Verdict.Clock;
            case "AuthFailure" or "SignatureDoesNotMatch" or "InvalidSignatureException" or "InvalidClientTokenId"
                or "UnrecognizedClientException" or "IncompleteSignature" or "ExpiredToken" or "ExpiredTokenException":
                return skew ? Verdict.Clock : Verdict.BadKey;
            case "UnauthorizedOperation" or "AccessDenied" or "AccessDeniedException" or "OptInRequired":
                return Verdict.Denied;
            case "RequestLimitExceeded" or "Throttling" or "ThrottlingException" or "TooManyRequestsException" or "LimitExceededException":
                return Verdict.Throttled;
        }
        return httpCode switch { 429 => Verdict.Throttled, >= 500 => Verdict.Server, _ => Verdict.Other };
    }

    static double? ParseTime(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d.ToUnixTimeMilliseconds() / 1000.0 : null;

    static List<Instance> ParseInstances(XElement root, string region)
    {
        var res = new List<Instance>();
        foreach (var rv in Child(root, "reservationSet")?.Elements() ?? Enumerable.Empty<XElement>())
            foreach (var it in Child(rv, "instancesSet")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                var id = Text(it, "instanceId");
                if (!InstanceRx.IsMatch(id)) continue;
                var name = (Child(it, "tagSet")?.Elements() ?? Enumerable.Empty<XElement>()).Where(t => Text(t, "key") == "Name").Select(t => Text(t, "value")).FirstOrDefault();
                var launch = Text(it, "launchTime");
                res.Add(new Instance
                {
                    Id = id, Name = J.Cut((name ?? "").Trim(), 200), Region = region, Type = J.Cut(Text(it, "instanceType"), 60),
                    RawState = J.Cut(Text(Child(it, "instanceState"), "name"), 30), LaunchRaw = J.Cut(launch, 40), Started = ParseTime(launch),
                });
            }
        return res;
    }

    // ------------------------------------------------------------ instances -> tasks
    static string Tid(string id) => "aws-" + J.Cut(Regex.Replace(id ?? "", @"[^A-Za-z0-9_.-]", "_"), 60);

    /// <summary>One Region read completely. An instance missing from two reads in a row is gone (the list is eventually
    /// consistent, and terminated ones drop out of it after a while).</summary>
    void Reconcile(string region, List<Instance> list)
    {
        var seen = new HashSet<string>();
        foreach (var i in list)
        {
            seen.Add(i.Id);
            Track t;
            string prev;
            lock (gate)
            {
                if (!machines.TryGetValue(i.Id, out t)) machines[i.Id] = t = new Track { I = i };
                prev = t.I == i ? null : t.I.RawState;
                t.I = i; t.Missed = 0;
            }
            Transition(t, prev);
        }
        List<Track> missing;
        lock (gate) missing = machines.Values.Where(t => t.I.Region == region && !seen.Contains(t.I.Id) && t.I.RawState != "terminated").ToList();
        foreach (var t in missing)
        {
            if (++t.Missed < 2) continue;
            var prev = t.I.RawState;
            t.I.RawState = "terminated";
            Transition(t, prev);
        }
    }

    JsonObject TryTask(string tid)
    {
        try { return hub.GetTask(tid); } catch { return null; }
    }

    void Try(Action a)
    {
        try { a(); } catch (ApiError e) { Log.W("aws: " + e.Message); }
    }

    /// <summary>The task follows the instance: active while it runs, starts or stops; stopped / terminated -> done. An
    /// instance first seen stopped is shown without a toast and already seen; one first seen terminated isn't shown.</summary>
    void Transition(Track t, string prev)
    {
        var i = t.I;
        var tid = Tid(i.Id);
        var task = TryTask(tid);
        bool taskActive = task != null && Hub.IsActive(J.Str(task["state"]));
        if (i.Active)
        {
            if (!taskActive) Register(t, quiet: false);
            Show(t, prev != i.RawState);
            return;
        }
        bool quiet = false;
        if (task == null)
        {
            if (i.RawState == "terminated" || prev != null) return;
            Register(t, quiet: true);
            taskActive = true;
            quiet = true;
        }
        if (taskActive)
        {
            Show(t, true);
            Try(() => hub.Finish(tid, new JsonObject { ["state"] = "done", ["message"] = i.RawState == "terminated" ? L.T("m_terminated") : L.T("m_stopped") }));
            if (quiet) hub.MarkSeen(new[] { tid });
            return;
        }
        if (i.RawState == "terminated" && prev != "terminated")
        {
            Show(t, true);
            Try(() => hub.Finish(tid, new JsonObject { ["state"] = "done", ["message"] = L.T("m_terminated") }));
        }
        else Show(t, false);
    }

    void Register(Track t, bool quiet)
    {
        var i = t.I;
        var b = new JsonObject
        {
            ["id"] = Tid(i.Id), ["name"] = J.Cut(i.Shown, 200), ["source"] = Id, ["project"] = Title, ["started"] = i.Started,
            ["notify"] = new JsonObject { ["finish"] = !quiet, ["fail"] = !quiet, ["stale"] = false },
            ["meta"] = Meta(t),
            ["controls"] = Controls(t),
            ["widgets"] = new JsonArray(new JsonObject { ["id"] = "info", ["type"] = "kv" }),
        };
        try { hub.Register(b, trusted: true); }
        catch (Exception e) { Log.W($"aws: register {Tid(i.Id)}: " + e.Message); return; }
        t.LastSig = null;
    }

    JsonObject Meta(Track t) => new()
    {
        [Id] = new JsonObject
        {
            ["id"] = t.I.Id, ["name"] = t.I.Name, ["region"] = t.I.Region, ["type"] = t.I.Type, ["state"] = t.I.RawState,
            ["launch"] = t.I.LaunchRaw, ["reminded"] = t.RemindedFor,
        },
    };

    static string ForgottenText(Instance i) => L.T("aws_forgotten", MachineConnector.Hours((J.Now() - i.Started.Value) / 3600));

    bool IsForgotten(Track t)
    {
        double h = ForgottenHours;
        return h > 0 && t.I.RawState == "running" && t.I.Started is double s && J.Now() - s >= h * 3600 && !Exempt.Contains(t.I.Id);
    }

    /// <summary>"Needed, don't remind" while it is called forgotten; "Remind again" once it was marked. Local settings only:
    /// no button here ever acts on the AWS account.</summary>
    JsonArray Controls(Track t)
    {
        var a = new JsonArray();
        var live = new JsonArray("running", "paused", "stale");
        if (Exempt.Contains(t.I.Id))
            a.Add(new JsonObject { ["id"] = "remind", ["label"] = L.T("aws_remind"), ["kind"] = "connector", ["states"] = live });
        else if (IsForgotten(t))
            a.Add(new JsonObject { ["id"] = "keep", ["label"] = L.T("aws_keep"), ["kind"] = "connector", ["states"] = live });
        return a;
    }

    void Show(Track t, bool force)
    {
        var i = t.I;
        var info = new JsonObject();
        void Kv(string k, string v) { if (!string.IsNullOrEmpty(v)) info[k] = v; }
        Kv(L.T("m_region"), i.Region);
        Kv(L.T("aws_type"), i.Type);
        Kv(L.T("aws_state"), i.RawState);
        if (Exempt.Contains(i.Id)) Kv(L.T("aws_reminder"), L.T("aws_reminder_off"));
        var links = new JsonArray();
        if (Regex.IsMatch(i.Region, "^[a-z0-9-]+$"))
            links.Add(new JsonObject
            {
                ["label"] = L.T("aws_console"),
                ["url"] = $"https://{i.Region}.console.aws.amazon.com/ec2/home?region={i.Region}#InstanceDetails:instanceId={i.Id}",
            });
        var u = new JsonObject
        {
            ["name"] = J.Cut(i.Shown, 200),
            ["description"] = SettingsRows.Line(i.Type, i.Region),
            ["status"] = i.RawState is "running" ? "" : J.Cut(i.RawState, 40),
            ["links"] = links,
            ["controls"] = Controls(t),
            ["attention"] = IsForgotten(t) ? ForgottenText(i) : "",
            ["meta"] = Meta(t),
        };
        // the attention text changes with the time, slowly: it is refreshed with the rest, its being there counts for "changed"
        var sig = J.Dump(u["name"]) + J.Dump(u["description"]) + J.Dump(u["status"]) + J.Dump(u["controls"]) + J.Dump(u["meta"]) + J.Dump(info) + IsForgotten(t);
        if (!force && sig == t.LastSig) return;
        t.LastSig = sig;
        u["widgets"] = new JsonObject { ["info"] = info };
        Try(() => hub.Update(Tid(i.Id), u, trusted: true));
    }

    /// <summary>The first time a run counts as forgotten: a line in its log, and a toast (at most one per 10 min: turning the
    /// connector on with many long-running servers isn't a burst, each still shows its "needs you" line).</summary>
    void RemindAll()
    {
        List<Track> due;
        lock (gate) due = machines.Values.Where(t => IsForgotten(t) && t.RemindedFor != t.I.LaunchRaw).ToList();
        foreach (var t in due)
        {
            var now = J.Now();
            var text = ForgottenText(t.I);
            t.RemindedFor = t.I.LaunchRaw;
            var u = new JsonObject { ["log"] = text, ["level"] = "warn", ["meta"] = Meta(t) };
            if (now - lastReminder >= (Mock ? 0 : ReminderGap)) { u["toast"] = text; lastReminder = now; }
            Try(() => hub.Update(Tid(t.I.Id), u, trusted: true));
            Show(t, true);
        }
    }

    /// <summary>What can't be told about any more (another key, connector off, a Region no longer watched) stops being
    /// followed. It isn't called stopped or terminated: nobody knows.</summary>
    void Release(Func<Track, bool> which)
    {
        List<Track> gone;
        lock (gate)
        {
            gone = machines.Values.Where(which).ToList();
            foreach (var t in gone) machines.Remove(t.I.Id);
        }
        foreach (var t in gone)
        {
            var tid = Tid(t.I.Id);
            if (TryTask(tid) is { } task && Hub.IsActive(J.Str(task["state"])))
                Try(() => hub.Finish(tid, new JsonObject { ["state"] = "done", ["message"] = L.T("aws_unwatched") }));
        }
    }

    // ------------------------------------------------------------ buttons
    public JsonObject OnControl(string tid, string cid, JsonNode value, string via)
    {
        var task = hub.GetTask(tid);
        if (J.Str(task["source"]) != Id || task["meta"]?[Id] is not JsonObject g) throw new ApiError(404, "not an AWS instance");
        if (cid is not ("keep" or "remind")) throw new ApiError(404, $"unknown action '{cid}'");
        var id = J.Str(g["id"]);
        if (!InstanceRx.IsMatch(id)) throw new ApiError(404, "not an AWS instance");
        var c = Cfg;
        var ex = Exempt;
        if (cid == "keep") ex.Add(id); else ex.Remove(id);
        c["forgotten_exempt"] = J.StrArray(ex);
        Connectors.SetConfig(S, Id, c);
        Track t;
        lock (gate) machines.TryGetValue(id, out t);
        if (t != null) Show(t, true);
        return new JsonObject { ["ok"] = true };
    }
}
