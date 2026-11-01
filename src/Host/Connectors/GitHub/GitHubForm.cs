using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>Tray menu -> Connectors -> GitHub Actions...: access (own token / Git Credential Manager), repositories, options.</summary>
sealed class GitHubForm : Form
{
    const string NewTokenUrl = "https://github.com/settings/personal-access-tokens/new";
    static GitHubForm open;
    readonly GitHubConnector gh;
    readonly CheckBox enabled, notifySuccess, logs;
    readonly RadioButton authToken, authGcm;
    readonly TextBox token, repos;
    readonly NumericUpDown interval;
    readonly Label tokenState, gcmWarn, state;
    readonly Button save, forget;
    readonly System.Windows.Forms.Timer timer;
    bool busy;

    public static void ShowFor(GitHubConnector gh)
    {
        if (open != null && !open.IsDisposed) { open.Activate(); return; }
        open = new GitHubForm(gh);
        open.Show();
    }

    GitHubForm(GitHubConnector gh)
    {
        this.gh = gh;
        Text = "TaskHub — " + L.T("gh_title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        var s = gh.State();
        const int W = 540;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(16), Dock = DockStyle.Fill };
        root.Controls.Add(new Label { Text = L.T("gh_intro"), AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 0, 0, 10) });
        enabled = new CheckBox { Text = L.T("gh_enabled"), AutoSize = true, Checked = J.Truthy(s["enabled"]), Font = new Font(Font, FontStyle.Bold) };
        root.Controls.Add(enabled);

        // access
        root.Controls.Add(new Label { Text = L.T("gh_access"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 2) });
        authToken = new RadioButton { Text = L.T("gh_auth_token"), AutoSize = true, Checked = J.Str(s["auth"]) != "gcm" };
        root.Controls.Add(authToken);
        var link = new LinkLabel { Text = L.T("gh_create_token"), AutoSize = true, Margin = new Padding(20, 0, 0, 2) };
        link.LinkClicked += (_, _) => Open(NewTokenUrl);
        root.Controls.Add(link);
        root.Controls.Add(new Label { Text = L.T("gh_perm_hint"), AutoSize = true, MaximumSize = new Size(W - 20, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(20, 0, 0, 4) });
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(20, 0, 0, 0) };
        token = new TextBox { Width = 360, UseSystemPasswordChar = true, PlaceholderText = L.T("gh_token_ph") };
        forget = new Button { Text = L.T("gh_forget"), AutoSize = true };
        forget.Click += async (_, _) => await Apply(new JsonObject { ["forget_token"] = true });
        row.Controls.Add(token);
        row.Controls.Add(forget);
        root.Controls.Add(row);
        tokenState = new Label { AutoSize = true, MaximumSize = new Size(W - 20, 0), Margin = new Padding(20, 2, 0, 6) };
        root.Controls.Add(tokenState);
        authGcm = new RadioButton { Text = L.T("gh_auth_gcm"), AutoSize = true, Checked = J.Str(s["auth"]) == "gcm" };
        root.Controls.Add(authGcm);
        gcmWarn = new Label { Text = L.T("gh_gcm_warn"), AutoSize = true, MaximumSize = new Size(W - 20, 0), ForeColor = Color.FromArgb(180, 90, 0), Margin = new Padding(20, 0, 0, 0) };
        root.Controls.Add(gcmWarn);
        authToken.CheckedChanged += (_, _) => Sync();

