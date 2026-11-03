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
    string qrFor, qrData;   // the QR code of the current t.me link, made once per link

    public SettingsBridge(Settings s, Hub h, TelegramBridge t, IEnumerable<ISettingsConnector> c, Func<Updater> u, Action<Action> uiThread)
    {
        S = s; hub = h; tg = t; conns = c?.ToArray() ?? Array.Empty<ISettingsConnector>(); updater = u; ui = uiThread;
    }

    public JsonObject State()
    {
        var agents = new JsonArray();
        foreach (var a in Integrations.All)
        {
            var row = new JsonObject
            {
                ["agent"] = a == Integrations.Agent.Claude ? "claude" : "codex", ["name"] = Integrations.Name(a),
                ["hooks"] = Integrations.Enabled(a, Integrations.Kind.Hooks), ["mcp"] = Integrations.Enabled(a, Integrations.Kind.Mcp),
                ["detected"] = Integrations.Detected(a),
            };
            row["limits"] = Integrations.Enabled(a, Integrations.Kind.Limits);
            if (a == Integrations.Agent.Claude) row["limits_wraps"] = Integrations.HasOwnStatusLine();
            agents.Add(row);
        }
        var t = tg?.State();
        var rows = ConnectorRows(conns);
        var up = updater();
        up?.RecheckIfStale();   // open page: a remembered "Update to X" may be days old (the hub runs on while the panel is hidden)
        // the host's own translations of what used to be the tray's items (one source of the words in 10 languages)
        var labels = new JsonObject();
        foreach (var k in new[] { "mute", "test_toast", "connectors", "integrations", "menu_hooks", "menu_mcp", "menu_limits", "menu_limits_codex", "limits_d_codex", "limits_d_new", "limits_d_wrap", "autostart",
                     "updates", "update_check_now", "update_auto", "data_folder", "api_help", "support" })
            labels[k] = L.T(k).TrimStart('❤', ' ');
        if (up?.Available is { } av) labels["update_to"] = L.T("update_to", av.Version.ToString(3)).TrimStart('⬆', ' ').TrimEnd('…');
        return new JsonObject
        {
            ["labels"] = labels,
            ["version"] = Settings.VersionText, ["edition"] = Install.Edition,
            ["autostart"] = Install.AutostartEnabled(S),
            ["muted"] = hub.Muted,
            ["notifications"] = hub.Notifier?.SystemSetting,   // "disabled_*": turned off in Windows -> a warning row
            ["updates"] = up == null ? null : new JsonObject { ["auto"] = up.Auto, ["available"] = up.Available?.Version.ToString(3) },
            ["agents"] = agents,
            ["telegram"] = t == null ? null : TelegramState(t),
            // every connector the same way: what it is, on / off, a short state line; its own window for the details
            ["connectors"] = rows,
        };
    }

    /// <summary>Telegram as the settings page shows it (set up right there: the token, the t.me link and its QR code,
    /// the switches, Send test / New link / Disconnect). Never the token. The words come from the host's translations.</summary>
    JsonObject TelegramState(JsonObject t)
    {
        bool linked = J.Truthy(t["linked"]), configured = J.Truthy(t["configured"]);
        string st = J.Str(t["status"]), bot = J.Str(t["bot"]), link = J.Str(t["link"]);
        if (linked || link.Length == 0) { qrFor = qrData = null; }
        else if (link != qrFor)
        {
            qrFor = link; qrData = null;
            try
            {
                using var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(link, QRCoder.QRCodeGenerator.ECCLevel.M);
                qrData = "data:image/png;base64," + Convert.ToBase64String(new QRCoder.PngByteQRCode(data).GetGraphic(8));
            }
            catch (Exception e) { Log.W("settings", "qr: " + e.Message); }
        }
        string state = linked ? L.T("tg_state_ok", bot) + (st == "connected" ? "" : "  (" + TgStatus(st) + ")")
            : configured ? (link.Length > 0 ? L.T("tg_state_wait") : TgStatus(st)) : L.T("tg_state_off");
        return new JsonObject
        {
            ["configured"] = configured, ["linked"] = linked, ["bot"] = J.C(t["bot"]), ["status"] = st,
            ["warning"] = J.C(t["warning"]),
            ["away"] = J.Truthy(t["away_only"]), ["progress"] = J.Truthy(t["progress"]),
            ["link"] = linked || link.Length == 0 ? null : link, ["qr"] = linked ? null : qrData,
            ["texts"] = new JsonObject
            {
                ["state"] = state, ["intro"] = L.T("tg_intro"), ["token"] = L.T("tg_token").TrimEnd(':', '：', ' '),
                ["connect"] = L.T("tg_connect"), ["qr"] = L.T("tg_qr"), ["open"] = L.T("tg_open", "").TrimEnd(),
                ["away"] = L.T("tg_away"), ["progress"] = L.T("tg_progress"), ["test"] = L.T("tg_test"),
                ["relink"] = L.T("tg_relink"), ["relink_q"] = L.T("tg_relink_q"), ["disconnect"] = L.T("tg_disconnect"), ["disconnect_q"] = L.T("tg_disconnect_q"),
            },
        };
    }

    /// <summary>TelegramBridge.Status (English, also in the API) in the UI language.</summary>
    static string TgStatus(string st) => st switch
    {
        "connecting" => L.T("tg_st_connecting"),
        "invalid token" => L.T("tg_rejected"),
        "waiting for /start" => L.T("tg_state_wait"),
        _ when st.StartsWith("another program") => L.T("tg_st_conflict"),
        _ when st.StartsWith("offline") => L.T("tg_st_offline"),
        _ => st,
    };

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
        Log.D("settings", J.Str(c["settings"]) + " " + J.Str(c["what"]) + " [" + string.Join(",", c.Select(kv => kv.Key).Where(k => k is not ("settings" or "what"))) + "]");   // names, never values
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
                    if (c.ContainsKey("panel_push_windows")) hub.SetPushWindows(J.Truthy(c["panel_push_windows"]));
                    if (c.ContainsKey("tg_away")) tg?.SetAwayOnly(J.Truthy(c["tg_away"]));
                    if (c.ContainsKey("tg_progress")) tg?.SetProgress(J.Truthy(c["tg_progress"]));
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
                            err = e.Ui ?? L.T("k_invalid", cn.Title);   // nothing saved, the old key (if any) stays
                        }
                        catch (ApiError e) when (e.Code == 400 && (e.Message.StartsWith("guard_") || e.Ui != null))
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
                        var k = J.Str(c["kind"]) switch { "mcp" => Integrations.Kind.Mcp, "limits" => Integrations.Kind.Limits, _ => Integrations.Kind.Hooks };
                        bool on = J.Truthy(c["on"]);
                        err = await Task.Run(() => Integrations.Set(a, k, on, S));
                        if (err == null && on) note = Integrations.Hint(a, k);
                        if (err == null && !on && k == Integrations.Kind.Limits) hub.ClearLimits(a == Integrations.Agent.Codex ? "codex" : "claude");
                        if (err == null && on && k == Integrations.Kind.Limits && a == Integrations.Agent.Codex) hub.ReadCodexLimitsNow();
                        if (k == Integrations.Kind.Hooks) await Task.Run(hub.RefreshHookOffer);
                    }
                    break;
                case "do":
                    switch (J.Str(c["what"]))
                    {
                        // the card that offers an agent's hooks: the first write to the agent's files after the user's click;
                        // "not now" is remembered as that choice and writes nothing but TaskHub's own config
                        case "hooks_offer":
                        {
                            // only the agents the card named: one found since then is not part of this answer
                            bool connect = J.Truthy(c["connect"]);
                            var named = (c["agents"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).ToHashSet();
                            var errors = new List<string>();
                            var hints = new List<string>();
                            foreach (var a in await Task.Run(() => Integrations.HookOffer(S)))
                            {
                                if (!named.Contains(Integrations.ArgName(a, Integrations.Kind.Hooks))) continue;
                                if (!connect) { S.Set((Integrations.CfgKey(a), false)); continue; }
                                var e = await Task.Run(() => Integrations.Set(a, Integrations.Kind.Hooks, true, S));
                                if (e != null) errors.Add(e); else hints.Add(Integrations.Hint(a, Integrations.Kind.Hooks));
                            }
                            // one agent failing must not hide the step the other one still needs
                            if (errors.Count > 0) err = string.Join("\n\n", errors.Concat(hints));
                            else if (hints.Count > 0) note = string.Join("\n", hints);
                            await Task.Run(hub.RefreshHookOffer);
                            break;
                        }
                        // Telegram, set up on the page: the link is not opened by itself (the Telegram account may be on the phone)
                        case "tg_pair":
                        case "tg_relink":
                            if (tg == null) break;
                            try
                            {
                                bool fresh = J.Str(c["what"]) == "tg_pair";
                                var st = await Task.Run(() => fresh ? tg.Pair(J.Str(c["token"])) : tg.Relink());
                                // always an answer (the page shows "Checking…" until one comes)
                                info = J.Str(st["link"]).Length > 0 ? L.T("tg_state_wait") : TgStatus(J.Str(st["status"]));
                            }
                            catch (ApiError e) when (e.Code == 400) { err = L.T(e.Message.StartsWith("Telegram") ? "tg_rejected" : "tg_bad_token"); }
                            break;
                        case "tg_test": if (tg != null && tg.Linked) { tg.Test(); info = L.T("tg_test_sent"); } break;
                        case "tg_disconnect": tg?.Disconnect(); break;
                        case "test_toast":
                            hub.Notify(new JsonObject { ["title"] = "TaskHub", ["message"] = L.T("test_toast") + " ✓", ["force"] = true });
                            break;
                        case "data_folder": Hub.OpenTarget(S.DataDir); break;
                        case "notif_settings": Hub.OpenTarget("ms-settings:notifications"); break;
                        case "colors_settings": Hub.OpenTarget("ms-settings:colors"); break;
                        case "energy_settings":   // ms-settings:batterysaver exists only on devices with a battery
                            Hub.OpenTarget(SystemInformation.PowerStatus.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery)
                                ? "ms-settings:powersleep" : "ms-settings:batterysaver");
                            break;
                        case "api_help":
                        {
                            var p = Path.Combine(S.ExeDir, "API.md");
                            if (!File.Exists(p)) { p = S.File_("API.md"); File.WriteAllBytes(p, Res.Read("API.md")); }
                            Hub.OpenTarget(p);
                            break;
                        }
                        case "update_check_now":   // the answer is shown on the page (a notification may be turned off)
                            if (updater() is { } u1) info = await u1.CheckNow();
                            break;
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
        catch (Exception e) { err = e.Message; Log.W("settings", "settings: " + e.Message); }
        var r = new JsonObject { ["settings_state"] = State() };
        if (err != null) r["error"] = err;
        if (note != null) r["note"] = note;
        if (info != null) r["info"] = info;   // shown without a dialog
        return r;
    }
}
