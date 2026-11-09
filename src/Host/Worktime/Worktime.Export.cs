using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>The hours of a period for export: day by day and summed up. To Excel (a file the user saves on this PC; amounts only there and only
/// when asked) or as a short summary to a linked messenger, which never carries amounts.</summary>
sealed partial class Worktime
{
    public const int MaxExportDays = 3 * 366;

    public sealed record ExportDay(DateOnly Date, string Kind, string Name, long Norm, long Worked, long Short, long Covered, long Overtime, string Pool);
    public sealed record ExportMonth(int Year, int Month, string Currency, decimal Salary, decimal Overtime, bool Closed);
    public sealed record Export(DateOnly From, DateOnly To, IReadOnlyList<ExportDay> Days, long Norm, long Worked, long Short, long Covered,
        long OtWeekday, long OtWeekend, long OtHoliday, long? ShortLeft, IReadOnlyList<ExportMonth> Months, JsonObject Display, string Currency);

    /// <summary>The period's days (only days counted: from the start, not after today) and their sums. Amounts: each month the period touches, as
    /// a whole month (its salary part and overtime so far), with the display currency at the current rate when one is set.</summary>
    public Export ExportOf(string fromText, string toText, bool amounts)
    {
        DateOnly from, to;
        try { from = ParseDate(fromText ?? "", "from"); to = ParseDate(toText ?? "", "to"); }
        catch (ApiError e) { throw new ApiError(400, e.Message) { Ui = L.T("wt_x_e_dates") }; }
        if (to < from) throw new ApiError(400, "to is before from") { Ui = L.T("wt_x_e_order") };
        if (to.DayNumber - from.DayNumber >= MaxExportDays) throw new ApiError(400, $"at most {MaxExportDays} days at a time") { Ui = L.T("wt_x_e_long") };
        lock (gate)
        {
            On();
            return Store.Tx(db =>
            {
                var today = Today();
                var c = CalcOf(db, today, Now());
                var r = WorktimeCalc.Compute(c.Input);
                var rows = r.Days.Where(x => x.Date >= from && x.Date <= to).ToList();
                var days = rows.Select(x =>
                {
                    var kind = x.Kind.ToString().ToLowerInvariant();
                    return new ExportDay(x.Date, kind, NameOf(db, c.Settings, x.Date, kind) ?? "", x.Norm, x.Worked,
                        x.ToDeficit, x.PaidOff, x.ToPool, x.ToPool > 0 ? x.Pool.ToString().ToLowerInvariant() : "");
                }).ToList();
                long Ot(WorktimeCalc.PoolKind k) => rows.Where(x => x.Pool == k).Sum(x => x.ToPool);
                var months = new List<ExportMonth>();
                JsonObject display = null;
                string current = null;   // the salary currency of the settings now: the display rate is from it
                if (amounts)
                {
                    var versions = db.Settings();
                    foreach (var m in r.Months.Where(m => new DateOnly(m.Year, m.Month, 1) <= to && LastDay(m.Year, m.Month) >= from))
                    {
                        var last = LastDay(m.Year, m.Month);
                        var v = versions.Last(x => x.From <= last || x.From == versions[0].From);
                        months.Add(new ExportMonth(m.Year, m.Month, J.Str(Filled(v.Json)["salary_currency"]), m.SalaryPart, m.OvertimeMoney, m.Closed));
                    }
                    var s = Filled(versions.Last().Json);
                    current = J.Str(s["salary_currency"]);
                    // the display currency at the rate the panel uses now; the file converts only the months in the current salary currency
                    if (DisplayOf(s) != null)
                    {
                        string err;
                        lock (rateGate) rateErr.TryGetValue(SourceOf(s), out err);
                        display = DisplayMoney(db, s, today, 0, 0, false, false, err);
                    }
                }
                return new Export(from, to, days, rows.Sum(x => x.Norm), rows.Sum(x => x.Worked), rows.Sum(x => x.ToDeficit), rows.Sum(x => x.PaidOff),
                    Ot(WorktimeCalc.PoolKind.Weekday), Ot(WorktimeCalc.PoolKind.Weekend), Ot(WorktimeCalc.PoolKind.Holiday),
                    rows.Count > 0 ? rows[^1].DeficitAfter : null, months, display, current);
            });
        }
    }

    /// <summary>Hours as the panel writes them: 168:30.</summary>
    public static string Hm(long sec) { sec = Math.Max(0, sec); return $"{sec / 3600}:{sec % 3600 / 60:D2}"; }

    /// <summary>The summary for a messenger: the period, hours against the norm, overtime by kind, the shortfall. Never an amount.</summary>
    public static (string Title, string Text) ExportText(Export x)
    {
        var ui = Ui();
        var title = L.T("wt_x_title", x.From.ToString("d", ui), x.To.ToString("d", ui));
        var lines = new List<string>
        {
            L.T("wt_x_worked", Hm(x.Worked), Hm(x.Norm)),
            L.T("wt_x_over", Hm(x.OtWeekday + x.OtWeekend + x.OtHoliday), Hm(x.OtWeekday), Hm(x.OtWeekend), Hm(x.OtHoliday)),
            L.T("wt_x_short", Hm(x.Short)),
        };
        if (x.ShortLeft is long left && left > 0) lines.Add(L.T("wt_x_left", x.Days[^1].Date.ToString("d", ui), Hm(left)));
        if (x.Days.Count == 0) lines = new List<string> { L.T("wt_x_empty") };
        return (title, string.Join("\n", lines));
    }

    // ------------------------------------------------------------ Excel
    /// <summary>An .xlsx of two sheets, Summary and Days, written by hand (SpreadsheetML in a zip, inline strings): hours as [h]:mm, dates as
    /// dates, amounts with two decimals. Written to a temporary file first, then moved over the target.</summary>
    public static void WriteXlsx(string path, Export x, bool amounts)
    {
        var ui = Ui();
        static string Kind(string k) => k switch
        {
            "weekend" => L.T("wt_x_k_weekend"), "holiday" => L.T("wt_x_k_holiday"), "vacation" => L.T("wt_x_k_vacation"), _ => L.T("wt_x_k_workday"),
        };
        static string Pool(string p) => p switch { "weekday" => L.T("wt_x_p_weekday"), "weekend" => L.T("wt_x_p_weekend"), "holiday" => L.T("wt_x_p_holiday"), _ => "" };
        var days = new List<Cell[]>
        {
            new[] { Txt(L.T("wt_x_c_date"), Bold), Txt(L.T("wt_x_c_weekday"), Bold), Txt(L.T("wt_x_c_type"), Bold), Txt(L.T("wt_x_c_norm"), Bold), Txt(L.T("wt_x_c_worked"), Bold),
                    Txt(L.T("wt_x_c_short"), Bold), Txt(L.T("wt_x_c_covered"), Bold), Txt(L.T("wt_x_c_over"), Bold), Txt(L.T("wt_x_c_overkind"), Bold) },
        };
        foreach (var d in x.Days)
            days.Add(new[] { Date(d.Date), Txt(ui.DateTimeFormat.GetDayName(d.Date.DayOfWeek)), Txt(d.Name.Length > 0 ? Kind(d.Kind) + ": " + d.Name : Kind(d.Kind)),
                             Hours(d.Norm), Hours(d.Worked), Hours(d.Short), Hours(d.Covered), Hours(d.Overtime), Txt(Pool(d.Pool)) });
        if (x.Days.Count > 0)
            days.Add(new[] { Txt(L.T("wt_x_total"), Bold), Txt(""), Txt(""), Hours(x.Norm, true), Hours(x.Worked, true), Hours(x.Short, true), Hours(x.Covered, true),
                             Hours(x.OtWeekday + x.OtWeekend + x.OtHoliday, true), Txt("") });

        var sum = new List<Cell[]>
        {
            new[] { Txt(L.T("wt_x_title", x.From.ToString("d", ui), x.To.ToString("d", ui)), Bold) },
            Array.Empty<Cell>(),
            new[] { Txt(L.T("wt_x_c_norm")), Hours(x.Norm) },
            new[] { Txt(L.T("wt_x_c_worked")), Hours(x.Worked) },
            new[] { Txt(L.T("wt_x_c_short")), Hours(x.Short) },
            new[] { Txt(L.T("wt_x_c_covered")), Hours(x.Covered) },
            new[] { Txt(L.T("wt_x_s_over", L.T("wt_x_p_weekday"))), Hours(x.OtWeekday) },
            new[] { Txt(L.T("wt_x_s_over", L.T("wt_x_p_weekend"))), Hours(x.OtWeekend) },
            new[] { Txt(L.T("wt_x_s_over", L.T("wt_x_p_holiday"))), Hours(x.OtHoliday) },
        };
        if (x.ShortLeft is long left && x.Days.Count > 0) sum.Add(new[] { Txt(L.T("wt_x_s_left", x.Days[^1].Date.ToString("d", ui))), Hours(left) });
        if (amounts && x.Months.Count > 0)
        {
            sum.Add(Array.Empty<Cell>());
            bool disp = x.Display != null && !J.Truthy(x.Display["missing"]) && J.Num(x.Display["rate"]) is double;
            var head = new List<Cell> { Txt(L.T("wt_x_m_month"), Bold), Txt(L.T("wt_x_m_salary"), Bold), Txt(L.T("wt_x_m_over"), Bold), Txt(L.T("wt_x_m_total"), Bold), Txt(L.T("wt_x_m_currency"), Bold) };
            if (disp) head.Add(Txt(L.T("wt_x_m_in", J.Str(x.Display["currency"])), Bold));
            sum.Add(head.ToArray());
            var cur = x.Currency;   // not the last month's: a month in an earlier currency has no rate here
            foreach (var m in x.Months)
            {
                var row = new List<Cell> { Txt(new DateTime(m.Year, m.Month, 1).ToString("Y", ui) + (m.Closed ? "" : " (" + L.T("wt_x_m_open") + ")")), Money(m.Salary), Money(m.Overtime),
                                           Money(m.Salary + m.Overtime), Txt(m.Currency) };
                if (disp) row.Add(m.Currency == cur ? Money(Math.Round((m.Salary + m.Overtime) * (decimal)J.Num(x.Display["rate"]).Value, 2)) : Txt(""));
                sum.Add(row.ToArray());
            }
            if (disp)
            {
                var src = J.Str(x.Display["source"]) switch { "nbp" => L.T("wt_x_src_nbp"), "nbu" => L.T("wt_x_src_nbu"), _ => L.T("wt_x_src_ecb") };
                var when = DateOnly.TryParseExact(J.Str(x.Display["rate_date"]) ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rd) ? rd.ToString("d", ui) : "";
                sum.Add(new[] { Txt(J.Truthy(x.Display["manual"]) ? L.T("wt_x_rate_own") : L.T("wt_x_rate", src, when)) });
            }
            sum.Add(new[] { Txt(L.T("wt_x_m_note")) });
        }

        // a name of our own beside the target (the move stays on one volume): never a file the user already has
        var tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            WriteParts(tmp, sum, days);
            File.Move(tmp, path, true);
        }
        // the file is open in Excel (a sharing violation): said in words, not as the system's message
        // (replacing a file held open without delete sharing fails as access denied, so a denied replace asks the file itself)
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && InUse(path)) { throw new ApiError(409, "the file is in use") { Ui = L.T("wt_x_e_busy") }; }
        finally { try { File.Delete(tmp); } catch (Exception) { } }   // a failed save leaves nothing next to the user's file
    }

    /// <summary>The file exists and another program holds it open (a sharing violation when opened alone).</summary>
    static bool InUse(string path)
    {
        if (!File.Exists(path)) return false;
        try { using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) { return true; }
        catch (Exception) { return false; }
    }

    static void WriteParts(string tmp, List<Cell[]> sum, List<Cell[]> days)
    {
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Part(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            string sumName = SheetName(L.T("wt_x_sheet_summary")), dayName = SheetName(L.T("wt_x_sheet_days"));
            if (dayName == sumName) dayName += " 2";
            Part("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Part("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Part("xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Esc(sumName)}" sheetId="1" r:id="rId1"/><sheet name="{Esc(dayName)}" sheetId="2" r:id="rId2"/></sheets></workbook>""");
            Part("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            // cellXfs: 0 plain, 1 date, 2 hours, 3 bold, 4 money, 5 bold hours
            Part("xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="[h]:mm"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/><family val="2"/></font><font><b/><sz val="11"/><name val="Calibri"/><family val="2"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="6"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="1" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");
            Part("xl/worksheets/sheet1.xml", Sheet(sum, new[] { 44.0, 14, 14, 14, 10, 16 }, false));
            Part("xl/worksheets/sheet2.xml", Sheet(days, new[] { 12.0, 14, 30, 10, 12, 16, 16, 14, 14 }, true));
        }
    }

    // ---- the cells
    const int Bold = 3;
    readonly record struct Cell(string Text, double? Number, int Style);
    static Cell Txt(string s, int style = 0) => new(s ?? "", null, style);
    static Cell Hours(long sec, bool bold = false) => new(null, Math.Max(0, sec) / 86400.0, bold ? 5 : 2);
    static Cell Date(DateOnly d) => new(null, d.DayNumber - new DateOnly(1899, 12, 30).DayNumber, 1);
    static Cell Money(decimal m) => new(null, (double)m, 4);

    static string Sheet(List<Cell[]> rows, double[] widths, bool frozenHead)
    {
        var b = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        if (frozenHead) b.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        b.Append("<cols>");
        for (int i = 0; i < widths.Length; i++) b.Append($"""<col min="{i + 1}" max="{i + 1}" width="{widths[i].ToString(CultureInfo.InvariantCulture)}" customWidth="1"/>""");
        b.Append("</cols><sheetData>");
        for (int r = 0; r < rows.Count; r++)
        {
            b.Append($"""<row r="{r + 1}">""");
            for (int c = 0; c < rows[r].Length; c++)
            {
                var cell = rows[r][c];
                var at = Col(c) + (r + 1);
                var st = cell.Style != 0 ? $" s=\"{cell.Style}\"" : "";
                if (cell.Number is double n) b.Append($"""<c r="{at}"{st}><v>{n.ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                else if (cell.Text.Length > 0) b.Append($"""<c r="{at}"{st} t="inlineStr"><is><t xml:space="preserve">{Esc(cell.Text)}</t></is></c>""");
            }
            b.Append("</row>");
        }
        return b.Append("</sheetData></worksheet>").ToString();
    }

    static string Col(int i) => i < 26 ? ((char)('A' + i)).ToString() : Col(i / 26 - 1) + (char)('A' + i % 26);

    /// <summary>Text for XML: escaped, and characters XML 1.0 can't carry dropped.</summary>
    static string Esc(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r')) continue;
            b.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
        }
        return b.ToString();
    }

    /// <summary>A sheet name Excel accepts: at most 31 characters, none of : \ / ? * [ ].</summary>
    static string SheetName(string s)
    {
        var n = new string(s.Where(ch => ":\\/?*[]".IndexOf(ch) < 0).ToArray()).Trim('\'').Trim();
        return n.Length == 0 ? "Sheet" : n.Length > 31 ? n[..31] : n;
    }

    /// <summary>The file name offered when saving: the period, nothing the user would rather not show.</summary>
    public static string ExportFileName(Export x) => SafeName(L.T("wt_r_need_t") + " " + x.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " – "
        + x.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + ".xlsx";

    static string SafeName(string s) => new(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
}
