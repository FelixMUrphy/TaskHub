namespace TaskHub;

/// <summary>Tray menu -> Telegram...: paste the bot token, press the t.me link, done.</summary>
sealed class TelegramForm : Form
{
    static TelegramForm open;
    readonly TelegramBridge tg;
    readonly TextBox token;
    readonly Button connect, test, disconnect;
    readonly Label state, qrHint;
    readonly PictureBox qr;
    readonly LinkLabel link;
    readonly CheckBox away;
    readonly System.Windows.Forms.Timer timer;
    bool busy;

    public static void ShowFor(TelegramBridge tg)
    {
        if (open != null && !open.IsDisposed) { open.Activate(); return; }
        open = new TelegramForm(tg);
        open.Show();
    }

    TelegramForm(TelegramBridge tg)
    {
        this.tg = tg;
        Text = "TaskHub — " + L.T("tg_title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(16), Dock = DockStyle.Fill };
        var intro = new LinkLabel { Text = L.T("tg_intro"), AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 12) };
        int at = intro.Text.IndexOf("@BotFather", StringComparison.Ordinal);
        if (at >= 0) intro.Links.Add(at, "@BotFather".Length, "https://t.me/BotFather");
        intro.LinkClicked += (_, e) => Open(e.Link.LinkData as string);
        root.Controls.Add(intro);

        root.Controls.Add(new Label { Text = L.T("tg_token"), AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        token = new TextBox { Width = 400, UseSystemPasswordChar = true };
        connect = new Button { Text = L.T("tg_connect"), AutoSize = true };
        connect.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; connect.Enabled = false;
            var t = token.Text;
            string err = null;
            // empty field + a bot already set up -> keep the bot, link another chat (new code)
            try { await Task.Run(() => string.IsNullOrWhiteSpace(t) && J.Truthy(tg.State()["configured"]) ? tg.Relink() : tg.Pair(t)); }
            catch (Exception ex) { err = ex.Message; }
            busy = false; connect.Enabled = true;
            if (err != null) MessageBox.Show(this, err, "TaskHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else { token.Clear(); Refresh_(); }   // not opened automatically: the Telegram account may be on the phone
        };
        row.Controls.Add(token);
        row.Controls.Add(connect);
        root.Controls.Add(row);

        state = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 12, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        qrHint = new Label { Text = L.T("tg_qr"), AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 8, 0, 4) };
        qr = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(200, 200), Margin = new Padding(0, 0, 0, 4) };
        link = new LinkLabel { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 4, 0, 0) };
        link.LinkClicked += (_, e) => Open(e.Link.LinkData as string);
        root.Controls.Add(state);
        root.Controls.Add(qrHint);
        root.Controls.Add(qr);
        root.Controls.Add(link);

        away = new CheckBox { Text = L.T("tg_away"), AutoSize = true, Checked = tg.AwayOnly, Margin = new Padding(0, 12, 0, 0) };
        away.CheckedChanged += (_, _) => tg.SetAwayOnly(away.Checked);
        root.Controls.Add(away);
        var prog = new CheckBox { Text = L.T("tg_progress"), AutoSize = true, Checked = tg.ShowProgress, Margin = new Padding(0, 6, 0, 0) };
        prog.CheckedChanged += (_, _) => tg.SetProgress(prog.Checked);
        root.Controls.Add(prog);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        var close = new Button { Text = L.T("tg_close"), AutoSize = true };
        close.Click += (_, _) => Close();
        disconnect = new Button { Text = L.T("tg_disconnect"), AutoSize = true };
        disconnect.Click += (_, _) => { tg.Disconnect(); Refresh_(); };
        test = new Button { Text = L.T("tg_test"), AutoSize = true };
        test.Click += (_, _) => tg.Test();
        buttons.Controls.Add(close);
        buttons.Controls.Add(disconnect);
        buttons.Controls.Add(test);
        root.Controls.Add(buttons);
        Controls.Add(root);
        CancelButton = close;

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => Refresh_();
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
        Refresh_();
    }

    void Refresh_()
    {
        var s = tg.State();
        bool linked = J.Truthy(s["linked"]), configured = J.Truthy(s["configured"]);
        var st = J.Str(s["status"]);
        var lnk = J.Str(s["link"]);
        state.Text = linked ? L.T("tg_state_ok", J.Str(s["bot"])) + (st == "connected" ? "" : "  (" + st + ")")
            : configured ? (lnk.Length > 0 ? L.T("tg_state_wait") : st) : L.T("tg_state_off");
        link.Visible = qr.Visible = qrHint.Visible = !linked && lnk.Length > 0;
        if (link.Visible && link.Tag as string != lnk)
        {
            link.Tag = lnk;
            link.Text = L.T("tg_open", lnk);
            link.Links.Clear();
            link.Links.Add(link.Text.IndexOf(lnk, StringComparison.Ordinal), lnk.Length, lnk);
            try
            {
                using var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(lnk, QRCoder.QRCodeGenerator.ECCLevel.M);
                var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
                qr.Image?.Dispose();
                qr.Image = Image.FromStream(new MemoryStream(png));
            }
            catch (Exception e) { Log.W("qr: " + e.Message); qr.Visible = qrHint.Visible = false; }
        }
        token.PlaceholderText = configured ? L.T("tg_relink_ph") : "123456789:AA...";
        test.Enabled = disconnect.Enabled = configured;
        test.Enabled = linked;
    }

    static void Open(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }
}
