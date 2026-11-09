using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TaskHub;

/// <summary>Exchange rates for showing the amounts in another currency. Every amount is worked out in the salary currency and only converted for
/// display. Rates come from the chosen source (the ECB by default, the NBP or the NBU), only while a display currency other than the salary's is
/// set: refreshed on the first call of the day and each morning after the set time, kept in worktime.db with their history. When the chosen source
/// doesn't answer (a network or a VPN it refuses) or hasn't got the pair, the others are asked in turn, and the freshest rate any of them gave is
/// used, saying which. A refresh that got the pair nowhere is retried after 15 minutes, up to 4 times; then the last rate or the own one is used,
/// said on the panel, and a toast says so once.</summary>
sealed partial class Worktime
{
    const long RateRetrySec = 15 * 60;
    const int RateTries = 4;
    static readonly System.Text.RegularExpressions.Regex TimeOfDayRx = new(@"^([01]\d|2[0-3]):[0-5]\d$");

    /// <summary>Called when the rates couldn't be refreshed after all the tries: a title and a message, for a toast.</summary>
    public Action<string, string> Toast;

    readonly object rateGate = new();
    bool rateBusy;
    int rateFails;
    long rateLastTry;
    string rateKey;   // what the tries were for: a change of source or currencies starts them again
    readonly Dictionary<string, string> rateErr = new();   // per source, why its last fetch failed: blocked (it refused this network) or unavailable
    DateOnly rateWarnedDay;   // one notice a day at most
    SafeHttp rateHttp;
    System.Threading.Timer rateTimer;

    static readonly string[] RateSources = { "ecb", "nbp", "nbu" };
    static string SourceOf(JsonObject s) => J.IsStr(s["rate_source"]) && RateSources.Contains(J.Str(s["rate_source"])) ? J.Str(s["rate_source"]) : "ecb";
    static string BaseOf(string source) => source switch { "nbu" => "UAH", "nbp" => "PLN", _ => "EUR" };
    /// <summary>The chosen source first, then the others in a fixed order.</summary>
    static IEnumerable<string> SourcesFor(string chosen) => RateSources.Where(x => x == chosen).Concat(RateSources.Where(x => x != chosen));

