using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// The panel's settings page talks to the host through this (WebView2 messages from our own page only, never the HTTP
/// API: these change the system - autostart, the AI agents' settings files - so other programs on the PC can't use them).
/// The page asks {"settings":"get"} / "set" / "do" and gets back {"settings_state": State(), "error"?, "note"?, "info"?}.
/// Everything that used to be in the tray menu lives here; the tray keeps only the quick actions.
/// </summary>
sealed class SettingsBridge
{
    readonly Settings S;
    readonly Hub hub;
    readonly TelegramBridge tg;
    readonly GitHubConnector gh;
    readonly MachineConnector[] machines;
    readonly Func<Updater> updater;
    readonly Action<Action> ui;

    public SettingsBridge(Settings s, Hub h, TelegramBridge t, GitHubConnector g, MachineConnector[] m, Func<Updater> u, Action<Action> uiThread)
    {
        S = s; hub = h; tg = t; gh = g; machines = m ?? Array.Empty<MachineConnector>(); updater = u; ui = uiThread;
    }

    public JsonObject State()
    {
        var agents = new JsonArray();
        foreach (var a in Integrations.All)
            agents.Add(new JsonObject
            {
                ["agent"] = a == Integrations.Agent.Claude ? "claude" : "codex", ["name"] = Integrations.Name(a),
                ["hooks"] = Integrations.Enabled(a, Integrations.Kind.Hooks), ["mcp"] = Integrations.Enabled(a, Integrations.Kind.Mcp),
                ["detected"] = Integrations.Detected(a),
            });
        var t = tg?.State();
        var conns = ConnectorRows();
        var up = updater();
        // the host's own translations of what used to be the tray's items (one source of the words in 10 languages)
        var labels = new JsonObject();
        foreach (var k in new[] { "mute", "test_toast", "connectors", "integrations", "menu_hooks", "menu_mcp", "autostart",
                     "updates", "update_check_now", "update_auto", "data_folder", "api_help", "support" })
            labels[k] = L.T(k).TrimStart('❤', ' ');
        if (up?.Available is { } av) labels["update_to"] = L.T("update_to", av.Version.ToString(3)).TrimStart('⬆', ' ').TrimEnd('…');
        return new JsonObject
        {
            ["labels"] = labels,
            ["version"] = Settings.Version, ["edition"] = Install.Edition,
            ["autostart"] = Install.AutostartEnabled(S),
            ["muted"] = hub.Muted,
            ["notifications"] = hub.Notifier?.SystemSetting,   // "disabled_*": turned off in Windows -> a warning row
            ["updates"] = up == null ? null : new JsonObject { ["auto"] = up.Auto, ["available"] = up.Available?.Version.ToString(3) },
            ["agents"] = agents,
            ["telegram"] = t == null ? null : new JsonObject
            {
                ["configured"] = J.C(t["configured"]), ["linked"] = J.C(t["linked"]), ["bot"] = J.C(t["bot"]), ["status"] = J.C(t["status"]),
                ["warning"] = J.C(t["warning"]),
            },
            // every connector the same way: what it is, on / off, a short state line; its own window for the details
            ["connectors"] = conns,
        };
    }

    /// <summary>Languages (besides English) of the "how to get a key" pages, docs/keys/&lt;id&gt;.&lt;lang&gt;.md, per connector.
    /// Adding a translated page = adding its language here.</summary>
    static readonly Dictionary<string, string[]> KeyDocLangs = new()
    {
        ["github"] = new[] { "uk" }, ["runpod"] = new[] { "uk" }, ["vastai"] = new[] { "uk" },
    };

    /// <summary>The "how to get a key" page of a connector, in the UI language when it exists, else English.</summary>
    public static string KeyHelpUrl(string id)
    {
        var lang = L.Lang;
        var suffix = lang != "en" && KeyDocLangs.TryGetValue(id, out var ls) && ls.Contains(lang) ? "." + lang : "";
        return $"{L.RepoUrl}/blob/main/docs/keys/{id}{suffix}.md";
    }

