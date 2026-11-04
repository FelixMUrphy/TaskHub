using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskHub;

/// <summary>Input validation and normalisation: controls, inputs, links, widgets and their values.</summary>
sealed partial class Hub
{
    // ------------------------------------------------------------ validation
    static JsonObject NormInput(JsonNode inp)
    {
        if (!J.Truthy(inp)) return null;
        var o = inp as JsonObject ?? new JsonObject { ["type"] = "text" };
        var typ = J.Str(o["type"], "text");
        if (typ is not ("text" or "number" or "select" or "bool")) throw new ApiError(400, $"bad input type '{typ}'");
        var r = new JsonObject
        {
            ["type"] = typ,
            ["label"] = J.Cut(J.Str(o["label"]), 80),
            ["default"] = J.C(o["default"]),
            ["placeholder"] = J.C(o["placeholder"]),
        };
        if (typ == "select")
        {
            if (o["options"] is not JsonArray opts || opts.Count == 0) throw new ApiError(400, "select input needs options[]");
            r["options"] = J.StrArray(opts.Take(50).Select(x => J.Str(x)));
        }
        if (typ == "number")
            foreach (var k in new[] { "min", "max", "step" })
                if (J.Num(o[k]) is double d) r[k] = d;
        return r;
    }

    /// <param name="trusted">a built-in connector: may use kind "connector" (the press goes back to the connector)</param>
    static JsonObject NormControl(JsonNode cn, bool trusted)
    {
        if (cn is not JsonObject c) throw new ApiError(400, "control must be an object");
        var cid = J.Str(c["id"]).Trim();
        if (!CidRx.IsMatch(cid)) throw new ApiError(400, $"bad control id '{J.Cut(cid, 64)}'");
        var kind = J.Str(c["kind"], "signal");
        if (kind is not ("signal" or "shell" or "open" or "kill") && !(trusted && kind == "connector"))
            throw new ApiError(400, $"bad control kind '{J.Cut(kind, 64)}'");
        var statesN = c["states"];
        List<string> states;
        if (J.IsStr(statesN)) states = new() { J.Str(statesN) };
        else if (statesN is JsonArray sa && sa.Count > 0) states = sa.Select(x => J.Str(x)).ToList();
        else states = Active.ToList();
        states = states.Where(x => AllStates.Contains(x)).ToList();
        if (states.Count == 0) states = Active.ToList();
        var style = J.Str(c["style"]);
        var r = new JsonObject
        {
            ["id"] = cid,
            ["label"] = J.Cut(J.Truthy(c["label"]) ? J.Str(c["label"]) : cid, 60),
            ["kind"] = kind,
            ["style"] = style is "primary" or "danger" or "default" ? style : "default",
            ["confirm"] = J.Truthy(c["confirm"]) ? J.Cut(J.Str(c["confirm"]), 200) : null,
            ["hint"] = J.Truthy(c["hint"]) ? J.Cut(J.Str(c["hint"]), 200) : null,
            ["states"] = J.StrArray(states),
            ["input"] = NormInput(c["input"]),
            ["toast"] = J.Truthy(c["toast"]),
        };
        // irreversible connector actions (delete a rented machine): only through the panel's own WebView2 messages, never
        // the HTTP API, toasts or Telegram - Trigger refuses them unless via == "panel"
        if (trusted && J.Truthy(c["panel_only"])) { r["panel_only"] = true; r["toast"] = false; }
        if (kind == "shell")
        {
            var cmd = c["command"];
            bool ok = J.IsStr(cmd) || (cmd is JsonArray ca && ca.Count > 0 && ca.All(J.IsStr));
            if (!ok) throw new ApiError(400, "shell control needs command (string or list of strings)");
            if (cmd is JsonArray la) ShellListCheck(la.Select(x => J.Str(x)).ToList());
            r["command"] = J.C(cmd);
            r["cwd"] = J.C(c["cwd"]);
            r["timeout"] = J.Num(c["timeout"]) is double to && to > 0 ? to : 300;
        }
        if (kind == "open")
        {
            if (!J.Truthy(c["target"])) throw new ApiError(400, "open control needs target");
            r["target"] = J.Str(c["target"]);
        }
        return r;
    }

