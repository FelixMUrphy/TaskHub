using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>
/// AWS money, each behind its own switch (off by default): month-to-date cost and forecast from Cost Explorer, which AWS bills
/// per request, and Budgets. A paid read is rare and remembered in the config before it is made, or not made: a restart, a key
/// swapped or a switch turned off and on again never pays twice, and a failure only retries sooner if nothing reached AWS. An
/// error is never shown as $0. The account id Budgets needs comes from
/// sts:GetCallerIdentity and stays in memory, out of the state, the settings and the log.
/// </summary>
sealed partial class AwsConnector
{
    const double CostEvery = 8 * 3600, CostRetry = 3600, BudgetEvery = 6 * 3600, BudgetRetry = 1800;
    const int MaxBudgetPages = 5, ShownBudgets = 5;
    const string GlobalRegion = "us-east-1";
    static readonly Regex UnitRx = new(@"^[A-Za-z0-9/ -]{1,16}\z", RegexOptions.CultureInvariant);

    sealed record BudgetRead(string Name, decimal Actual, decimal Limit, decimal? Forecast, string Unit);

    string accountId;                                  // memory only
    double budgetsNext;
    string budgetsState = "", budgetsCode = "";
    double budgetsAt, costBlockedUntil;
    int moneyBusy;
    int budgetsTotal, moneyGen;                        // moneyGen: a read that began under the old key isn't kept
    List<BudgetRead> budgetsRead = new();
    bool moneyStale;                                   // another key: another account's figures

    double CostInterval => Mock && J.Num(Cfg["cost_interval"]) is double d ? d : CostEvery;
    double BudgetInterval => Mock && J.Num(Cfg["budget_interval"]) is double d ? d : BudgetEvery;
    JsonObject CostCache => Cfg["cost_cache"] as JsonObject ?? new JsonObject();

    void ConfigureMoney(JsonObject b, JsonObject c)
    {
        foreach (var k in new[] { "cost_interval", "budget_interval" })
        {
            if (!b.ContainsKey(k)) continue;
            if (!Mock) throw new ApiError(400, k + " can't be changed");
            if (J.Num(b[k]) is not double d || d < 0.2) throw new ApiError(400, k + " must be a number");
            c[k] = d;
            if (k == "cost_interval" && c["cost_cache"] is JsonObject o) o["next"] = 0;   // the simulator only
        }
        // on again after being refused: the permission may have been added, the warning mustn't outlive the fix. A refused
        // request isn't a read that was answered; any other state keeps its wait
        if (J.Truthy(b["cost"]) && !J.Truthy(c["cost"]) && c["cost_cache"] is JsonObject cc && J.Str(cc["state"]) == "no_access") cc["next"] = 0;
        if (J.Truthy(b["budgets"]) && !J.Truthy(c["budgets"])) lock (gate) if (budgetsState == "no_access") budgetsNext = 0;
        foreach (var k in new[] { "cost", "budgets" })
            if (b.ContainsKey(k)) c[k] = J.Truthy(b[k]);
    }

    /// <summary>Another account's figures aren't this one's: forgotten with the key that read them. The wait for the next paid
    /// read stays, so swapping keys can't pay again and again; unless the old key was refused, which read nothing.</summary>
    void ResetMoney(JsonObject c)
    {
        var old = c["cost_cache"] as JsonObject;
        var next = old != null && J.Str(old["state"]) != "no_access" ? J.Num(old["next"]) : null;
        c.Remove("cost_cache");
        if (next != null) c["cost_cache"] = new JsonObject { ["next"] = next };
        c.Remove("budget_alerted");
        moneyStale = false;
        lock (gate)
        {
            moneyGen++;
            accountId = null; budgetsNext = 0; budgetsState = ""; budgetsCode = ""; budgetsAt = 0; budgetsTotal = 0;
            budgetsRead = new List<BudgetRead>();
        }
    }

    /// <summary>At most one of them per tick, and one at a time. A change of the settings restarts the loop: a read already on its
    /// way isn't cancelled, it may be paid for and its wait is already saved. A read begun under another key isn't kept.</summary>
    async Task ReadMoney(SigV4.Creds creds)
    {
        if (Interlocked.Exchange(ref moneyBusy, 1) == 1) return;
        try
        {
            var now = J.Now();
            if (JobsOn("cost") && now >= (J.Num(CostCache["next"]) ?? 0) && now >= costBlockedUntil) await ReadCost(creds, now, CancellationToken.None);
            else if (JobsOn("budgets") && now >= budgetsNext) await ReadBudgets(creds, now, CancellationToken.None);
        }
        finally { Volatile.Write(ref moneyBusy, 0); }
    }

