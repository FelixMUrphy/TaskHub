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
    readonly ISettingsConnector[] conns;   // in the order of the page
    readonly Func<Updater> updater;
    readonly Action<Action> ui;

    public SettingsBridge(Settings s, Hub h, TelegramBridge t, IEnumerable<ISettingsConnector> c, Func<Updater> u, Action<Action> uiThread)
    {
        S = s; hub = h; tg = t; conns = c?.ToArray() ?? Array.Empty<ISettingsConnector>(); updater = u; ui = uiThread;
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
        var rows = ConnectorRows(conns);
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
            ["connectors"] = rows,
        };
    }

    /// <summary>
    /// The connectors as the settings page shows them, all the same way, each row built by its connector
    /// (<see cref="ISettingsConnector.SettingsRow"/>): name, on / off, a short state line; when on, the key (only whether
    /// one is saved and the verdict of its last check - never the key, not even a part of it), the "how to get a key"
    /// link while there is no working key, and the connector's other settings. Also GET /api/connectors?view=settings.
    /// </summary>
    public static JsonArray ConnectorRows(IEnumerable<ISettingsConnector> list)
    {
        var rows = new JsonArray();
        foreach (var c in list ?? Enumerable.Empty<ISettingsConnector>()) rows.Add(c.SettingsRow());
        return rows;
    }

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
                        var cn = conns.FirstOrDefault(x => x.Id == id) ?? throw new Exception("no connector " + id);
                        var b = cn.SettingsChange(c);
                        bool keyed = J.IsStr(c["key"]);
                        try
                        {
                            await Task.Run(() => cn.Configure(b));
                        }
                        catch (ApiError e) when (keyed && e.Code == 400)
                        {
                            err = L.T("k_invalid", cn.Title);   // nothing saved, the old key (if any) stays
                        }
                        catch (ApiError e) when (e.Code == 400 && e.Message.StartsWith("guard_"))
                        {
                            err = e.Ui ?? L.T("g_bad_values", e.Message);   // nothing changed
                        }
                        // right after saving: the verdict of the check ("Connected, balance $X" / "Connected as @login"...)
                        if (keyed && err == null)
                        {
                            var (check, text) = cn.KeyVerdict();
                            if (check == "ok") info = text;
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
                            var mc = conns.OfType<MachineConnector>().FirstOrDefault(x => x.Id == J.Str(c["connector"])) ?? throw new Exception("no connector");
                            var act = J.Str(c["act"]) == "stop_now" ? "stop_now" : "snooze";
                            var gr = await Task.Run(() => mc.GuardAct(act));
                            info = J.Str(gr["message"]);
                            break;
                        }
                        default:   // a connector's own setup window (its row's "setup")
                            if (conns.FirstOrDefault(x => x.Id == J.Str(c["what"]))?.Setup is { } setup) ui(setup);
                            break;
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
