using System.Diagnostics;

namespace TaskHub;

/// <summary>
/// The setup window (setup exe from GitHub) in the Windows 11 style and the panel's colours. It opens at once and stays
/// for the whole job: options → progress → done ("Open TaskHub") or the error; the same for uninstall (confirm → progress
/// → done) and for the two-button questions (Store edition next to the setup-exe one). The quiet / scripted modes don't use it.
/// Layout: one column of blocks (texts, a folder field, cards of switches) and a row of buttons at the bottom right,
/// all sized from the window's DPI (no WinForms auto-scaling).
/// </summary>
sealed class SetupForm : Form
{
    public enum Choice { Cancel, Install, Run }

    // ------------------------------------------------------------ state shown
    sealed class Block
    {
        public Control C;
        public int Card = -1;             // blocks with the same card number sit on one rounded card
        public float Gap;                 // space above, in 100 % pixels
        public float Height;              // fixed height (100 % px); 0 = measured (texts, switches)
        public bool Full;                 // a card row: the whole card width, no inner padding
    }

    readonly Settings S;
    Fluent.Theme T;
    readonly List<Block> blocks = new();
    readonly List<Fluent.Button> buttons = new();
    string head, sub, glyph;
    Color glyphColor;
    bool busy;
    Font fBody, fSmall, fTitle, fSection;

    public SetupForm(Settings s, string caption)
    {
        S = s;
        T = Fluent.Theme.For(s);
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        BackColor = T.Bg;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Fluent.TitleBar(this, T.Dark);
    }