    /// <summary>For the settings page: the currencies any source had a rate for when it was last fetched, with the bases; null if none was fetched.</summary>
    static JsonArray RateCurrenciesOf(WorktimeStore.Db db, JsonObject s)
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var src in RateSources)
        {
            var list = db.RateCurrencies(src);
            if (list.Count == 0) continue;
            all.UnionWith(list);
            all.Add(BaseOf(src));
        }
        return all.Count == 0 ? null : new JsonArray(all.Select(x => (JsonNode)x).ToArray());
    }

    /// <summary>The display currency, if it differs from the salary's; else null and no rates are needed.</summary>
    static string DisplayOf(JsonObject s)
    {
        var d = J.IsStr(s["display_currency"]) ? J.Str(s["display_currency"]) : null;
        return d == null || d == J.Str(s["salary_currency"]) ? null : d;
    }

    static void CheckRates(JsonObject s)
    {
        if (s["display_currency"] != null && !(J.IsStr(s["display_currency"]) && CurrencyRx.IsMatch(J.Str(s["display_currency"])))) throw new ApiError(400, "display_currency must be an ISO code, like EUR");
        if (!(J.IsStr(s["rate_source"]) && RateSources.Contains(J.Str(s["rate_source"])))) throw new ApiError(400, "rate_source must be ecb, nbp or nbu");
        if (!(J.IsStr(s["rate_time"]) && TimeOfDayRx.IsMatch(J.Str(s["rate_time"])))) throw new ApiError(400, "rate_time must be a time like 08:00");
        if (s["fx_manual_rate"] != null && Range(s, "fx_manual_rate", 0, 1e9) <= 0) throw new ApiError(400, "fx_manual_rate must be above 0");
    }

    /// <summary>A rate in the source's terms: units of the currency for one unit of its base (EUR for the ECB, PLN for the NBP, UAH for the NBU), on its own date.</summary>
    sealed record Quote(DateOnly Date, decimal PerBase);

    static Quote QuoteOf(WorktimeStore.Db db, string source, string currency, DateOnly onOrBefore)
    {
        if (currency == BaseOf(source)) return new Quote(onOrBefore, 1);
        var r = db.Rate(source, currency, onOrBefore);
        return r == null ? null : new Quote(r.Value.Date, r.Value.Rate);
    }

    /// <summary>The pair from one source: display units for one salary unit and the date it stands for (the older quote's; the base has none), or null.</summary>
    static (decimal Rate, DateOnly Date)? PairOf(WorktimeStore.Db db, string source, string salary, string display, DateOnly today)
    {
        var a = QuoteOf(db, source, salary, today);
        var b = QuoteOf(db, source, display, today);
        if (a == null || b == null || a.PerBase <= 0) return null;
        var date = salary == BaseOf(source) ? b.Date : display == BaseOf(source) ? a.Date : a.Date < b.Date ? a.Date : b.Date;
        return (b.PerBase / a.PerBase, date);
    }

    /// <summary>The money of the status in the display currency: <c>rate</c> is display units for one salary unit, <c>rate_date</c> the date it
    /// stands for, <c>source</c> the source it came from (the freshest rate of all the sources; the chosen one first on the same date), <c>chosen</c>
    /// the source set, <c>fallback</c> when another one stood in, <c>chosen_error</c> why the chosen one failed last (blocked: it refused this network;
    /// unavailable), <c>stale</c> when the last refresh got the pair nowhere or the rate is more than 4 days old, <c>manual</c> when the set rate stands
    /// in because no source has the pair. Null when no display currency is set; <c>missing</c> when there's no rate at all.</summary>
    static JsonObject DisplayMoney(WorktimeStore.Db db, JsonObject s, DateOnly today, decimal earned, decimal overtime, bool failing, bool busy = false, string chosenError = null)
    {
        var display = DisplayOf(s);
        if (display == null) return null;
        var chosen = SourceOf(s);
        var salary = J.Str(s["salary_currency"]);
        // the chosen source while it answers and its rate is recent; else the freshest rate of any (the chosen one first on the same date)
        string used = null;
        (decimal Rate, DateOnly Date)? best = null;
        if (chosenError == null && PairOf(db, chosen, salary, display, today) is { } own && own.Date >= today.AddDays(-4)) { best = own; used = chosen; }
        else
            foreach (var src in SourcesFor(chosen))
                if (PairOf(db, src, salary, display, today) is { } p && (best == null || p.Date > best.Value.Date)) { best = p; used = src; }
        decimal? rate = best?.Rate;
        DateOnly? date = best?.Date;
        bool manual = false;
        if (rate == null && s["fx_manual_rate"] != null)
        {
            rate = (decimal)J.Num(s["fx_manual_rate"]).Value;
            manual = true;
        }
        var o = new JsonObject { ["currency"] = display, ["source"] = used ?? chosen, ["chosen"] = chosen, ["fallback"] = used != null && used != chosen, ["chosen_error"] = chosenError };
        if (rate == null)
        {
            // why: still being fetched, a source answered without this pair, or none could be reached
            bool fetched = RateSources.Any(x => db.LastRateDate(x) != null);
            o["missing"] = true;
            o["why"] = busy && !fetched ? "pending" : fetched ? "unsupported" : "unavailable";
            return o;
        }
        o["earned"] = Math.Round(earned * rate.Value, 2);
        o["overtime"] = Math.Round(overtime * rate.Value, 2);
        o["rate"] = rate.Value;
        o["rate_date"] = date == null ? null : WorktimeStore.D(date.Value);
        o["manual"] = manual;
        o["stale"] = !manual && (failing || date < today.AddDays(-4));
        return o;
    }

    JsonObject Display(WorktimeStore.Db db, JsonObject s, DateOnly today, decimal earned, decimal overtime)
    {
        if (DisplayOf(s) == null) return null;
        KickRates(s, LastOk(db, s));
        bool failing, busy;
        string err;
        lock (rateGate) { failing = rateFails > 0; busy = rateBusy; rateErr.TryGetValue(SourceOf(s), out err); }
        return DisplayMoney(db, s, today, earned, overtime, failing, busy, err);
    }

    // ------------------------------------------------------------ the refresh
    /// <summary>When the rates of these settings were last good, Unix seconds (0: never): the chosen source's last fetch, unless its list is known to
    /// lack the pair; or the last refresh that got the pair from another source. So a new display currency a fresh list already has fetches nothing,
    /// a newly chosen source is fetched, and a source standing in for a blocked one isn't asked again until the next due time.</summary>
    static long LastOk(WorktimeStore.Db db, JsonObject s)
    {
        string chosen = SourceOf(s), salary = J.Str(s["salary_currency"]), display = DisplayOf(s);
        var list = db.RateCurrencies(chosen);
        bool Has(string c) => c == BaseOf(chosen) || list.Contains(c);
        long own = (list.Count == 0 || (Has(salary) && Has(display))) && long.TryParse(db.Meta("rates_ok_" + chosen), out var v) ? v : 0;
        long pair = long.TryParse(db.Meta("rates_pair_ok:" + RateKeyOf(s)), out var w) ? w : 0;
        return Math.Max(own, pair);
    }
    static string RateKeyOf(JsonObject s) => SourceOf(s) + "|" + J.Str(s["salary_currency"]) + "|" + DisplayOf(s);

    /// <summary>The sources' addresses. Isolated test hubs: a loopback stand-in, or a closed loopback port when the test has none, never the real one.</summary>
    static Uri RateUri(string source)
    {
        if (Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 })
        {
            var o = Environment.GetEnvironmentVariable("TASKHUB_TEST_" + source.ToUpperInvariant());
            return Uri.TryCreate(o, UriKind.Absolute, out var u) && u.IsLoopback ? u : new Uri("http://127.0.0.1:9/" + source);
        }
        return new Uri(source switch
        {
            "nbu" => "https://bank.gov.ua/NBUStatService/v1/statdirectory/exchange?json",
            "nbp" => "https://api.nbp.pl/api/exchangerates/tables/A/?format=json",
            _ => "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml",
        });
    }

    /// <summary>The source's rates, as units of each currency for one unit of the base, with their date. ECB: the newest Cube of the daily file (a
    /// morning call gives the previous working day: expected). NBU: UAH for one unit, inverted; metals and SDR (codes starting with X) left out.
    /// NBP: the newest table A, PLN for one unit (mid), inverted; XDR left out.</summary>
    internal static (DateOnly Date, Dictionary<string, decimal> Rates)? ParseRates(string source, string text)
    {
        var rates = new Dictionary<string, decimal>();
        if (source == "ecb")
        {
            using var xr = System.Xml.XmlReader.Create(new StringReader(text), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            var doc = XDocument.Load(xr);   // no DTD: the daily file has none, and entities could only blow it up
            var day = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Cube" && e.Attribute("time") != null);
            if (day == null || !DateOnly.TryParseExact((string)day.Attribute("time"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
            foreach (var c in day.Elements().Where(e => e.Name.LocalName == "Cube"))
                if ((string)c.Attribute("currency") is { } cc && CurrencyRx.IsMatch(cc)
                    && decimal.TryParse((string)c.Attribute("rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
                    rates[cc] = r;
            return rates.Count > 0 ? (d, rates) : null;
        }
        if (source == "nbp")
        {
            if (J.Parse(text) is not JsonArray tables || tables.Count == 0 || tables[0] is not JsonObject t || !J.IsStr(t["effectiveDate"]) || t["rates"] is not JsonArray list
                || !DateOnly.TryParseExact(J.Str(t["effectiveDate"]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var td)) return null;
            foreach (var n in list)
                if (n is JsonObject x && J.IsStr(x["code"]) && CurrencyRx.IsMatch(J.Str(x["code"])) && !J.Str(x["code"]).StartsWith('X') && J.Num(x["mid"]) is double pln && pln > 0)
                    rates[J.Str(x["code"])] = Math.Round(1m / (decimal)pln, 12);
            return rates.Count > 0 ? (td, rates) : null;
        }
        if (J.Parse(text) is not JsonArray all) return null;
        DateOnly? date = null;
        foreach (var n in all)
        {
            if (n is not JsonObject x || !J.IsStr(x["cc"]) || !J.IsStr(x["exchangedate"]) || J.Num(x["rate"]) is not double uah || uah <= 0) continue;
            var cc = J.Str(x["cc"]);
            if (!CurrencyRx.IsMatch(cc) || cc.StartsWith('X')) continue;
            if (!DateOnly.TryParseExact(J.Str(x["exchangedate"]), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            date ??= d;
            if (d != date) continue;   // one date per answer; read it from the answer, never assume today
            rates[cc] = Math.Round(1m / (decimal)uah, 12);
        }
        return date != null && rates.Count > 0 ? (date.Value, rates) : null;
    }

    /// <summary>Whether a refresh is due: none yet today, or none since this morning's time; a failure waits 15 minutes and gives up after 4 tries
    /// until the next due time.</summary>
    bool RateDue(JsonObject s, long now, long lastOk)
    {
        if (DisplayOf(s) == null) return false;
        var local = DateTimeOffset.FromUnixTimeSeconds(now).ToLocalTime();
        var t = TimeOnly.ParseExact(J.Str(s["rate_time"]), "HH:mm", CultureInfo.InvariantCulture);
        var morning = new DateTimeOffset(local.Date + t.ToTimeSpan(), local.Offset).ToUnixTimeSeconds();
        var dayStart = new DateTimeOffset(local.Date, local.Offset).ToUnixTimeSeconds();
        long dueSince = now >= morning ? morning : dayStart;
        var key = RateKeyOf(s);
        if (key != rateKey) { rateKey = key; rateFails = 0; rateLastTry = 0; }
        if (lastOk >= dueSince) { rateFails = 0; return false; }
        if (rateLastTry < dueSince) rateFails = 0;   // a new due time: tries again
        return rateFails < RateTries && now - rateLastTry >= (rateFails == 0 ? 0 : RateRetrySec);
    }

    /// <summary>Starts a refresh in the background when one is due; the status never waits for the network.</summary>
    void KickRates(JsonObject s, long lastOk)
    {
        var now = Now();
        lock (rateGate)
        {
            if (rateBusy || !RateDue(s, now, lastOk)) return;
            rateBusy = true;
            rateLastTry = now;
        }
        var chosen = SourceOf(s);
        var salary = J.Str(s["salary_currency"]);
        var display = DisplayOf(s);
        var key = RateKeyOf(s);
        _ = Task.Run(async () =>
        {
            bool ok = false;
            try { ok = await RefreshPair(chosen, salary, display, key); }
            catch (Exception e) { Log.W("worktime", "rates: " + e.GetType().Name); }
            bool warn = false;
            lock (rateGate)
            {
                rateBusy = false;
                if (ok) rateFails = 0;
                else if (++rateFails == RateTries && rateWarnedDay != Today()) { warn = true; rateWarnedDay = Today(); }   // the tries stop here; one notice a day
            }
            if (warn)
            {
                JsonObject shown = null;
                try { lock (gate) shown = Store.Tx(db => DisplayMoney(db, s, Today(), 0, 0, true)); } catch (Exception) { }
                string text = shown == null || J.Truthy(shown["missing"]) ? L.T("wt_rates_none")
                    : J.Truthy(shown["manual"]) ? L.T("wt_rates_own")
                    : L.T("wt_rates_old", LocalDay(J.Str(shown["rate_date"])));
                try { Toast?.Invoke(Settings.AppId, text); } catch (Exception e) { Log.W("worktime", "rates toast: " + e.Message); }
            }
            Changed();   // the panel says what came, or why not
        });
    }

    /// <summary>The chosen source, then the others until one has the pair. A source already known to lack a currency of the pair isn't asked, so
    /// a working chosen source is the only request. True when some source gave the pair.</summary>
    async Task<bool> RefreshPair(string chosen, string salary, string display, string key)
    {
        foreach (var src in SourcesFor(chosen))
        {
            if (src != chosen)
            {
                List<string> known;
                lock (gate) known = Store.Tx(db => db.RateCurrencies(src));
                bool Has(string c) => c == BaseOf(src) || known.Contains(c);
                if (known.Count > 0 && !(Has(salary) && Has(display))) continue;
            }
            var (got, err) = await RefreshRates(src);
            lock (rateGate) rateErr[src] = err;
            if (got == null) continue;
            bool covers = (salary == BaseOf(src) || got.Contains(salary)) && (display == BaseOf(src) || got.Contains(display));
            if (!covers) continue;
            lock (gate) if (Enabled) Store.Tx(db => db.SetMeta("rates_pair_ok:" + key, Now().ToString(CultureInfo.InvariantCulture)));
            if (src != chosen) Log.I("worktime", $"rates: {src} stood in for {chosen}");
            return true;
        }
        return false;
    }

    /// <summary>A date as the UI's language writes it.</summary>
    static string LocalDay(string iso)
    {
        if (!DateOnly.TryParseExact(iso ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return iso;
        try { return d.ToString("d", CultureInfo.GetCultureInfo(L.Lang)); } catch (CultureNotFoundException) { return iso; }
    }

    /// <summary>One source's list fetched and kept: the currencies it has, or null and why not (blocked: it refused this network, 401/403/451;
    /// unavailable: anything else).</summary>
    async Task<(HashSet<string> Got, string Err)> RefreshRates(string source)
    {
        var uri = RateUri(source);
        rateHttp ??= new SafeHttp(() => RateSources.Select(x => RateUri(x).Host.ToLowerInvariant()).Distinct().ToArray(), 20);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string text;
        try
        {
            using var res = await rateHttp.Send(req, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                Log.W("worktime", $"rates {source}: HTTP {(int)res.StatusCode}");
                return (null, (int)res.StatusCode is 401 or 403 or 451 ? "blocked" : "unavailable");
            }
            text = await res.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            Log.W("worktime", $"rates {source}: {e.GetType().Name}");
            return (null, "unavailable");
        }
        (DateOnly Date, Dictionary<string, decimal> Rates)? got;
        try { got = text.Length > 2_000_000 ? null : ParseRates(source, text); }
        catch (Exception e) when (e is System.Xml.XmlException or System.Text.Json.JsonException or FormatException) { got = null; }
        if (got == null) { Log.W("worktime", $"rates {source}: not understood"); return (null, "unavailable"); }
        var now = Now();
        lock (gate)
        {
            if (!Enabled) return (null, null);
            Store.Tx(db =>
            {
                foreach (var (cc, r) in got.Value.Rates) db.SetRate(got.Value.Date, source, cc, r);
                db.SetMeta("rates_ok_" + source, now.ToString(CultureInfo.InvariantCulture));
            });
        }
        Log.I("worktime", $"rates {source} of {WorktimeStore.D(got.Value.Date)}: {got.Value.Rates.Count}");
        return (got.Value.Rates.Keys.ToHashSet(), null);
    }

    /// <summary>A refresh due while nobody asks for the status. Not on a test hub: there the status starts every refresh, at the test's pace.</summary>
    void StartRateTimer()
    {
        if (Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 }) return;
        rateTimer ??= new System.Threading.Timer(_ =>
    {
        try
        {
            if (!Enabled) return;
            JsonObject s;
            long lastOk;
            lock (gate)
                (s, lastOk) = Store.Tx(db => { var f = Filled(db.Settings().Last().Json); return (f, LastOk(db, f)); });
            KickRates(s, lastOk);
        }
        catch (Exception e) { Log.W("worktime", "rate timer: " + e.GetType().Name); }
    }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }
}