    /// <summary>
    /// The connectors as the settings page shows them, all the same way: name, on / off, a short state line; when on, the key
    /// (only whether one is saved and the verdict of its last check - never the key, not even a part of it), the "how to get
    /// a key" link while there is no working key, and the connector's other settings. Also GET /api/connectors?view=settings.
    /// </summary>
    public static JsonArray ConnectorRows(GitHubConnector gh, IEnumerable<MachineConnector> machines)
    {
        var conns = new JsonArray();
        static string Line(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        // states the key line already tells (or that need no words): not repeated
        static bool Normal(string st) => st is "ok" or "starting" or "off" or "" or "no key" or "no token" or "key rejected" or "token rejected";
        if (gh != null)
        {
            var g = gh.State();
            bool on = J.Truthy(g["enabled"]), tokenAuth = J.Str(g["auth"]) != "gcm";
            var st = J.Str(g["status"]);
            var check = J.Str(g["token_check"]);
            int n = (g["repos"] as JsonArray)?.Count ?? 0;
            var login = J.Str(g["login"]);
            conns.Add(new JsonObject
            {
                ["id"] = "github", ["name"] = "GitHub Actions", ["kind"] = "key", ["enabled"] = on,
                ["uses_key"] = tokenAuth, ["key_set"] = J.Truthy(g["token_set"]),
                ["key_check"] = check.Length > 0 ? check : null,
                ["summary"] = !on ? "" : Line(tokenAuth ? J.Str(g["token_check_text"]) : (login.Length > 0 ? "@" + login : ""),
                    (tokenAuth ? Normal(st) : st is "ok" or "starting" or "off" or "") ? "" : st, n > 0 ? L.N("gh_repos_n", n) : ""),
                ["help_url"] = KeyHelpUrl("github"),
                ["setup"] = "github",   // repositories and options: the GitHub window
            });
        }
        foreach (var mc in machines ?? Enumerable.Empty<MachineConnector>())
        {
            var ms = mc.State();
            bool on = J.Truthy(ms["enabled"]);
            var st = J.Str(ms["status"]);
            var check = J.Str(ms["key_check"]);
            conns.Add(new JsonObject
            {
                ["id"] = mc.Id, ["name"] = mc.Title, ["kind"] = "key", ["enabled"] = on, ["uses_key"] = true,
                ["key_set"] = J.Truthy(ms["key_set"]), ["key_check"] = check.Length > 0 ? check : null,
                ["summary"] = !on ? "" : Line(J.Str(ms["summary"]), Normal(st) ? "" : st),
                ["help_url"] = KeyHelpUrl(mc.Id),
                ["thresholds"] = new JsonObject
                {
                    ["idle_minutes"] = J.C(ms["idle_minutes"]), ["low_runway_hours"] = J.C(ms["low_runway_hours"]),
                    ["low_balance"] = J.C(ms["low_balance"]),
                },
                // the balance guard (2.9): its values, forecast / pending line, machines with "never auto-stop", last stops
                ["guard"] = on ? mc.GuardRow() : null,
            });
        }
        return conns;
    }

    JsonArray ConnectorRows() => ConnectorRows(gh, machines);

    static readonly string[] ThresholdKeys = { "idle_minutes", "low_runway_hours", "low_balance",
        "guard", "guard_warn", "guard_stop", "guard_grace_min", "guard_snooze_min", "guard_exempt" };

    /// <summary>Runs a command from the page; returns the reply to post back (the new state, maybe an error / a note).</summary>
    public async Task<JsonObject> Handle(JsonObject c)
    {
        string err = null, note = null, info = null;
        try
        {
            switch (J.Str(c["settings"]))
            {
                case "set":
                    if (c.ContainsKey("autostart"))
                    {
                        bool on = J.Truthy(c["autostart"]);
                        Install.SetAutostart(on, S);
                        S.Set(("autostart", on));      // respected by the self-healing check on start
                    }
                    if (c.ContainsKey("update_check")) S.Set(("update_check", J.Truthy(c["update_check"])));
                    if (c.ContainsKey("muted")) hub.SetMuted(J.Truthy(c["muted"]));
                    // a connector: on / off (off keeps the key), the key (checked with the service, then kept in the
                    // Credential Manager - never config.json), remove the key (only this deletes it), thresholds
                    if (J.IsStr(c["connector"]))
                    {
                        var id = J.Str(c["connector"]);
                        var mc = machines.FirstOrDefault(x => x.Id == id);
                        if (mc == null && (id != "github" || gh == null)) throw new Exception("no connector " + id);
                        var b = new JsonObject();
                        if (c.ContainsKey("enabled")) b["enabled"] = J.Truthy(c["enabled"]);
                        bool keyed = J.IsStr(c["key"]);
                        if (keyed) { b[mc != null ? "key" : "token"] = J.Str(c["key"]); b["enabled"] = true; }
                        if (J.Truthy(c["forget_key"])) b[mc != null ? "forget_key" : "forget_token"] = true;
                        if (mc != null) foreach (var k in ThresholdKeys) if (c.ContainsKey(k)) b[k] = c[k]?.DeepClone();
                        try
                        {
                            if (mc != null) await Task.Run(() => mc.Configure(b));
                            else await Task.Run(() => gh.Configure(b));
                        }
                        catch (ApiError e) when (keyed && e.Code == 400)
                        {
                            err = L.T("k_invalid", mc?.Title ?? "GitHub");   // nothing saved, the old key (if any) stays
                        }
                        catch (ApiError e) when (e.Code == 400 && e.Message.StartsWith("guard_"))
                        {
                            err = L.T("g_bad_values", e.Message);   // nothing changed
                        }
                        // right after saving: the verdict of the check ("Connected, balance $X" / "Connected as @login"...)
                        if (keyed && err == null)
                        {
                            var v = mc != null ? mc.State() : gh.State();
                            var text = J.Str(v[mc != null ? "key_check_text" : "token_check_text"]);
                            if (J.Str(v[mc != null ? "key_check" : "token_check"]) == "ok") info = text;
                            else if (text.Length > 0) note = text;   // saved, not checked: no network / service unavailable
                        }
                    }
                    else if (J.IsStr(c["agent"]))
                    {
                        var a = J.Str(c["agent"]) == "codex" ? Integrations.Agent.Codex : Integrations.Agent.Claude;
                        var k = J.Str(c["kind"]) == "mcp" ? Integrations.Kind.Mcp : Integrations.Kind.Hooks;
                        bool on = J.Truthy(c["on"]);
                        err = await Task.Run(() => Integrations.Set(a, k, on, S));
                        if (err == null && on) note = Integrations.Hint(a, k);
                    }
                    break;
                case "do":
                    switch (J.Str(c["what"]))
                    {
                        case "telegram": if (tg != null) ui(() => TelegramForm.ShowFor(tg)); break;
                        case "github": if (gh != null) ui(() => GitHubForm.ShowFor(gh)); break;
                        case "test_toast":
                            hub.Notify(new JsonObject { ["title"] = "TaskHub", ["message"] = L.T("test_toast") + " ✓", ["force"] = true });
                            break;
                        case "data_folder": Hub.OpenTarget(S.DataDir); break;
                        case "notif_settings": Hub.OpenTarget("ms-settings:notifications"); break;
                        case "api_help":
                        {
                            var p = Path.Combine(S.ExeDir, "API.md");
                            if (!File.Exists(p)) { p = S.File_("API.md"); File.WriteAllBytes(p, Res.Read("API.md")); }
                            Hub.OpenTarget(p);
                            break;
                        }
                        case "update_check_now": if (updater() is { } u1) await u1.Check(true); break;
                        case "update_open": updater()?.OpenPage(); break;
                        case "support": Hub.OpenTarget(L.SupportUrl); break;
                        case "guard":   // Stop now / Snooze of a pending balance-guard stop, from the settings page
                        {
                            var mc = machines.FirstOrDefault(x => x.Id == J.Str(c["connector"])) ?? throw new Exception("no connector");
                            var act = J.Str(c["act"]) == "stop_now" ? "stop_now" : "snooze";
                            var gr = await Task.Run(() => mc.GuardAct(act));
                            info = J.Str(gr["message"]);
                            break;
                        }
                    }
                    break;
            }
        }
        catch (Exception e) { err = e.Message; Log.W("settings: " + e.Message); }
        var r = new JsonObject { ["settings_state"] = State() };
        if (err != null) r["error"] = err;
        if (note != null) r["note"] = note;
        if (info != null) r["info"] = info;   // shown without a dialog
        return r;
    }
}
