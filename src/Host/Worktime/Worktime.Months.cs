using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>The work-hours module's months: closing one (cover the shortfall with weekend or holiday hours, carry the rest or not), reopening the
/// latest closed one, and the official holidays of the current month, which count only once the user has confirmed them.</summary>
sealed partial class Worktime
{
    static readonly Regex MonthRx = new(@"^(\d{4})-(\d{2})$"), CountryRx = new("^[A-Z]{2}$"), RegionRx = new("^[A-Z]{2}-[A-Z0-9]{1,3}$");
    const int MaxHolidayName = 100;
    const long HolidayCacheSec = 7 * 86400, HolidayRetrySec = 600;

    static (int Year, int Month) ParseMonth(string s)
    {
        var m = MonthRx.Match(s ?? "");
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var mo) || mo < 1 || mo > 12) throw new ApiError(400, "month must be like 2026-10");
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), mo);
    }

    static string MonthText(int y, int m) => $"{y:D4}-{m:D2}";

    // ------------------------------------------------------------ closing a month
    sealed record Offer(WorktimeCalc.Input Input, WorktimeCalc.MonthResult Before, bool Closed, bool CanClose, bool CanReopen, string Earlier);

    /// <summary>A month as the closing sees it: its numbers without its own closing (so a closed month shows what was decided from), whether it may be
    /// closed now (a past month, the earliest one not closed) or reopened (the latest closed one).</summary>
    static Offer OfferOf(WorktimeStore.Db db, int y, int m)
    {
        var c = CalcOf(db, Today(), Now());
        var closes = c.Input.Closes.ToDictionary(x => x.Key, x => x.Value);
        bool closed = closes.Remove((y, m));
        var without = c.Input with { Closes = closes };
        var month = WorktimeCalc.Compute(without).Months.FirstOrDefault(x => x.Year == y && x.Month == m);
        var all = WorktimeCalc.Compute(c.Input).Months;
        var earlier = all.Where(x => x.Paid && !x.Closed && (x.Year, x.Month).CompareTo((y, m)) < 0).Select(x => MonthText(x.Year, x.Month)).FirstOrDefault();
        var latest = c.Input.Closes.Keys.DefaultIfEmpty((0, 0)).Max();
        return new Offer(c.Input, month, closed, month != null && month.Paid && !closed && earlier == null, closed && latest == (y, m), earlier);
    }

    public JsonObject Month(string text)
    {
        var (y, m) = ParseMonth(text);
        lock (gate)
        {
            On();
            return Store.Tx(db =>
            {
                var o = OfferOf(db, y, m);
                var b = o.Before;
                long deficit = b?.DeficitAfter ?? 0, we = b?.Weekend ?? 0, ho = b?.Holiday ?? 0;
                return new JsonObject
                {
                    ["month"] = MonthText(y, m), ["closed"] = o.Closed, ["can_close"] = o.CanClose, ["can_reopen"] = o.CanReopen, ["close_first"] = o.Earlier,
                    ["deficit_seconds"] = deficit, ["weekday_seconds"] = b?.Weekday ?? 0, ["weekend_seconds"] = we, ["holiday_seconds"] = ho,
                    ["max_cover_seconds"] = Math.Min(deficit, we + ho),
                    ["deduct_unclosed"] = b != null && WorktimeCalc.RulesAt(o.Input.Rules, LastDay(y, m)).DeductUnclosed,
                    ["close"] = o.Closed ? db.CloseRow(MonthText(y, m)) : null,
                };
            });
        }
    }

    static DateOnly LastDay(int y, int m) => new(y, m, DateTime.DaysInMonth(y, m));

    /// <summary>Closes a past month: <c>cover_hours</c> of the shortfall come from the weekend and holiday hours (the lower multiplier first), and the rest
    /// is carried on (<c>carry</c>, the default) or not, which the "deducted or written off" setting then decides. The overtime left is paid.</summary>
    public JsonObject CloseMonth(string text, JsonObject b, bool preview = false)
    {
        var (y, m) = ParseMonth(text);
        Only(b, "cover_hours", "carry");
        if (b["carry"] != null && !J.IsBool(b["carry"])) throw new ApiError(400, "carry must be true or false");
        bool carry = b["carry"] == null || J.Truthy(b["carry"]);
        long cover = b["cover_hours"] == null ? 0 : Hours(b["cover_hours"], "cover_hours", MaxOpeningSec, zeroOk: true);
        return Apply(preview, db =>
        {
            var o = OfferOf(db, y, m);
            if (o.Closed) throw new ApiError(409, $"{MonthText(y, m)} is already closed");
            if (o.Earlier != null) throw new ApiError(409, $"close {o.Earlier} first");
            if (!o.CanClose) throw new ApiError(409, $"{MonthText(y, m)} hasn't ended yet");
            var before = o.Before;
            long max = Math.Min(before.DeficitAfter, before.Weekend + before.Holiday);
            if (cover > max) throw new ApiError(400, $"at most {J.Fmt(max / 3600.0)} hours can be covered with weekend and holiday hours");
            var rules = WorktimeCalc.RulesAt(o.Input.Rules, LastDay(y, m));
            var (byW, byH) = WorktimeCalc.SplitClosing(rules, cover, before.Weekend, before.Holiday);
            var close = new WorktimeCalc.MonthClose(byW, byH, carry);
            var closes = o.Input.Closes.ToDictionary(x => x.Key, x => x.Value);
            closes[(y, m)] = close;
            var input = o.Input with { Closes = closes };
            var after = WorktimeCalc.Compute(input).Months.First(x => x.Year == y && x.Month == m);
            var currency = J.Str(((JsonObject)J.Parse(db.Settings().Last(x => x.From <= LastDay(y, m) || x.From == db.Settings()[0].From).Json))["salary_currency"]);
            db.AddClose(MonthText(y, m), close, after, WorktimeCalc.RateAt(input, rules, LastDay(y, m)), currency);
            Log.I("worktime", $"month {MonthText(y, m)} closed");
        });
    }

    /// <summary>Opens the latest closed month again: its days can be changed and it's closed anew. An earlier one would change what the later
    /// closings were decided from.</summary>
    public JsonObject ReopenMonth(string text, bool preview = false)
    {
        var (y, m) = ParseMonth(text);
        return Apply(preview, db =>
        {
            var o = OfferOf(db, y, m);
            if (!o.Closed) throw new ApiError(409, $"{MonthText(y, m)} isn't closed");
            if (!o.CanReopen) throw new ApiError(409, "only the latest closed month can be reopened");
            db.DeleteClose(MonthText(y, m));
            Log.I("worktime", $"month {MonthText(y, m)} reopened");
        });
    }

    // ------------------------------------------------------------ holidays
    sealed record Holiday(DateOnly Date, string Name);

    /// <summary>The source's list, filtered to days off: "Public" among the types (nationalHoliday alone also marks bank holidays), and nationwide or
    /// in the region chosen. Anything that doesn't parse is left out.</summary>
    static List<Holiday> Official(string json, string region)
    {
        var list = new List<Holiday>();
        if (J.Parse(json) is not JsonArray all) return list;
        foreach (var n in all)
        {
            if (n is not JsonObject h || !J.IsStr(h["date"]) || !DateOnly.TryParseExact(J.Str(h["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (h["holidayTypes"] is not JsonArray types || !types.Any(t => J.IsStr(t) && J.Str(t) == "Public")) continue;
            bool here = J.Truthy(h["nationalHoliday"]) || (region != null && h["subdivisionCodes"] is JsonArray subs && subs.Any(s => J.IsStr(s) && J.Str(s) == region));
            if (!here) continue;
            var name = J.IsStr(h["name"]) ? J.Cut(J.Str(h["name"]), MaxHolidayName) : "";
            if (list.All(x => x.Date != d)) list.Add(new Holiday(d, name));
        }
        return list.OrderBy(x => x.Date).ToList();
    }

    static (string Country, string Region) PlaceOf(JsonObject settings)
    {
        var c = J.IsStr(settings["country"]) ? J.Str(settings["country"]) : null;
        return (c, c == null ? null : J.IsStr(settings["region"]) ? J.Str(settings["region"]) : null);
    }

    /// <summary>For the status: the current month, if its holidays wait for a confirmation. Without a fetched list it's asked too, the page fetches it.</summary>
    static string HolidaysToConfirm(WorktimeStore.Db db, JsonObject settings, DateOnly today)
    {
        var (country, region) = PlaceOf(settings);
        var month = MonthText(today.Year, today.Month);
        if (country == null || db.Reviewed(month)) return null;
        var cache = db.HolidayCache(country, today.Year);
        if (cache == null) return month;
        if (cache.Value.Json == UnknownCountry) return null;
        return Official(cache.Value.Json, region).Any(x => x.Date.Year == today.Year && x.Date.Month == today.Month) ? month : null;
    }

    readonly Dictionary<(string, int), long> holidayFailed = new();
    SafeHttp holidayHttp;

    /// <summary>Isolated test hubs only: a loopback stand-in for the holiday source.</summary>
    static Uri HolidayBase()
    {
        var o = Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 } ? Environment.GetEnvironmentVariable("TASKHUB_TEST_NAGER") : null;
        return Uri.TryCreate(o, UriKind.Absolute, out var u) && u.IsLoopback ? u : new Uri("https://nagerholidays.com");
    }

    const string UnknownCountry = "{\"unknown\":true}";

    /// <summary>A year of a country from the source; <see cref="UnknownCountry"/> when the source doesn't cover it (404); null if it can't be had now.
    /// Only nagerholidays.com, through SafeHttp.</summary>
    async Task<string> FetchHolidays(string country, int year)
    {
        var baseUri = HolidayBase();
        holidayHttp ??= new SafeHttp(() => new[] { baseUri.Host.ToLowerInvariant() }, 15);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"/api/v4/Holidays/{country}/{year}"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var res = await holidayHttp.Send(req, cts.Token);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) { Log.I("worktime", $"holidays: {country} isn't covered by the source"); return UnknownCountry; }
            if (!res.IsSuccessStatusCode) { Log.W("worktime", $"holidays {country} {year}: HTTP {(int)res.StatusCode}"); return null; }
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            if (text.Length > 1_000_000 || J.Parse(text) is not JsonArray) { Log.W("worktime", $"holidays {country} {year}: not a list"); return null; }
            return text;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Log.W("worktime", $"holidays {country} {year}: {e.GetType().Name}");
            return null;
        }
    }

    /// <summary>The month's official holidays, with what's marked already. The list is fetched when it's missing or a week old; on a failure an older
    /// list is used, or none, and the page offers to add days by hand.</summary>
    public async Task<JsonObject> Holidays(Dictionary<string, string> q)
    {
        var today = Today();
        var (y, m) = q.TryGetValue("month", out var mt) ? ParseMonth(mt) : (today.Year, today.Month);
        string country, region;
        (long Fetched, string Json)? cache;
        lock (gate)
        {
            On();
            (country, region, cache) = Store.Tx(db =>
            {
                var s = (JsonObject)J.Parse(db.Settings().Last().Json);
                var (c, r) = PlaceOf(s);
                return (c, r, c == null ? null : db.HolidayCache(c, y));
            });
        }
        string source = "none";
        bool fetched = false;
        if (country != null)
        {
            source = cache != null ? "ok" : "unavailable";
            var now = Now();
            bool stale = cache == null || now - cache.Value.Fetched > HolidayCacheSec;
            bool waited;
            lock (holidayFailed) waited = !holidayFailed.TryGetValue((country, y), out var failedAt) || now - failedAt > HolidayRetrySec;
            if (q.TryGetValue("refresh", out var rf) && rf == "1") waited = true;   // the user pressed Retry
            if (stale && waited)
            {
                var got = await FetchHolidays(country, y);
                if (got != null)
                {
                    lock (gate) Store.Tx(db => db.SetHolidayCache(country, y, now, got));
                    cache = (now, got);
                    source = "ok";
                    fetched = true;
                }
                else
                {
                    lock (holidayFailed) holidayFailed[(country, y)] = now;
                    source = cache != null ? "old" : "unavailable";
                }
            }
            else if (stale) source = cache != null ? "old" : "unavailable";
        }
        if (fetched) Changed();
        if (cache != null && cache.Value.Json == UnknownCountry) source = "unknown";
        lock (gate)
        {
            On();
            return Store.Tx(db =>
            {
                var first = new DateOnly(y, m, 1);
                var last = LastDay(y, m);
                var marks = db.Marks(first, last).ToDictionary(x => x.Date, x => x);
                var names = db.Names(first, last);
                var official = cache == null ? new List<Holiday>() : Official(cache.Value.Json, region).Where(x => x.Date >= first && x.Date <= last).ToList();
                var lang = L.Lang;
                JsonObject Row(DateOnly d, string apiName) => new()
                {
                    ["date"] = WorktimeStore.D(d), ["name"] = ShownName(country, d, apiName, names, lang), ["source_name"] = apiName,
                    ["marked"] = marks.TryGetValue(d, out var mk) && mk.Kind == "holiday", ["kind"] = marks.TryGetValue(d, out var k2) ? k2.Kind : null,
                };
                var own = marks.Values.Where(x => x.Kind == "holiday" && official.All(o => o.Date != x.Date)).OrderBy(x => x.Date)
                    .Select(x => (JsonNode)new JsonObject { ["date"] = WorktimeStore.D(x.Date), ["name"] = names.TryGetValue(x.Date, out var n) ? n.Name : "" }).ToArray();
                return new JsonObject
                {
                    ["month"] = MonthText(y, m), ["country"] = country, ["region"] = region, ["source"] = source,
                    ["reviewed"] = db.Reviewed(MonthText(y, m)), ["closed"] = db.MonthClosed(first),
                    ["holidays"] = new JsonArray(official.Select(x => (JsonNode)Row(x.Date, x.Name)).ToArray()),
                    ["own"] = new JsonArray(own),
                };
            });
        }
    }

    /// <summary>The user's own name wins, then a built-in translation into the UI language, then the source's English name.</summary>
    static string ShownName(string country, DateOnly d, string apiName, Dictionary<DateOnly, (string Name, bool Own)> names, string lang)
    {
        if (names.TryGetValue(d, out var n) && n.Own) return n.Name;
        return HolidayNames.Local(country, apiName, lang) ?? apiName;
    }

    /// <summary>The user's answer for a month's holidays: the official days kept (<c>dates</c>; the others aren't applied) and days of their own
    /// (<c>own</c>: date and name). After that the month isn't asked again; the days can still be changed one by one.</summary>
    public JsonObject ConfirmHolidays(string text, JsonObject b, bool preview = false)
    {
        var (y, m) = ParseMonth(text);
        Only(b, "dates", "own");
        var first = new DateOnly(y, m, 1);
        var last = LastDay(y, m);
        DateOnly InMonth(JsonNode n)
        {
            var d = ParseDate(J.IsStr(n) ? J.Str(n) : "", "a date");
            if (d < first || d > last) throw new ApiError(400, $"{WorktimeStore.D(d)} isn't in {MonthText(y, m)}");
            return d;
        }
        if (b["dates"] != null && b["dates"] is not JsonArray) throw new ApiError(400, "dates must be a list of dates");
        if (b["own"] != null && b["own"] is not JsonArray) throw new ApiError(400, "own must be a list of {date, name}");
        var keep = (b["dates"] as JsonArray ?? new JsonArray()).Select(InMonth).ToHashSet();
        var own = new Dictionary<DateOnly, string>();
        foreach (var n in b["own"] as JsonArray ?? new JsonArray())
        {
            if (n is not JsonObject o) throw new ApiError(400, "own must be a list of {date, name}");
            Only(o, "date", "name");
            var d = InMonth(o["date"]);
            var name = J.IsStr(o["name"]) ? J.Str(o["name"]).Trim() : "";
            if (name.Length == 0 || name.Length > MaxHolidayName || name.Any(char.IsControl)) throw new ApiError(400, $"a holiday's name: 1 to {MaxHolidayName} characters");
            own[d] = name;
        }
        return Apply(preview, db =>
        {
            var s = (JsonObject)J.Parse(db.Settings().Last().Json);
            var (country, region) = PlaceOf(s);
            var cache = country == null ? null : db.HolidayCache(country, y);
            var official = cache == null ? new List<Holiday>() : Official(cache.Value.Json, region).Where(x => x.Date >= first && x.Date <= last).ToList();
            foreach (var d in keep.Concat(own.Keys)) Writable(db, d);
            foreach (var h in official)
            {
                var day = h.Date;
                var had = db.Marks(day, day).FirstOrDefault();
                if (keep.Contains(day) && !own.ContainsKey(day))
                {
                    if (had.Kind != "holiday") Logged(db, day, "day_holiday", () => db.SetMark(day, "holiday", "official"));
                    db.SetName(day, h.Name, false);
                }
                else if (!keep.Contains(day) && !own.ContainsKey(day) && had.Kind == "holiday" && had.Source == "official")
                {
                    Logged(db, day, "day_normal", () => db.ClearMark(day));
                    db.ClearName(day);
                }
            }
            foreach (var (d, name) in own)
            {
                var day = d;
                if (db.Marks(day, day).FirstOrDefault().Kind != "holiday") Logged(db, day, "day_holiday", () => db.SetMark(day, "holiday", "manual"));
                db.SetName(day, name, true);
            }
            if (cache != null) db.SetReviewed(MonthText(y, m), Now());
        });
    }

    static void CheckPlace(JsonObject s)
    {
        if (s["country"] != null && !(J.IsStr(s["country"]) && CountryRx.IsMatch(J.Str(s["country"])))) throw new ApiError(400, "country must be a two-letter code, like UA");
        if (s["region"] == null) return;
        if (!(J.IsStr(s["region"]) && RegionRx.IsMatch(J.Str(s["region"])))) throw new ApiError(400, "region must be a subdivision code, like DE-BY");
        if (s["country"] == null || !J.Str(s["region"]).StartsWith(J.Str(s["country"]) + "-", StringComparison.Ordinal)) throw new ApiError(400, "region must be in the country");
    }
}

