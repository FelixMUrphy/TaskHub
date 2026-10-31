using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>Small helpers so the hub can treat JsonNode like Python dicts.</summary>
static class J
{
    public static readonly JsonSerializerOptions Out = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
    public static readonly JsonSerializerOptions Pretty = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static double Now() => Math.Round(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, 2);

    public static JsonNode C(JsonNode n) => n?.DeepClone();

    public static string Str(JsonNode n, string def = "")
    {
        if (n == null) return def;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<JsonElement>(out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
                if (e.ValueKind == JsonValueKind.Null) return def;
                if (e.ValueKind == JsonValueKind.True) return "True";
                if (e.ValueKind == JsonValueKind.False) return "False";
                return e.GetRawText();
            }
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            var d = Num(n);
            if (d != null) return Fmt(d.Value);
        }
        return n.ToJsonString(Out);
    }

    public static string Fmt(double d)
    {
        if (Math.Abs(d % 1) < 1e-12 && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("G", CultureInfo.InvariantCulture);
    }

    /// <summary>At most n UTF-16 units; never splits a surrogate pair (half an emoji is invalid text for JSON / UTF-8).</summary>
    public static string Cut(string s, int n)
    {
        if (s == null) return "";
        if (s.Length <= n) return s;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s.Substring(0, Math.Max(0, n));
    }

    /// <summary>Finite number (JSON number or numeric string), else null: NaN / Infinity would break serialization.</summary>
    public static double? Num(JsonNode n) => RawNum(n) is double d && double.IsFinite(d) ? d : null;

    static double? RawNum(JsonNode n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<JsonElement>(out var e))
        {
            if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
            if (e.ValueKind == JsonValueKind.String &&
                double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) return ps;
            return null;
        }
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    /// <summary>true only for real JSON numbers (not numeric strings), like isinstance(v,(int,float)).</summary>
    public static bool IsNum(JsonNode n)
    {
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<JsonElement>(out var e)) return e.ValueKind == JsonValueKind.Number;
        return v.TryGetValue<double>(out _) || v.TryGetValue<long>(out _) || v.TryGetValue<int>(out _)
               || v.TryGetValue<float>(out _) || v.TryGetValue<decimal>(out _);
    }

    public static bool IsStr(JsonNode n)
    {
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<JsonElement>(out var e)) return e.ValueKind == JsonValueKind.String;
        return v.TryGetValue<string>(out _);
    }

    public static bool IsBool(JsonNode n)
    {
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<JsonElement>(out var e)) return e.ValueKind is JsonValueKind.True or JsonValueKind.False;
        return v.TryGetValue<bool>(out _);
    }

    public static bool Truthy(JsonNode n)
    {
        switch (n)
        {
            case null: return false;
            case JsonArray a: return a.Count > 0;
            case JsonObject o: return o.Count > 0;
            case JsonValue v:
                if (v.TryGetValue<JsonElement>(out var e))
                {
                    return e.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null or JsonValueKind.Undefined => false,
                        JsonValueKind.String => e.GetString().Length > 0,
                        JsonValueKind.Number => e.GetDouble() != 0,
                        _ => true,
                    };
                }
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<string>(out var s)) return s.Length > 0;
                var d = Num(n);
                if (d != null) return d.Value != 0;
                return true;
        }
        return true;
    }

    public static JsonArray StrArray(IEnumerable<string> xs)
    {
        var a = new JsonArray();
        foreach (var x in xs) a.Add(JsonValue.Create(x));
        return a;
    }

    public static JsonNode Parse(string s) => JsonNode.Parse(s, documentOptions: new JsonDocumentOptions
    { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

    public static string Dump(JsonNode n) => n == null ? "null" : n.ToJsonString(Out);
}
