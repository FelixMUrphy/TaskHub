namespace TaskHub;

/// <summary>Setup dialog: install folder (with Browse), autostart, PATH; Install / Run without installing / Cancel.</summary>
sealed class InstallForm : Form
{
    public enum Choice { Cancel, Install, Run }
    public Choice Result = Choice.Cancel;
    public string Folder => box.Text.Trim();
    public bool Autostart => cbAuto.Checked;
    public bool AddPath => cbPath.Checked;
    public IEnumerable<(Integrations.Agent agent, Integrations.Kind kind, bool on)> Agents => agentBoxes.Select(x => (x.a, x.k, x.cb.Checked));

    readonly TextBox box;
    readonly CheckBox cbAuto, cbPath;
    readonly List<(Integrations.Agent a, Integrations.Kind k, CheckBox cb)> agentBoxes = new();

    /// <summary>Default for an agent checkbox: the earlier choice on update, else on if the agent is used on this PC.</summary>
    static bool AgentDefault(Integrations.Agent a, Integrations.Kind k)
    {
        try
        {
            if (L.S?.Get(Integrations.CfgKey(a, k)) is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var on)) return on;
            return Integrations.Enabled(a, k) || Integrations.Detected(a);
        }
        catch { return false; }
    }

    public InstallForm(bool update)
    {
        Text = "TaskHub " + Settings.Version + " — " + L.T(update ? "setup_update" : "setup_title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(16), Dock = DockStyle.Fill };
        root.Controls.Add(new Label
        {
            Text = L.T(update ? "setup_intro_update" : "setup_intro"), AutoSize = true, MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 12),
        });
        root.Controls.Add(new Label { Text = L.T("setup_folder"), AutoSize = true, Margin = new Padding(0, 0, 0, 4) });

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        box = new TextBox { Width = 440, Text = Install.IsInstalled ? Install.InstallDir : Install.DefaultDir };
        var browse = new Button { Text = "…", Width = 40, Height = box.Height + 2 };
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
            box.Text = p;
        };
        row.Controls.Add(box);
        row.Controls.Add(browse);
        root.Controls.Add(row);

        cbAuto = new CheckBox { Text = L.T("setup_autostart"), Checked = true, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        cbPath = new CheckBox { Text = L.T("setup_path"), Checked = true, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        root.Controls.Add(cbAuto);
        root.Controls.Add(cbPath);
        foreach (var a in Integrations.All)
        {
            root.Controls.Add(new Label { Text = Integrations.Name(a) + ":", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 2) });
            foreach (var k in Integrations.Kinds)
            {
                var cb = new CheckBox
                {
                    Text = L.T(k == Integrations.Kind.Hooks ? "setup_agent" : "setup_mcp", Integrations.Name(a)),
                    Checked = AgentDefault(a, k), AutoSize = true, Margin = new Padding(12, 2, 0, 0),
                };
                agentBoxes.Add((a, k, cb));
                root.Controls.Add(cb);
            }
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        var bCancel = new Button { Text = L.T("setup_cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var bRun = new Button { Text = L.T("setup_run"), AutoSize = true };
        var bInstall = new Button { Text = L.T(update ? "setup_update_btn" : "setup_install"), AutoSize = true };
        bRun.Click += (_, _) => { Result = Choice.Run; Close(); };
        bInstall.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Folder) || !Path.IsPathRooted(Folder))
            {
                MessageBox.Show(this, L.T("setup_bad_folder"), "TaskHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var err = Install.CheckWritable(Folder);
            if (err != null)
            {
                MessageBox.Show(this, L.T("setup_no_access", Folder) + "\n\n" + err, "TaskHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Result = Choice.Install;
            Close();
        };
        buttons.Controls.Add(bCancel);
        buttons.Controls.Add(bRun);
        buttons.Controls.Add(bInstall);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = bInstall;
        CancelButton = bCancel;
    }
}