/// <summary>Built-in names of holidays in the language of their country, for the source's English names. Ukraine first; more countries can be added
/// here. The user's own name for a day always wins.</summary>
static class HolidayNames
{
    static readonly Dictionary<string, (string Lang, Dictionary<string, string> Names)> ByCountry = new()
    {
        ["UA"] = ("uk", new(StringComparer.OrdinalIgnoreCase)
        {
            ["New Year's Day"] = "Новий рік",
            ["International Women's Day"] = "Міжнародний жіночий день",
            ["Easter Sunday"] = "Великдень",
            ["International Workers' Day"] = "День праці",
            ["Victory day over Nazism in World War II"] = "День пам'яті та перемоги над нацизмом у Другій світовій війні",
            ["Pentecost"] = "Трійця",
            ["Constitution Day"] = "День Конституції України",
            ["Statehood Day"] = "День Української Державності",
            ["Independence Day"] = "День Незалежності України",
            ["Defender of Ukraine Day"] = "День захисників і захисниць України",
            ["Christmas Day"] = "Різдво Христове",
        }),
    };

    /// <summary>The name in <paramref name="lang"/> when it's the country's language and the name is known; else null.</summary>
    public static string Local(string country, string englishName, string lang) =>
        country != null && ByCountry.TryGetValue(country, out var c) && c.Lang == lang && c.Names.TryGetValue(englishName ?? "", out var n) ? n : null;
}