    // DeviceDpi is right once the handle exists: lay out here (before the window shows), then centre at the final size
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Done();
        CenterToScreen();
    }

    // AutoScaleMode.None: WinForms scales only the fonts and keeps the size; lay out again after it, with our own fonts
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy) { e.Cancel = true; return; }   // never in the middle of copying / registering
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && !busy) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // ------------------------------------------------------------ building pages
    void Clear()
    {
        foreach (var b in blocks) b.C.Dispose();
        foreach (var b in buttons) b.Dispose();
        blocks.Clear();
        buttons.Clear();
        AcceptButton = CancelButton = null;
    }

    sealed class TextBlock : Fluent.Item
    {
        public bool Muted;
        public TextBlock(Fluent.Theme t, string text, bool muted = false) : base(t) { Text = text; Muted = muted; TabStop = false; }
        public int Measure(int w) => string.IsNullOrEmpty(Text) ? 0
            : TextRenderer.MeasureText(Text, Font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
        protected override void OnPaint(PaintEventArgs e) =>
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Muted ? T.Mut : T.Fg,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    TextBlock AddText(string text, float gap = 12, bool muted = false, bool section = false, int card = -1)
    {
        var t = new TextBlock(T, text, muted) { Tag = section ? "section" : muted ? "small" : null };
        blocks.Add(new Block { C = t, Gap = gap, Card = card });
        Controls.Add(t);
        return t;
    }

    Fluent.Toggle AddToggle(string text, bool on, int card, string desc = null, float gap = 0)
    {
        var t = new Fluent.Toggle(T, text, on, desc);
        blocks.Add(new Block { C = t, Card = card, Full = true, Gap = gap });
        Controls.Add(t);
        return t;
    }

    Fluent.Button AddButton(string text, Action click, bool primary = false)
    {
        var b = new Fluent.Button(T, text, primary);
        b.Click += (_, _) => click();
        buttons.Add(b);
        Controls.Add(b);
        return b;
    }

    void Page(string title, string subtitle, string icon = null, Color? iconColor = null)
    {
        Clear();
        head = title; sub = subtitle; glyph = icon; glyphColor = iconColor ?? T.Acc;
    }

    void Done()
    {
        Relayout();
        var first = blocks.Select(b => b.C).FirstOrDefault(c => c.TabStop && c.CanSelect) ?? buttons.FirstOrDefault(b => b.Primary) ?? buttons.FirstOrDefault();
        (buttons.FirstOrDefault(b => b.Primary) ?? first)?.Select();
        Invalidate();
    }

    // ------------------------------------------------------------ layout
    const float Width100 = 480, Margin100 = 24, HeaderH = 64, ButtonH = 32, CardPad = 12;

    void Relayout()
    {
        if (!IsHandleCreated) return;
        T.S = DeviceDpi / 96f;
        fBody?.Dispose(); fSmall?.Dispose(); fTitle?.Dispose(); fSection?.Dispose();
        fBody = T.Font(14); fSmall = T.Font(12); fTitle = T.Font(20, FontStyle.Bold); fSection = T.Font(14, FontStyle.Bold);
        int m = T.Px(Margin100), w = T.Px(Width100), y = T.Px(HeaderH) + T.Px(8);
        int prevCard = -1;
        SuspendLayout();
        foreach (var b in blocks)
        {
            bool inCard = b.Card >= 0;
            if (b.Card != prevCard && prevCard >= 0) y += T.Px(4);          // end of the previous card
            if (inCard && b.Card != prevCard) y += T.Px(b.Gap);              // start of a card
            else if (!inCard || b.Gap > 0) y += T.Px(b.Gap);
            int x = m, cw = w;
            if (inCard && !b.Full) { x += T.Px(CardPad); cw -= 2 * T.Px(CardPad); }
            b.C.Font = b.C.Tag as string == "section" ? fSection : b.C.Tag as string == "small" ? fSmall : fBody;
            if (b.C is Fluent.Toggle tg) tg.DescFont = fSmall;
            int h = b.Height > 0 ? T.Px(b.Height)
                : b.C is TextBlock tb ? tb.Measure(cw)
                : b.C is Fluent.Toggle t2 ? t2.PreferredHeight(cw) : b.C.Height;
            if (inCard && !b.Full && (b.Card != prevCard)) y += T.Px(CardPad);   // padding inside a card above content
            b.C.SetBounds(x, y, cw, h);
            if (b.C is Fluent.Item it) it.SetTheme(T);
            else if (b.C is Fluent.Toggle tt) tt.SetTheme(T);
            y += h;
            if (inCard && !b.Full) y += T.Px(CardPad);
            prevCard = b.Card;
        }
        if (prevCard >= 0) y += T.Px(4);
        y += T.Px(24);
        // buttons: right-aligned, primary at the right (Windows 11 dialogs)
        int bx = m + w, bh = T.Px(ButtonH);
        foreach (var b in buttons.OrderBy(b => b.Primary ? 0 : 1))
        {
            b.Font = fBody;
            int bw = b.PreferredWidth(fBody);
            bx -= bw;
            b.SetBounds(bx, y, bw, bh);
            b.SetTheme(T);
            bx -= T.Px(8);
        }
        if (buttons.Count > 0) y += bh;
        y += m;
        ResumeLayout();
        var size = new Size(w + 2 * m, y);
        if (ClientSize != size)
        {
            var c = new Point(Left + Width / 2, Top + Height / 2);
            bool shown = Visible;
            ClientSize = size;
            if (shown) Location = new Point(c.X - Width / 2, c.Y - Height / 2);   // grow / shrink around the centre
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(T.Bg);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int m = T.Px(Margin100);
        // header: the state glyph (or the app icon) and the title with a grey line under it
        float iconCx = m + T.Px(16), iconCy = T.Px(Margin100) + T.Px(16);
        if (glyph != null) Fluent.Glyph(g, glyph, T.Px(28), new PointF(iconCx, iconCy), glyphColor);
        else if (Icon != null)
        {
            using var ic = new Icon(Icon, T.Px(32), T.Px(32));
            g.DrawIcon(ic, new Rectangle(m, T.Px(Margin100), T.Px(32), T.Px(32)));
        }
        int tx = m + T.Px(48), tw = ClientSize.Width - tx - m;
        if (fTitle != null)
        {
            TextRenderer.DrawText(g, head, fTitle, new Rectangle(tx, T.Px(Margin100) - T.Px(4), tw, T.Px(28)), T.Fg,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (!string.IsNullOrEmpty(sub))
                TextRenderer.DrawText(g, sub, fSmall, new Rectangle(tx, T.Px(Margin100) + T.Px(24), tw, T.Px(18)), T.Mut,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        // cards: rounded, a hairline border, hairlines between switch rows
        foreach (var grp in blocks.Where(b => b.Card >= 0).GroupBy(b => b.Card))
        {
            var list = grp.ToList();
            var first = list[0].C.Bounds; var last = list[^1].C.Bounds;
            int top = first.Top - (list[0].Full ? 0 : T.Px(CardPad)), bottom = last.Bottom + (list[^1].Full ? 0 : T.Px(CardPad));
            var r = new RectangleF(m + 0.5f, top + 0.5f, T.Px(Width100) - 1, bottom - top - 1);
            using var path = Fluent.Round(r, 8 * T.S);
            using (var b = new SolidBrush(T.Card)) g.FillPath(b, path);
            using var p = new Pen(T.Line);
            g.DrawPath(p, path);
            for (int i = 1; i < list.Count; i++)
                if (list[i].Full && list[i - 1].Full) g.DrawLine(p, m + 1, list[i].C.Top, m + T.Px(Width100) - 1, list[i].C.Top);
        }
    }

    // ------------------------------------------------------------ setup: options
    public Choice Result = Choice.Cancel;
    Fluent.Field folder;
    Fluent.Toggle tAuto, tPath;
    readonly List<(Integrations.Agent a, Integrations.Kind k, Fluent.Toggle t)> agentToggles = new();
    public string Folder => folder?.Box.Text.Trim();
    public bool Autostart => tAuto?.Checked ?? true;
    public bool AddPath => tPath?.Checked ?? true;
    public IEnumerable<(Integrations.Agent agent, Integrations.Kind kind, bool on)> Agents => agentToggles.Select(x => (x.a, x.k, x.t.Checked));

    /// <summary>Default for an agent switch: the earlier choice on update, else on if the agent is used on this PC.</summary>
    static bool AgentDefault(Integrations.Agent a, Integrations.Kind k)
    {
        try
        {
            if (L.S?.Get(Integrations.CfgKey(a, k)) is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var on)) return on;
            return Integrations.Enabled(a, k) || Integrations.Detected(a);
        }
        catch { return false; }
    }

    /// <summary>What the setup will do: install, update (an older one is installed), reinstall or install an older version.</summary>
    public static (string title, string intro, string button, int cmp) Describe(bool installed, Version installedVersion)
    {
        var mine = Version.Parse(Settings.Version);
        var have = installedVersion == null ? null
            : new Version(installedVersion.Major, installedVersion.Minor, Math.Max(installedVersion.Build, 0));
        int cmp = have == null ? -1 : Math.Sign(have.CompareTo(mine));
        string verText = have?.ToString() ?? "?";
        string title = !installed ? "setup_title" : cmp < 0 ? "setup_update" : cmp == 0 ? "setup_reinstall" : "setup_downgrade";
        string intro = !installed ? L.T("setup_intro")
            : L.T("setup_installed", verText, Install.InstallDir) + "\n" + (cmp < 0 ? L.T("setup_will_update", Settings.Version)
                : cmp == 0 ? L.T("setup_will_reinstall") : L.T("setup_will_downgrade", Settings.Version));
        string button = !installed ? L.T("setup_install") : cmp < 0 ? L.T("setup_update_btn")
            : cmp == 0 ? L.T("setup_reinstall_btn") : L.T("setup_downgrade_btn", Settings.Version);
        return (L.T(title), intro, button, cmp);
    }

    /// <summary>The options page. Install checks the folder, asks about the Store edition if storeQuestion() gives a text,
    /// then calls install() - which runs the job in this window (progress, then done / error).</summary>
    public void ShowOptions(bool installed, Version installedVersion, Func<string> storeQuestion, Action install)
    {
        var (title, intro, button, _) = Describe(installed, installedVersion);
        Page(title, "TaskHub " + Settings.Version);
        AddText(intro, gap: 0);
        AddText(L.T("setup_folder").TrimEnd(':', '：'), gap: 16, section: true);
        folder = new Fluent.Field(T, installed ? Install.InstallDir : Install.DefaultDir);
        var browse = new Fluent.Button(T, "…") { AccessibleName = L.T("setup_folder").TrimEnd(':', '：') + " …" };
        folder.Controls.Add(browse);
        folder.Accessory = browse;
        browse.Behind = folder.Box.BackColor;
        blocks.Add(new Block { C = folder, Gap = 8, Height = 32 });
        Controls.Add(folder);
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = L.T("setup_folder"), UseDescriptionForTitle = true, ShowNewFolderButton = true };
            try { d.InitialDirectory = Directory.Exists(Folder) ? Folder : Path.GetDirectoryName(Folder); } catch { }
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var p = d.SelectedPath;
            // picking a parent folder (e.g. D:\Tools) installs into its TaskHub subfolder
            if (!string.Equals(Path.GetFileName(p.TrimEnd('\\')), "TaskHub", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(Path.Combine(p, "TaskHubHost.exe")))
                p = Path.Combine(p, "TaskHub");
            folder.Box.Text = p;
        };
        // over an existing install: keep its current autostart / PATH choice
        bool auto = true, path = true;
        if (installed)
        {
            try { auto = Install.AutostartEnabled(S); path = Install.OnPath(Install.InstallDir); } catch { }
        }
        tAuto = AddToggle(L.T("setup_autostart"), auto, 1, gap: 16);
        tPath = AddToggle(L.T("setup_path"), path, 1);
        AddText(L.T("integrations"), gap: 16, section: true);
        agentToggles.Clear();
        bool firstAgent = true;
        foreach (var a in Integrations.All)
            foreach (var k in Integrations.Kinds)
            {
                var t = AddToggle(L.T(k == Integrations.Kind.Hooks ? "setup_agent" : "setup_mcp", Integrations.Name(a)), AgentDefault(a, k), 2, gap: firstAgent ? 8 : 0);
                firstAgent = false;
                agentToggles.Add((a, k, t));
            }
        var bInstall = AddButton(button, () =>
        {
            if (string.IsNullOrWhiteSpace(Folder) || !Path.IsPathRooted(Folder)) { Overlay(L.T("setup_error"), L.T("setup_bad_folder"), null, null); return; }
            var err = Install.CheckWritable(Folder);
            if (err != null) { Overlay(L.T("setup_error"), L.T("setup_no_access", Folder) + "\n\n" + err, null, null); return; }
            var q = storeQuestion?.Invoke();
            if (q != null) { Overlay("TaskHub", q, L.T("setup_store_replace"), install); return; }
            install();
        }, primary: true);
        AddButton(L.T("setup_run"), () => { Result = Choice.Run; Close(); });
        var bCancel = AddButton(L.T("setup_cancel"), () => { Result = Choice.Cancel; Close(); });
        AcceptButton = bInstall;
        CancelButton = bCancel;
        if (IsHandleCreated) Done();
    }

    // a message / question over the options page, which stays (hidden) with what the user chose
    readonly List<Block> hiddenBlocks = new();
    readonly List<Fluent.Button> hiddenButtons = new();
    string hiddenHead, hiddenSub;

    /// <summary>Shows text over the current page: with yes = null only "OK" (back), else yes (back, then go) and Cancel (back).</summary>
    void Overlay(string title, string text, string yes, Action go)
    {
        hiddenHead = head; hiddenSub = sub;
        foreach (var b in blocks) { b.C.Visible = false; hiddenBlocks.Add(b); }
        foreach (var b in buttons) { b.Visible = false; hiddenButtons.Add(b); }
        var accept = AcceptButton; var cancel = CancelButton;
        blocks.Clear(); buttons.Clear();
        head = title; sub = null;
        if (yes == null) { glyph = "\uE783"; glyphColor = T.Red; }
        void Back()
        {
            foreach (var b in blocks) b.C.Dispose();
            foreach (var b in buttons) b.Dispose();
            blocks.Clear(); buttons.Clear();
            foreach (var b in hiddenBlocks) { b.C.Visible = true; blocks.Add(b); }
            foreach (var b in hiddenButtons) { b.Visible = true; buttons.Add(b); }
            hiddenBlocks.Clear(); hiddenButtons.Clear();
            head = hiddenHead; sub = hiddenSub; glyph = null;
            AcceptButton = accept; CancelButton = cancel;
            Done();
        }
        AddText(text, gap: 0);
        if (yes == null) AcceptButton = CancelButton = AddButton("OK", Back, primary: true);
        else
        {
            AcceptButton = AddButton(yes, () => { Back(); go(); }, primary: true);
            CancelButton = AddButton(L.T("setup_cancel"), Back);
        }
        Done();
    }

    // ------------------------------------------------------------ questions
    bool? answer;

    /// <summary>A two-button question in this style (instead of a plain task dialog): true / false, null if closed.</summary>
    public static bool? Ask(Settings s, string text, string yes, string no)
    {
        using var f = new SetupForm(s, "TaskHub");
        f.Page("TaskHub", null);
        f.AddText(text, gap: 0);
        f.AcceptButton = f.AddButton(yes, () => { f.answer = true; f.Close(); }, primary: true);
        f.AddButton(no, () => { f.answer = false; f.Close(); });
        f.ShowDialog();
        return f.answer;
    }

    // ------------------------------------------------------------ progress / done / error
    TextBlock stepText;
    Fluent.Progress bar;

    /// <summary>Progress page: a step line and a bar. The window can't be closed until the job ends.</summary>
    public void ShowProgress(string title)
    {
        Page(title, "TaskHub " + Settings.Version);
        stepText = AddText(" ", gap: 8);   // one line high from the start
        bar = new Fluent.Progress(T);
        blocks.Add(new Block { C = bar, Gap = 12, Height = 8 });
        Controls.Add(bar);
        busy = true;
        Done();
    }

    public void Step(string text, double? value)
    {
        if (InvokeRequired) { BeginInvoke(() => Step(text, value)); return; }
        stepText.Text = text;
        bar.Value = value;
    }

    public void ShowDone(string title, string text, IEnumerable<string> notes, string openLabel, Action open)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowDone(title, text, notes, openLabel, open)); return; }
        busy = false;
        Page(title, "TaskHub " + Settings.Version, "\uE930", T.Acc);   // Completed (a check in a circle)
        AddText(text, gap: 0);
        var n = notes?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (n is { Count: > 0 }) AddText(string.Join("\n", n), gap: 12, muted: true);
        if (open != null) AcceptButton = AddButton(openLabel, () => { open(); Close(); }, primary: true);
        var bc = AddButton(L.T("setup_close"), Close, primary: open == null);
        CancelButton = bc;
        if (open == null) AcceptButton = bc;
        Done();
    }

    public void ShowFailed(string title, string text)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowFailed(title, text)); return; }
        busy = false;
        Page(title, "TaskHub " + Settings.Version, "\uE783", T.Red);   // Error (an exclamation in a circle)
        AddText(text, gap: 0);
        AcceptButton = CancelButton = AddButton(L.T("setup_close"), Close, primary: true);
        Done();
    }

    // ------------------------------------------------------------ uninstall: confirm
    public bool Purge => tPurge?.Checked ?? false;
    Fluent.Toggle tPurge;

    /// <summary>"Uninstall TaskHub?" with the "also delete my data" switch; calls go() on Uninstall.</summary>
    public void ShowUninstall(Action go)
    {
        Page(L.T("uninstall_title"), "TaskHub " + (Install.InstalledVersion?.ToString(3) ?? Settings.Version));
        AddText(L.T("uninstall_q"), gap: 0);
        tPurge = AddToggle(L.T("uninstall_purge"), false, 1, L.T("uninstall_purge_desc", S.DataDir), gap: 16);
        AcceptButton = AddButton(L.T("uninstall_btn"), go, primary: true);
        CancelButton = AddButton(L.T("setup_cancel"), Close);
        Done();
    }
}