        // what to watch
        root.Controls.Add(new Label { Text = L.T("gh_repos"), AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
        repos = new TextBox
        {
            Multiline = true, Width = W, Height = 90, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
            Text = string.Join(Environment.NewLine, (s["repos"] as JsonArray ?? new JsonArray()).Select(x => J.Str(x))),
            PlaceholderText = "FelixMUrphy/TaskHub",
        };
        root.Controls.Add(repos);
        var irow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        irow.Controls.Add(new Label { Text = L.T("gh_interval"), AutoSize = true, Margin = new Padding(0, 5, 6, 0) });
        interval = new NumericUpDown { Minimum = 15, Maximum = 3600, Increment = 15, Width = 80, Value = Math.Clamp((decimal)(J.Num(s["interval"]) ?? 60), 15, 3600) };
        irow.Controls.Add(interval);
        root.Controls.Add(irow);
        notifySuccess = new CheckBox { Text = L.T("gh_notify_success"), AutoSize = true, Checked = J.Truthy(s["notify_success"]), Margin = new Padding(0, 8, 0, 0) };
        root.Controls.Add(notifySuccess);
        logs = new CheckBox { Text = L.T("gh_logs"), AutoSize = true, MaximumSize = new Size(W, 0), Checked = J.Truthy(s["logs"]), Margin = new Padding(0, 6, 0, 0) };
        root.Controls.Add(logs);

        state = new Label { AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 12, 0, 0) };
        root.Controls.Add(state);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        var close = new Button { Text = L.T("tg_close"), AutoSize = true };
        close.Click += (_, _) => Close();
        save = new Button { Text = L.T("gh_save"), AutoSize = true };
        save.Click += async (_, _) =>
        {
            var b = new JsonObject
            {
                ["enabled"] = enabled.Checked, ["auth"] = authGcm.Checked ? "gcm" : "token",
                ["repos"] = repos.Text, ["interval"] = (int)interval.Value,
                ["notify_success"] = notifySuccess.Checked, ["logs"] = logs.Checked,
            };
            if (token.Text.Trim().Length > 0) b["token"] = token.Text.Trim();
            if (await Apply(b)) token.Clear();
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(save);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = close;

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => Refresh_();
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
        Sync();
        Refresh_();
    }

    async Task<bool> Apply(JsonObject b)
    {
        if (busy) return false;
        busy = true; save.Enabled = forget.Enabled = false;
        string err = null;
        try { await Task.Run(() => gh.Configure(b)); }
        catch (Exception ex) { err = ex.Message; }
        busy = false; save.Enabled = true;
        Refresh_();
        if (err != null) MessageBox.Show(this, err, "TaskHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return err == null;
    }

    void Sync()
    {
        token.Enabled = authToken.Checked;
        gcmWarn.Enabled = authGcm.Checked;
    }

    void Refresh_()
    {
        var s = gh.State();
        bool set = J.Truthy(s["token_set"]);
        var login = J.Str(s["login"]);
        var exp = J.Num(s["token_expires"]) is double e ? DateTimeOffset.FromUnixTimeSeconds((long)e).ToLocalTime() : (DateTimeOffset?)null;
        tokenState.Text = !set ? L.T("gh_no_token")
            : (login.Length > 0 && J.Str(s["auth"]) == "token" ? L.T("gh_token_ok", login) : L.T("gh_token_saved"))
              + (exp is { } x ? "  ·  " + L.T("gh_expires", x.ToString("d MMM yyyy")) : "");
        tokenState.ForeColor = exp is { } x2 && x2 - DateTimeOffset.Now < TimeSpan.FromDays(7) ? Color.FromArgb(190, 40, 40) : SystemColors.ControlText;
        forget.Enabled = set && !busy;
        var st = J.Str(s["status"]);
        var lines = new List<string>();
        if (J.Truthy(s["enabled"]))
        {
            var n = (s["repos"] as JsonArray)?.Count ?? 0;
            lines.Add(st == "ok" ? L.N("gh_watching", n) + (login.Length > 0 ? "  @" + login : "") : st);
            foreach (var (repo, rs) in s["repo_status"] as JsonObject ?? new JsonObject())
                if (J.Str(rs) is { Length: > 0 } v && v != "ok") lines.Add("• " + repo + ": " + v);
            if (J.Num(s["rate_remaining"]) is double rl && rl < 1000) lines.Add("API: " + J.Fmt(rl) + " requests left this hour");
        }
        state.Text = string.Join("\n", lines);
    }

    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }
}