    /// <summary>A task link the panel may open: http / https / mailto, or a local / network path (file: URL or an
    /// absolute path), which the panel only shows in Explorer, never runs.</summary>
    public static bool LinkOk(string url, out Uri u)
    {
        u = null;
        return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)
            && u.Scheme is "http" or "https" or "mailto" or "file";
    }

    /// <summary>links: a list of URLs or {label, url}; any other scheme (javascript:, custom protocols...) -> 400.</summary>
    static JsonArray NormLinks(JsonNode ln)
    {
        if (ln == null) return new JsonArray();
        if (ln is not JsonArray la) throw new ApiError(400, "links must be a list");
        foreach (var l in la)
        {
            var url = l is JsonObject lo ? J.Str(lo["url"]) : J.IsStr(l) ? J.Str(l) : throw new ApiError(400, "link must be a string or {label, url}");
            if (url.Length > 0 && !LinkOk(url, out _))
                throw new ApiError(400, $"link '{J.Cut(url, 60)}': only http, https, mailto and file links (or an absolute path)");
        }
        return (JsonArray)la.DeepClone();
    }

    JsonObject NormWidget(JsonNode wn)
    {
        if (wn is not JsonObject w) throw new ApiError(400, "widget must be an object");
        var wid = J.Str(w["id"]).Trim();
        if (!CidRx.IsMatch(wid)) throw new ApiError(400, $"bad widget id '{J.Cut(wid, 64)}'");
        var typ = J.Str(w["type"], "text");
        if (!WidgetTypes.Contains(typ)) throw new ApiError(400, $"bad widget type '{J.Cut(typ, 64)}'; one of {string.Join(", ", WidgetTypes)}");
        JsonNode span = null;
        if (w.ContainsKey("span") || w.ContainsKey("wide"))
            span = (J.Num(w["span"]) == 2 || J.Truthy(w["wide"])) ? 2 : 1;
        var r = new JsonObject
        {
            ["id"] = wid, ["type"] = typ,
            ["label"] = J.Cut(J.Str(w["label"]), 80),
            ["unit"] = J.Cut(J.Str(w["unit"]), 16),
            ["span"] = span,
            ["value"] = J.C(w["value"]),
        };
        foreach (var k in new[] { "min", "max", "warn", "crit", "decimals", "invert", "mono", "color", "columns", "kind",
                     "series", "max_points", "y_min", "y_max", "total", "height", "text" })
            if (w.ContainsKey(k)) SetWidgetAttr(r, k, w[k]);
        if (typ == "chart")
        {
            var kind = J.Str(w["kind"]);
            r["kind"] = kind is "line" or "bar" or "area" ? kind : "line";
            var ser = w["series"];
            List<string> sl = ser is JsonArray sa && sa.Count > 0 ? sa.Select(x => J.Cut(J.Str(x), 32)).ToList()
                : J.Truthy(ser) ? new() { J.Cut(J.Str(ser), 32) } : new() { wid };
            r["series"] = J.StrArray(sl.Take(6));
            var mp = J.Num(w["max_points"]) is double m && m > 0 ? (int)m : 120;
            r["max_points"] = Math.Clamp(mp, 2, 2000);
            r["points"] = new JsonArray();
            r.Remove("value");
            if (J.Truthy(w["points"])) ChartAdd(r, w["points"]);
        }
        if (typ == "table")
        {
            r["columns"] = J.StrArray((w["columns"] as JsonArray ?? new JsonArray()).Take(12).Select(x => J.Cut(J.Str(x), 40)));
            r["value"] = J.C(w["value"]) ?? new JsonArray();
        }
        return r;
    }

    static readonly HashSet<string> WidgetNumAttrs = new()
        { "min", "max", "warn", "crit", "decimals", "max_points", "y_min", "y_max", "total", "height" };
    // a CSS colour the panel puts into style attributes: hex, a name, var(--x) or rgb()/hsl() with plain numbers
    static readonly Regex ColorRx = new(@"^(#[0-9A-Fa-f]{3,8}|[A-Za-z]{1,30}|var\(--[A-Za-z0-9-]{1,30}\)|(rgb|rgba|hsl|hsla)\([0-9.,%\s/]{1,60}\))$");

    /// <summary>Copies a widget attribute with its type enforced: numbers finite, colour a plain CSS colour, flags bool.
    /// The panel renders these into HTML attributes, so nothing else may get through.</summary>
    static void SetWidgetAttr(JsonObject w, string k, JsonNode v)
    {
        if (WidgetNumAttrs.Contains(k))
        {
            if (J.Num(v) is double d)
            {
                if (k == "decimals") d = Math.Clamp(Math.Round(d), 0, 10);
                if (k == "height") d = Math.Clamp(d, 20, 400);
                w[k] = d;
            }
            else w.Remove(k);
        }
        else if (k == "color")
        {
            var c = J.Str(v).Trim();
            if (ColorRx.IsMatch(c)) w[k] = c; else w.Remove(k);
        }
        else if (k is "invert" or "mono") w[k] = J.Truthy(v);
        else if (k is "label" or "kind") w[k] = J.Cut(J.Str(v), 80);
        else if (k == "unit") w[k] = J.Cut(J.Str(v), 16);
        else if (k == "text") w[k] = J.Cut(J.Str(v), 1000);
        else w[k] = J.C(v);
    }

    /// <summary>A finite number as it came, a numeric text as a number, anything else null.</summary>
    static JsonNode NumNode(JsonNode x) => J.IsNum(x) && J.Num(x) != null ? J.C(x) : J.Num(x) is double d ? JsonValue.Create(d) : null;

    static JsonNode NumOrNull(JsonNode x) => J.IsNum(x) ? JsonValue.Create(J.Num(x).Value) : null;

    /// <summary>v: number | {series: number} | [number,...] (one per series) | list of such (batch).</summary>
    static void ChartAdd(JsonObject w, JsonNode v)
    {
        var now = J.Now();
        var series = (w["series"] as JsonArray).Select(x => J.Str(x)).ToList();
        var pts = w["points"] as JsonArray;
        if (pts == null) w["points"] = pts = new JsonArray();
        int max = J.Num(w["max_points"]) is double m ? (int)m : 120;
        // of a batch only what the chart can keep; this runs under Hub.Lock
        List<JsonNode> batch = v is JsonArray va && va.Count > 0 && (va[0] is JsonArray || va[0] is JsonObject)
            ? va.Skip(Math.Max(0, va.Count - max)).ToList() : new List<JsonNode> { v };
        foreach (var item in batch)
        {
            var row = new JsonArray();
            if (item is JsonObject io)
            {
                row.Add(J.Num(io["t"]) is double ts ? ts : now);
                foreach (var s in series) row.Add(NumOrNull(io[s]));
            }
            else if (item is JsonArray ia)
            {
                row.Add(now);
                for (int i = 0; i < series.Count; i++) row.Add(i < ia.Count ? NumOrNull(ia[i]) : null);
            }
            else
            {
                row.Add(now);
                row.Add(NumOrNull(item));
                for (int i = 1; i < series.Count; i++) row.Add(null);
            }
            pts.Add(row);
        }
        if (pts.Count > max) pts.RemoveRange(0, pts.Count - max);
    }

    static void ApplyWidgetValues(JsonObject t, JsonObject vals)
    {
        var idx = new Dictionary<string, JsonObject>();
        foreach (var n in t["widgets"] as JsonArray ?? new JsonArray())
            if (n is JsonObject w) idx[J.Str(w["id"])] = w;
        foreach (var (wid, v) in vals)
        {
            if (!idx.TryGetValue(wid, out var w)) continue;
            var typ = J.Str(w["type"]);
            bool numeric = typ is "progress" or "gauge" or "stat";
            if (numeric && J.NonFinite(v)) continue;
            if (typ == "chart")
            {
                if (v == null) w["points"] = new JsonArray();
                else ChartAdd(w, v);
            }
            else if (v is JsonObject vo && typ is "progress" or "gauge" or "stat" or "led" or "text"
                     && (vo.ContainsKey("value") || vo.ContainsKey("label") || vo.ContainsKey("text") || vo.ContainsKey("total") || typ == "led" && vo.ContainsKey("state")))
            {
                if (typ == "led" && vo.ContainsKey("state")) w["value"] = J.C(vo["state"]);
                foreach (var k in new[] { "value", "label", "text", "total", "unit", "max", "color" })
                    if (vo.ContainsKey(k) && !(numeric && J.NonFinite(vo[k]))) SetWidgetAttr(w, k, vo[k]);
            }
            else if (typ == "stat" && J.IsNum(v) && J.IsNum(w["value"]))
            {
                w["prev"] = J.C(w["value"]);
                w["value"] = J.C(v);
            }
            else w["value"] = J.C(v);
        }
    }
}