    bool SaveCost(Action<JsonObject> change)
    {
        lock (configLock)
        {
            var c = Cfg;
            var o = (JsonObject)CostCache.DeepClone();
            change(o);
            c["cost_cache"] = o;
            return Connectors.SetConfig(S, Id, c);
        }
    }

    async Task<JsonReply> Safe(Func<Task<JsonReply>> call, CancellationToken ct)
    {
        try { return await call(); }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new JsonReply(Verdict.Server, "network", 0, null);
        }
    }

    // ------------------------------------------------------------ Cost Explorer ($0.01 a request)
    async Task ReadCost(SigV4.Creds c, double now, CancellationToken ct)
    {
        int gen;
        lock (gate) gen = moneyGen;
        // before paying: a crash can't make it pay again. If this can't be remembered, it isn't paid at all
        if (!SaveCost(o => o["next"] = now + CostInterval))
        {
            costBlockedUntil = now + 600;
            Log.W("aws: cost: the next read can't be saved, not reading");
            return;
        }
        var today = DateTime.UtcNow.Date;
        var first = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var period = first.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        decimal? month = null;
        string unit = null, state = "ok", code = "";
        bool nothingPaid = true;                         // only then is a retry before the 8 hours free
        void Failed(JsonReply r)
        {
            code = r.Code;
            state = r.Code is "DataUnavailableException" or "BillExpirationException" ? "not_ready" : r.V is Verdict.BadKey or Verdict.Denied ? "no_access" : "error";
        }
        // the 1st: End is exclusive, so there is no period to ask for yet
        if (today > first)
        {
            var r = await Safe(() => Cost(c, "GetCostAndUsage", new JsonObject
            {
                ["TimePeriod"] = Period(first, today), ["Granularity"] = "MONTHLY", ["Metrics"] = new JsonArray("UnblendedCost"),
            }, ct), ct);
            if (r.V == Verdict.Ok)
            {
                foreach (var day in (r.Body["ResultsByTime"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (day["Total"]?["UnblendedCost"] is JsonObject t && Amount(t["Amount"]) is decimal a)
                    {
                        month = (month ?? 0) + a;
                        unit ??= Unit(t["Unit"]);
                    }
                if (month == null) { state = "error"; code = "unreadable answer"; }
            }
            else Failed(r);
            nothingPaid = r.Code == "network";
        }
        decimal? forecast = null;
        if (state == "ok")
        {
            var r = await Safe(() => Cost(c, "GetCostForecast", new JsonObject
            {
                ["TimePeriod"] = Period(today, first.AddMonths(1)), ["Granularity"] = "DAILY", ["Metric"] = "UNBLENDED_COST",
            }, ct), ct);
            if (r.V == Verdict.Ok && r.Body["Total"] is JsonObject t && Amount(t["Amount"]) is decimal a)
            {
                forecast = (month ?? 0) + a;   // the rest of the month, on top of what's already spent
                unit ??= Unit(t["Unit"]);
            }
            else if (r.V != Verdict.Ok && r.Code != "DataUnavailableException") Failed(r);   // too little history: only no forecast
            if (r.Code != "network") nothingPaid = false;
        }
        if (state != "ok") Log.W($"aws: cost: {code}");
        SaveCost(o =>
        {
            lock (gate) if (gen != moneyGen) return;   // the key changed while this was on its way
            o["state"] = state; o["code"] = code;
            if (nothingPaid) o["next"] = now + Math.Min(CostRetry, CostInterval);
            if (month != null || forecast != null || state == "ok")
            {
                o["period"] = period; o["month"] = month is decimal m ? (double)m : null; o["forecast"] = forecast is decimal f ? (double)f : null;
                o["unit"] = unit; o["at"] = now;
            }
        });
    }

    static JsonObject Period(DateTime start, DateTime end) => new()
    {
        ["Start"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["End"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    Task<JsonReply> Cost(SigV4.Creds c, string action, JsonObject body, CancellationToken ct) =>
        JsonCall(c, GlobalRegion, "ce", "/", "AWSInsightsIndexService." + action, Json11, body, ct);

    static decimal? Amount(JsonNode n) =>
        decimal.TryParse(J.Str(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    static string Unit(JsonNode n) => J.Str(n) is { } u && UnitRx.IsMatch(u) ? u : "";

    static string Money(double amount, string unit) => Money((decimal)amount, unit);

    static string Money(decimal amount, string unit) =>
        unit == "USD" ? "$" + amount.ToString("F2", CultureInfo.InvariantCulture) : (amount.ToString("F2", CultureInfo.InvariantCulture) + " " + unit).TrimEnd();

    /// <summary>A time that doesn't tick: a text that changes every second redraws the page and loses the focus.</summary>
    static string Clock(double t)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds((long)t).ToLocalTime();
        return d.ToString(d.Date == DateTime.Today ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------ Budgets
    async Task ReadBudgets(SigV4.Creds c, double now, CancellationToken ct)
    {
        budgetsNext = now + BudgetInterval;
        JsonReply bad = null;
        int gen, total = 0;
        lock (gate) gen = moneyGen;
        var list = new List<BudgetRead>();
        if (accountId == null)
        {
            var (id, err) = await Account(c, ct);
            if (id != null) lock (gate) accountId = id;
            bad = err;
        }
        string token = null;
        for (int page = 0; bad == null && page < MaxBudgetPages; page++)
        {
            var body = new JsonObject { ["AccountId"] = accountId, ["MaxResults"] = 100 };
            if (token != null) body["NextToken"] = token;
            var r = await Safe(() => JsonCall(c, GlobalRegion, "budgets", "/", "AWSBudgetServiceGateway.DescribeBudgets", Json11, body, ct), ct);
            if (r.V != Verdict.Ok) { bad = r; break; }
            foreach (var x in (r.Body["Budgets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(x => ++total > 0))
                // a utilisation or coverage budget's limit is a target percentage: being under it is the bad case
                if (J.Str(x["BudgetType"]) is "COST" or "USAGE" && Amount(x["BudgetLimit"]?["Amount"]) is decimal limit && Amount(x["CalculatedSpend"]?["ActualSpend"]?["Amount"]) is decimal actual)
                    list.Add(new BudgetRead(CleanText(J.Cut(J.Str(x["BudgetName"]), 100)), actual, limit, Amount(x["CalculatedSpend"]?["ForecastedSpend"]?["Amount"]), Unit(x["BudgetLimit"]?["Unit"])));
            token = J.Str(r.Body["NextToken"]) is { Length: > 0 } nt ? nt : null;
            if (token == null) break;
        }
        lock (gate)
        {
            if (gen != moneyGen) return;   // the key changed while this was on its way
            if (bad != null)
            {
                budgetsCode = bad.Code;
                budgetsState = bad.V is Verdict.BadKey or Verdict.Denied ? "no_access" : "error";
                if (budgetsState == "error") budgetsNext = now + Math.Min(BudgetRetry, BudgetInterval);
            }
            else
            {
                budgetsState = "ok"; budgetsCode = ""; budgetsAt = now; budgetsTotal = total;
                budgetsRead = list;
            }
        }
        if (bad != null) { Log.W($"aws: budgets: {bad.Code}"); return; }
        AlertBudgets(list);
    }

    /// <summary>The account's 12 digits, once per key. AWS: no permission is needed for this call, it can't be denied.</summary>
    async Task<(string Id, JsonReply Bad)> Account(SigV4.Creds c, CancellationToken ct)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes("Action=GetCallerIdentity&Version=2011-06-15");
            using var m = new HttpRequestMessage(HttpMethod.Post, ServiceUri(GlobalRegion, "sts", "/")) { Content = new ByteArrayContent(bytes) };
            m.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded; charset=utf-8");
            SigV4.Apply(m, bytes, c, GlobalRegion, "sts", DateTime.UtcNow);
            using var r = await http.Send(m, ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode)
            {
                var code = ErrorCode(ParseXml(text), text);
                return (null, new JsonReply(Classify(code, (int)r.StatusCode, r.Headers.Date), code.Length > 0 ? code : "HTTP " + (int)r.StatusCode, (int)r.StatusCode, null));
            }
            var id = ParseXml(text)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Account")?.Value;
            return id != null && Regex.IsMatch(id, @"^\d{12}\z") ? (id, null) : (null, new JsonReply(Verdict.Other, "unreadable answer", (int)r.StatusCode, null));
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return (null, new JsonReply(Verdict.Server, "network", 0, null));
        }
    }

    /// <summary>Over its limit: once per budget per month, one notification for all of them.</summary>
    void AlertBudgets(List<BudgetRead> list)
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        List<BudgetRead> over;
        lock (configLock)
        {
            var c = Cfg;
            var seen = c["budget_alerted"] as JsonObject ?? new JsonObject();
            over = list.Where(b => b.Limit > 0 && b.Actual > b.Limit && J.Str(seen[b.Name]) != month).ToList();
            if (over.Count == 0) return;
            var next = new JsonObject();
            foreach (var (name, m) in seen.Where(kv => J.Str(kv.Value) == month)) next[name] = m?.DeepClone();   // this month's stay
            foreach (var b in over) next[b.Name] = month;
            c["budget_alerted"] = next;
            Connectors.SetConfig(S, Id, c);
        }
        var names = string.Join(", ", over.Take(3).Select(b => b.Name)) + (over.Count > 3 ? " +" + (over.Count - 3) : "");
        hub.Notify(new JsonObject { ["title"] = "TaskHub · " + Title, ["level"] = "warn", ["message"] = L.N("aws_budget_over_n", over.Count) + ": " + names, ["click"] = "dismiss" });
    }

    // ------------------------------------------------------------ what the settings and the API say
    /// <summary>(lines, whether a permission is what's missing).</summary>
    (List<string> Lines, bool Denied) MoneyNotes()
    {
        var lines = new List<string>();
        bool denied = false;
        if (JobsOn("cost"))
        {
            var o = CostCache;
            var state = J.Str(o["state"]);
            var ago = J.Num(o["at"]) is double at ? L.T("aws_read_at", Clock(at)) : "";
            int before = lines.Count;
            if (J.Str(o["period"]) == DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            {
                var parts = new List<string>();
                if (J.Num(o["month"]) is double m) parts.Add(L.T("aws_cost_month", Money(m, J.Str(o["unit"]))));
                if (J.Num(o["forecast"]) is double f) parts.Add(L.T("aws_cost_forecast", Money(f, J.Str(o["unit"]))));
                if (parts.Count > 0) lines.Add(string.Join(" · ", parts) + " · " + ago);
            }
            if (lines.Count == before && state is "" or "ok" && J.Num(o["next"]) is double nx && nx > J.Now()) lines.Add(L.T("aws_cost_next", Clock(nx)));
            if (state == "not_ready") lines.Add(L.T("aws_cost_not_ready"));
            else if (state == "no_access") { lines.Add(L.T("aws_cost_denied")); denied = true; }
            else if (state == "error") lines.Add(L.T("aws_cost_error"));
        }
        if (JobsOn("budgets"))
            lock (gate)
            {
                if (budgetsState == "no_access") { lines.Add(L.T("aws_jobs_denied", "AWS Budgets")); denied = true; }
                else if (budgetsState == "error") lines.Add(L.T("aws_budgets_error"));
                else if (budgetsState == "ok" && budgetsTotal == 0) lines.Add(L.T("aws_budgets_none"));
                foreach (var b in budgetsRead.OrderByDescending(b => b.Limit > 0 ? b.Actual / b.Limit : 0).Take(ShownBudgets))
                    lines.Add((b.Limit > 0 && b.Actual > b.Limit ? "⚠ " : "") + L.T("aws_budget_line", b.Name, Money(b.Actual, b.Unit), Money(b.Limit, b.Unit)));
                if (budgetsRead.Count > ShownBudgets) lines.Add(L.T("aws_more_n", budgetsRead.Count - ShownBudgets));
            }
        return (lines, denied);
    }

    void AddMoneyState(JsonObject st)
    {
        st["cost"] = JobsOn("cost");
        st["budgets"] = JobsOn("budgets");
        var o = CostCache;
        st["cost_read"] = new JsonObject
        {
            ["state"] = J.Str(o["state"]), ["code"] = J.Str(o["code"]), ["period"] = J.Str(o["period"]), ["month"] = J.Num(o["month"]), ["forecast"] = J.Num(o["forecast"]),
            ["unit"] = J.Str(o["unit"]), ["read_at"] = J.Num(o["at"]), ["next_at"] = J.Num(o["next"]),
        };
        var items = new JsonArray();
        lock (gate)
        {
            foreach (var b in budgetsRead)
                items.Add(new JsonObject { ["name"] = b.Name, ["actual"] = (double)b.Actual, ["limit"] = (double)b.Limit, ["forecast"] = b.Forecast is decimal f ? (double)f : null, ["unit"] = b.Unit });
            st["budgets_read"] = new JsonObject { ["state"] = budgetsState, ["code"] = budgetsCode, ["read_at"] = budgetsAt > 0 ? budgetsAt : null, ["items"] = items };
        }
    }
}
