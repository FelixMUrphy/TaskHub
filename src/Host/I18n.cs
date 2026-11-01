using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// The translations: i18n/*.json (embedded as resources "i18n/&lt;name&gt;.json"), one source for the host and the panel.
/// Each file may hold a "host" table (L.T / L.N) and a "panel" table (ui/i18n.js: T); a key maps each of the 10 UI
/// languages to its text (the panel also has a few arrays). A new feature adds its own file instead of editing a shared
/// one. tests/i18n_test.py checks every key has all languages, matching placeholders and no duplicates.
/// </summary>
static class I18n
{
    static IEnumerable<(string File, JsonObject Root)> Files()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var n in asm.GetManifestResourceNames().Where(n => n.StartsWith("i18n/") && n.EndsWith(".json"))
                     .OrderBy(n => n, StringComparer.Ordinal))
            if (JsonNode.Parse(Res.Read(n, allowOverride: false)) is JsonObject o) yield return (n, o);
    }

    /// <summary>key -> text per language, in the order of L.Langs (a missing language falls back to English).</summary>
    public static Dictionary<string, string[]> Host(string[] langs)
    {
        var d = new Dictionary<string, string[]>();
        foreach (var (file, root) in Files())
            if (root["host"] is JsonObject h)
                foreach (var (k, v) in h)
                {
                    if (d.ContainsKey(k)) throw new InvalidOperationException($"i18n: key '{k}' twice ({file})");
                    var o = v as JsonObject;
                    string En() => o?["en"]?.GetValue<string>() ?? k;
                    d[k] = langs.Select(l => o?[l] is JsonValue s ? s.GetValue<string>() : En()).ToArray();
                }
        return d;
    }

    static string panelJson;

    /// <summary>The panel's table as a JS object literal: {lang: {key: text}}; safe inside a script element.</summary>
    public static string PanelJson(string[] langs)
    {
        if (panelJson != null) return panelJson;
        var all = new JsonObject();
        foreach (var l in langs) all[l] = new JsonObject();
        foreach (var (file, root) in Files())
            if (root["panel"] is JsonObject p)
                foreach (var (k, v) in p)
                    foreach (var l in langs)
                    {
                        var t = (JsonObject)all[l];
                        if (t.ContainsKey(k)) throw new InvalidOperationException($"i18n: panel key '{k}' twice ({file})");
                        var val = (v as JsonObject)?[l] ?? (v as JsonObject)?["en"];
                        if (val != null) t[k] = val.DeepClone();
                    }
        var json = all.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return panelJson = json.Replace("</", "<\\/").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
    }
}
