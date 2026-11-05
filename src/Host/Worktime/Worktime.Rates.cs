using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TaskHub;

/// <summary>Exchange rates for showing the amounts in another currency. Every amount is worked out in the salary currency and only converted for
/// display. Rates come from the ECB (default) or the NBU, only while a display currency other than the salary's is set: refreshed on the first
/// call of the day and each morning after the set time, kept in worktime.db with their history. A failed refresh is retried after 15 minutes, up
/// to 4 times; then the last rate is used, its date shown, and a toast says so once.</summary>
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
    DateOnly rateWarnedDay;   // one notice a day at most
    SafeHttp rateHttp;
    System.Threading.Timer rateTimer;

    static string SourceOf(JsonObject s) => J.IsStr(s["rate_source"]) && J.Str(s["rate_source"]) == "nbu" ? "nbu" : "ecb";
    static string BaseOf(string source) => source == "nbu" ? "UAH" : "EUR";

    /// <summary>The display currency, if it differs from the salary's; else null and no rates are needed.</summary>
    static string DisplayOf(JsonObject s)
    {
        var d = J.IsStr(s["display_currency"]) ? J.Str(s["display_currency"]) : null;
        return d == null || d == J.Str(s["salary_currency"]) ? null : d;
    }

    static void CheckRates(JsonObject s)
    {
        if (s["display_currency"] != null && !(J.IsStr(s["display_currency"]) && CurrencyRx.IsMatch(J.Str(s["display_currency"])))) throw new ApiError(400, "display_currency must be an ISO code, like EUR");
        if (!(J.IsStr(s["rate_source"]) && J.Str(s["rate_source"]) is "ecb" or "nbu")) throw new ApiError(400, "rate_source must be ecb or nbu");
        if (!(J.IsStr(s["rate_time"]) && TimeOfDayRx.IsMatch(J.Str(s["rate_time"])))) throw new ApiError(400, "rate_time must be a time like 08:00");
        if (s["fx_manual_rate"] != null && Range(s, "fx_manual_rate", 0, 1e9) <= 0) throw new ApiError(400, "fx_manual_rate must be above 0");
    }

    /// <summary>A rate in the source's terms: units of the currency for one unit of its base (EUR for the ECB, UAH for the NBU), on its own date.</summary>
    sealed record Quote(DateOnly Date, decimal PerBase);

    static Quote QuoteOf(WorktimeStore.Db db, string source, string currency, DateOnly onOrBefore)
    {
        if (currency == BaseOf(source)) return new Quote(onOrBefore, 1);
        var r = db.Rate(source, currency, onOrBefore);
        return r == null ? null : new Quote(r.Value.Date, r.Value.Rate);
    }

    /// <summary>The money of the status in the display currency: <c>rate</c> is display units for one salary unit, <c>rate_date</c> the older date of
    /// the two quotes, <c>stale</c> when the last refresh failed or the rate is more than 4 days old, <c>manual</c> when the set rate stands in for a
    /// pair the source hasn't got. Null when no display currency is set; <c>missing</c> when there's neither.</summary>
    static JsonObject DisplayMoney(WorktimeStore.Db db, JsonObject s, DateOnly today, decimal earned, decimal overtime, bool failing, bool busy = false)
    {
        var display = DisplayOf(s);
        if (display == null) return null;
        var source = SourceOf(s);
        var salary = J.Str(s["salary_currency"]);
        var a = QuoteOf(db, source, salary, today);
        var b = QuoteOf(db, source, display, today);
        decimal? rate = null;
        DateOnly? date = null;
        bool manual = false;
        if (a != null && b != null && a.PerBase > 0)
        {
            rate = b.PerBase / a.PerBase;
            date = a.Date < b.Date ? a.Date : b.Date;
            if (salary == BaseOf(source)) date = b.Date;
            if (display == BaseOf(source)) date = a.Date;
        }
        else if (s["fx_manual_rate"] != null)
        {
            rate = (decimal)J.Num(s["fx_manual_rate"]).Value;
            manual = true;
        }
        var o = new JsonObject { ["currency"] = display, ["source"] = source };
        if (rate == null)
        {
            // why: still being fetched, the source answered without this pair, or it couldn't be reached
            bool fetched = db.LastRateDate(source) != null;
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
        var source = SourceOf(s);
        KickRates(s, long.TryParse(db.Meta("rates_ok_" + source), out var ok) ? ok : 0);
        bool failing, busy;
        lock (rateGate) { failing = rateFails > 0; busy = rateBusy; }
        return DisplayMoney(db, s, today, earned, overtime, failing, busy);
    }

    // ------------------------------------------------------------ the refresh
    /// <summary>Isolated test hubs only: loopback stand-ins for the two sources.</summary>
    static Uri RateUri(string source)
    {
        var o = Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 } ? Environment.GetEnvironmentVariable(source == "nbu" ? "TASKHUB_TEST_NBU" : "TASKHUB_TEST_ECB") : null;
        if (Uri.TryCreate(o, UriKind.Absolute, out var u) && u.IsLoopback) return u;
        return new Uri(source == "nbu" ? "https://bank.gov.ua/NBUStatService/v1/statdirectory/exchange?json" : "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml");
    }

    /// <summary>The source's rates, as units of each currency for one unit of the base, with their date. ECB: the newest Cube of the daily file (a
    /// morning call gives the previous working day: expected). NBU: UAH for one unit, inverted; metals and SDR (codes starting with X) left out.</summary>
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
        var key = SourceOf(s) + "|" + J.Str(s["salary_currency"]) + "|" + DisplayOf(s);
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
        var source = SourceOf(s);
        _ = Task.Run(async () =>
        {
            bool ok = false;
            try { ok = await RefreshRates(source); }
            catch (Exception e) { Log.W("worktime", "rates: " + e.GetType().Name); }
            string warn = null;
            lock (rateGate)
            {
                rateBusy = false;
                if (ok) rateFails = 0;
                else if (++rateFails == RateTries && rateWarnedDay != Today()) { warn = source; rateWarnedDay = Today(); }   // the tries stop here; one notice a day
            }
            if (warn != null)
            {
                JsonObject shown = null;
                try { lock (gate) shown = Store.Tx(db => DisplayMoney(db, s, Today(), 0, 0, true)); } catch (Exception) { }
                string text = shown == null || J.Truthy(shown["missing"]) ? L.T("wt_rates_none")
                    : J.Truthy(shown["manual"]) ? L.T("wt_rates_own")
                    : L.T("wt_rates_old", LocalDay(J.Str(shown["rate_date"])));
                try { Toast?.Invoke(Settings.AppId, text); } catch (Exception e) { Log.W("worktime", "rates toast: " + e.Message); }
            }
            if (ok) Changed();
        });
    }

    /// <summary>A date as the UI's language writes it.</summary>
    static string LocalDay(string iso)
    {
        if (!DateOnly.TryParseExact(iso ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return iso;
        try { return d.ToString("d", CultureInfo.GetCultureInfo(L.Lang)); } catch (CultureNotFoundException) { return iso; }
    }

    async Task<bool> RefreshRates(string source)
    {
        var uri = RateUri(source);
        rateHttp ??= new SafeHttp(() => new[] { RateUri("ecb").Host.ToLowerInvariant(), RateUri("nbu").Host.ToLowerInvariant() }, 20);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string text;
        try
        {
            using var res = await rateHttp.Send(req, cts.Token);
            if (!res.IsSuccessStatusCode) { Log.W("worktime", $"rates {source}: HTTP {(int)res.StatusCode}"); return false; }
            text = await res.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            Log.W("worktime", $"rates {source}: {e.GetType().Name}");
            return false;
        }
        (DateOnly Date, Dictionary<string, decimal> Rates)? got;
        try { got = text.Length > 2_000_000 ? null : ParseRates(source, text); }
        catch (Exception e) when (e is System.Xml.XmlException or System.Text.Json.JsonException or FormatException) { got = null; }
        if (got == null) { Log.W("worktime", $"rates {source}: not understood"); return false; }
        var now = Now();
        lock (gate)
        {
            if (!Enabled) return false;
            Store.Tx(db =>
            {
                foreach (var (cc, r) in got.Value.Rates) db.SetRate(got.Value.Date, source, cc, r);
                db.SetMeta("rates_ok_" + source, now.ToString(CultureInfo.InvariantCulture));
            });
        }
        Log.I("worktime", $"rates {source} of {WorktimeStore.D(got.Value.Date)}: {got.Value.Rates.Count}");
        return true;
    }

    /// <summary>A timer for the morning refresh while nothing asks for the status (the panel closed); it only checks, and only while the module is on.</summary>
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
                (s, lastOk) = Store.Tx(db => { var f = Filled(db.Settings().Last().Json); return (f, long.TryParse(db.Meta("rates_ok_" + SourceOf(f)), out var v) ? v : 0L); });
            KickRates(s, lastOk);
        }
        catch (Exception e) { Log.W("worktime", "rate timer: " + e.GetType().Name); }
    }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }
}
