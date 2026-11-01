using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// A connector with a row in the Connectors section of the settings page. A new connector implements this and the page
/// draws its row from <see cref="SettingsRow"/> - on / off, the key (saved / replace / remove, "how to get a key"), its
/// number fields, its setup window - with no panel code of its own. The row is also GET /api/connectors?view=settings.
/// </summary>
interface ISettingsConnector : IConnector
{
    /// <summary>The service's name in messages ("Runpod", "GitHub").</summary>
    string Title { get; }

    /// <summary>{id, name, kind, enabled, uses_key, key_set, key_check, summary, help_url, fields?, ...}; never a key.
    /// fields: number inputs the page shows while the connector is on (<see cref="SettingsRows.Field"/>).</summary>
    JsonObject SettingsRow();

    /// <summary>A change from the page - enabled, key, forget_key and the keys of its fields - as the body of the
    /// connector's own Configure (which validates it).</summary>
    JsonObject SettingsChange(JsonObject page);

    JsonObject Configure(JsonObject body);

    /// <summary>The last check of the saved key: ("ok" / "revoked" / "network" / "unavailable" / "", the text in the UI
    /// language).</summary>
    (string Check, string Text) KeyVerdict();

    /// <summary>Its own setup window (the row's "setup" button; run on the UI thread), or null.</summary>
    Action Setup => null;
}

/// <summary>Helpers for the settings rows of connectors.</summary>
static class SettingsRows
{
    /// <summary>Parts of a state line joined with " · ", empty ones left out.</summary>
    public static string Line(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>States the key line already tells (or that need no words): not repeated in the summary.</summary>
    public static bool Normal(string st) =>
        st is "ok" or "starting" or "off" or "" or "no key" or "no token" or "key rejected" or "token rejected";

    /// <summary>The "how to get a key" page of a connector (docs/keys/&lt;id&gt;.md), in the UI language when that page
    /// exists (langs: the translated pages besides English), else English.</summary>
    public static string KeyHelpUrl(string id, params string[] langs)
    {
        var lang = L.Lang;
        var suffix = lang != "en" && langs.Contains(lang) ? "." + lang : "";
        return $"{L.RepoUrl}/blob/main/docs/keys/{id}{suffix}.md";
    }

    /// <summary>A number field of a row: key (sent back as {key: number}), label (a key of the panel's translations),
    /// limits, current value; emptyOk: an empty field is sent as null (= off) instead of being put back.</summary>
    public static JsonObject Field(string key, string label, double min, double max, double step, JsonNode value, bool emptyOk = false)
    {
        var f = new JsonObject
        {
            ["key"] = key, ["label"] = label, ["min"] = min, ["max"] = max, ["step"] = step, ["value"] = value?.DeepClone(),
        };
        if (emptyOk) f["empty_ok"] = true;
        return f;
    }

    /// <summary>The common part of <see cref="ISettingsConnector.SettingsChange"/>: enabled; a key (saving one turns the
    /// connector on) and forget_key under the connector's own names; plus the given keys as they are.</summary>
    public static JsonObject Change(JsonObject page, string keyName, string forgetName, IEnumerable<string> keys = null)
    {
        var b = new JsonObject();
        if (page.ContainsKey("enabled")) b["enabled"] = J.Truthy(page["enabled"]);
        if (J.IsStr(page["key"])) { b[keyName] = J.Str(page["key"]); b["enabled"] = true; }
        if (J.Truthy(page["forget_key"])) b[forgetName] = true;
        foreach (var k in keys ?? Enumerable.Empty<string>()) if (page.ContainsKey(k)) b[k] = page[k]?.DeepClone();
        return b;
    }
}
