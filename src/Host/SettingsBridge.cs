using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>
/// The panel's settings page talks to the host through this (WebView2 messages from our own page only, never the HTTP
/// API: these change the system - autostart, the AI agents' settings files - so other programs on the PC can't use them).
/// The page asks {"settings":"get"} / "set" / "do" and gets back {"settings_state": State(), "error"?, "note"?}.
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
        var g = gh?.State();
        var conns = new JsonArray();
        if (g != null)
            conns.Add(new JsonObject
            {
                ["id"] = "github", ["name"] = "GitHub Actions", ["enabled"] = J.C(g["enabled"]), ["status"] = J.C(g["status"]),
                ["account"] = J.C(g["login"]), ["items"] = (g["repos"] as JsonArray)?.Count ?? 0,
            });
        // machine connectors (Runpod...): one API key pasted here (kept in the Credential Manager), on / off, a state line
        foreach (var mc in machines)
        {
            var ms = mc.State();
            conns.Add(new JsonObject
            {
                ["id"] = mc.Id, ["name"] = mc.Title, ["kind"] = "key", ["enabled"] = J.C(ms["enabled"]), ["key_set"] = J.C(ms["key_set"]),
                ["status"] = J.C(ms["status"]), ["summary"] = J.C(ms["summary"]),
            });
        }
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

    /// <summary>Runs a command from the page; returns the reply to post back (the new state, maybe an error / a note).</summary>
    public async Task<JsonObject> Handle(JsonObject c)
    {
        string err = null, note = null;
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
                    // a machine connector: on / off, the API key (checked with the service, then kept in the Credential
                    // Manager - never config.json), forget the key
                    if (J.IsStr(c["connector"]) && machines.FirstOrDefault(x => x.Id == J.Str(c["connector"])) is { } mc)
                    {
                        var b = new JsonObject();
                        if (c.ContainsKey("enabled")) b["enabled"] = J.Truthy(c["enabled"]);
                        if (J.IsStr(c["key"])) { b["key"] = J.Str(c["key"]); b["enabled"] = true; }
                        if (J.Truthy(c["forget_key"])) b["forget_key"] = true;
                        await Task.Run(() => mc.Configure(b));
                        if (b.ContainsKey("key")) note = L.T("m_key_saved", mc.Title);
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
                    }
                    break;
            }
        }
        catch (Exception e) { err = e.Message; Log.W("settings: " + e.Message); }
        var r = new JsonObject { ["settings_state"] = State() };
        if (err != null) r["error"] = err;
        if (note != null) r["note"] = note;
        return r;
    }
}
