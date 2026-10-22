// C3D Print Bridge — tray Configure window (v1.1.0, owner-approved).
//
// Plain form fields instead of hand-editing JSON: a printer list
// with Add / Edit / Delete, a Scan button that runs the existing
// subnet probe and fills in IP + type for each candidate found,
// and masked inputs for the Bambu LAN access code and the
// dashboard API key. Access codes and keys are never printed back
// and are stored only in the local files on this PC (config for
// printer codes, the restricted key file for the dashboard key).
// Save writes bridge-config.json (never the key file, unless a
// new key was typed) and the tray app restarts the relay loop so
// the new config takes effect. Cancel discards everything.
// Deleting a printer asks for confirmation first.
//
// Honest limits, shown in the form: Scan only sees printers on
// this PC's own subnet, and a reachable port is a candidate, not
// a confirmed printer. Bambu serial + access code still come from
// the printer's own screen; the scan cannot discover them.

using System.Windows.Forms;

namespace C3DBridge;

public sealed class ConfigureForm : Form
{
    private readonly BridgeConfig _initial;
    private readonly List<PrinterConfig> _printers;
    private int _editingIndex = -1;

    private readonly ListView _list = new();
    private readonly Panel _rightPanel = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _type = new();
    private readonly TextBox _ip = new();
    private readonly TextBox _serial = new();
    private readonly TextBox _access = new();
    private readonly CheckBox _showAccess = new();
    private readonly CheckBox _camera = new();
    private readonly TextBox _key = new();
    private readonly TextBox _paxxUser = new();
    private readonly TextBox _paxxPass = new();
    private readonly TextBox _target = new();
    private readonly Label _portHint = new();
    private readonly Label _macLabel = new();
    private readonly Label _status = new();

    public ConfigureForm(BridgeConfig initial, bool hasKey)
    {
        _initial = initial;
        _printers = initial.Printers.ToList();

        Text = "C3D Print Bridge — Configure";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new System.Drawing.Font("Segoe UI", 9);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        // Fit on screen: never taller/wider than the working area, so the
        // title bar and corners are always grabbable. Minimum is low so
        // the form can be resized down, and the right panel scrolls.
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 960, 720);
        Width = Math.Min(960, wa.Width - 20);
        Height = Math.Min(720, wa.Height - 20);
        MinimumSize = new Size(640, 480);
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);

        var accent = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = System.Drawing.Color.FromArgb(0, 150, 136) };
        Controls.Add(accent);
        // Left panel: printers list
        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 480, Padding = new Padding(12) };
        var listLabel = new Label { Text = "Printers", Left = 0, Top = 0, Width = 440, Height = 20 };
        leftPanel.Controls.Add(listLabel);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.MultiSelect = false;
        _list.Columns.Add("Name", 140);
        _list.Columns.Add("Type", 90);
        _list.Columns.Add("IP address", 130);
        _list.Columns.Add("Status", 110);
        _list.Left = 0; _list.Top = 24; _list.Width = 456; _list.Height = 320;
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right;
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _printers.Count)
            {
                _editingIndex = _list.SelectedIndices[0];
                LoadFields(_printers[_editingIndex]);
            }
        };
        leftPanel.Controls.Add(_list);
        var scanBtn = new Button { Text = "Scan WiFi for printers", Left = 0, Top = 354, Width = 220, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        scanBtn.Click += async (_, _) => await ScanAsync(scanBtn);
        leftPanel.Controls.Add(scanBtn);
        var deleteBtn = new Button { Text = "Delete selected", Left = 230, Top = 354, Width = 220, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        deleteBtn.Click += (_, _) => DeleteSelected();
        leftPanel.Controls.Add(deleteBtn);
        _status.Left = 0; _status.Top = 394; _status.Width = 456; _status.Height = 100; _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _status.Text = "Scan only sees printers on this PC's own network. A found port is a candidate, not a confirmed printer.";
        leftPanel.Controls.Add(_status);
        Controls.Add(leftPanel);

        // Bottom panel: Save/Cancel always visible, never scrolled off
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12) };
        var saveHint = new Label { Text = "Save writes everything to disk.", Left = 0, Top = 16, Width = 300 };
        bottomPanel.Controls.Add(saveHint);
        var saveBtn = new Button { Text = "Save", Width = 100, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = System.Drawing.Color.FromArgb(0, 120, 215), ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
        saveBtn.Left = bottomPanel.Width - 230; saveBtn.Top = 12;
        saveBtn.Click += (_, _) => SaveAndClose();
        bottomPanel.Controls.Add(saveBtn);
        var cancelBtn = new Button { Text = "Cancel", Width = 100, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        cancelBtn.Left = bottomPanel.Width - 120; cancelBtn.Top = 12;
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottomPanel.Controls.Add(cancelBtn);
        // Keep Save/Cancel at the right edge when the form resizes
        bottomPanel.Resize += (_, _) => { saveBtn.Left = bottomPanel.Width - 230; cancelBtn.Left = bottomPanel.Width - 120; };
        Controls.Add(bottomPanel);

        // Right panel: scrollable fields, so a short form still reaches every field
        _rightPanel.Dock = DockStyle.Fill;
        _rightPanel.AutoScroll = true;
        _rightPanel.Padding = new Padding(12);
        Controls.Add(_rightPanel);
        // Bring left/bottom panels to front in dock order
        leftPanel.BringToFront(); bottomPanel.BringToFront();

        var x = 12;
        AddField("Name", _name, x, 36);
        var typeLabel = new Label { Text = "Type", Left = x, Top = 76, Width = 400 };
        _type.Left = x; _type.Top = 96; _type.Width = 400; _type.DropDownStyle = ComboBoxStyle.DropDownList; _type.DropDownHeight = 80; _type.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _type.Items.Add("bambu"); _type.Items.Add("snapmaker"); _type.SelectedIndex = 0;
        _rightPanel.Controls.Add(typeLabel); _rightPanel.Controls.Add(_type);
        AddField("IP address", _ip, x, 158);
        _macLabel.Left = x; _macLabel.Top = 184; _macLabel.Width = 400; _macLabel.Height = 16; _macLabel.ForeColor = System.Drawing.Color.Gray; _macLabel.Text = "MAC: not known yet";
        _rightPanel.Controls.Add(_macLabel);
        AddField("Serial (Bambu, from printer screen)", _serial, x, 220);
        AddField("Access code (Bambu / U1 Moonraker key, from printer screen or your saved key)", _access, x, 282);
        _access.UseSystemPasswordChar = true;
        AddField("Paxx username (Snapmaker U1, from Paxx API settings)", _paxxUser, x, 348);
        AddField("Paxx password (Snapmaker U1, from Paxx API settings)", _paxxPass, x, 414);
        _paxxPass.UseSystemPasswordChar = true;
        _showAccess.Text = "Show access / Paxx passwords"; _showAccess.Left = x; _showAccess.Top = 450; _showAccess.Width = 300;
        _showAccess.CheckedChanged += (_, _) => { _access.UseSystemPasswordChar = !_showAccess.Checked; _paxxPass.UseSystemPasswordChar = !_showAccess.Checked; };
        _rightPanel.Controls.Add(_showAccess);
        _camera.Text = "Has camera"; _camera.Left = x; _camera.Top = 476; _camera.Width = 200; _camera.Checked = true;
        _rightPanel.Controls.Add(_camera);
        var btnHint = new Label { Text = "Add new clears these fields for a new printer. Apply to list saves these fields into the printers list on the left. Neither saves to disk until you click Save below.", Left = x, Top = 504, Width = 400, Height = 42 };
        _rightPanel.Controls.Add(btnHint);
        var addBtn = new Button { Text = "Add new", Left = x, Top = 552, Width = 140, Height = 32 };
        addBtn.Click += (_, _) => { _editingIndex = -1; _list.SelectedItems.Clear(); ClearFields(); _name.Focus(); };
        _rightPanel.Controls.Add(addBtn);
        var applyBtn = new Button { Text = "Apply to list", Left = x + 150, Top = 552, Width = 140, Height = 32 };
        applyBtn.Click += (_, _) => ApplyFields();
        _rightPanel.Controls.Add(applyBtn);
        AddField("Dashboard API key (leave blank to keep current)", _key, x, 618);
        _key.UseSystemPasswordChar = true;
        var keyHint = new Label
        {
            Text = hasKey ? "A key is already stored. Only type a new one to replace it."
                : "No key stored yet. Paste the key from the dashboard Bridge key page.",
            Left = x, Top = 644, Width = 400, Height = 30,
        };
        _rightPanel.Controls.Add(keyHint);
        AddField("Dashboard host URL - where the bridge sends status", _target, x, 700);
        _target.Text = _initial.DashboardBaseUrl;
        _target.TextChanged += (_, _) => UpdatePortHint();
        _portHint.Left = x; _portHint.Top = 726; _portHint.Width = 400; _portHint.Height = 28; _portHint.ForeColor = System.Drawing.Color.Gray;
        _rightPanel.Controls.Add(_portHint);
        UpdatePortHint();

        RefreshList();
        if (_printers.Count > 0) { _list.Items[0].Selected = true; }
    }

    private void UpdatePortHint()
    {
        var url = _target.Text.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsDefaultPort)
            _portHint.Text = $"Port: {uri.Port} (from the URL above) - this is where the bridge sends status.";
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            _portHint.Text = "Port: 443 (https default) - this is where the bridge sends status. Change the URL above to point the bridge elsewhere.";
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            _portHint.Text = "Port: 80 (http default) - this is where the bridge sends status. Change the URL above to point the bridge elsewhere.";
        else
            _portHint.Text = "Port: shown here once a valid http(s) URL is entered above.";
    }

    private void AddField(string label, TextBox box, int x, int top)
    {
        var l = new Label { Text = label, Left = x, Top = top - 20, Width = 400 };
        box.Left = x; box.Top = top; box.Width = 400; box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _rightPanel.Controls.Add(l); _rightPanel.Controls.Add(box);
    }

    private void RefreshList()
    {
        int selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        _list.Items.Clear();
        foreach (var p in _printers)
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? "(fill in a name)" : p.Name;
            var ready = p.Type == "bambu" ? (!string.IsNullOrWhiteSpace(p.Serial) && !string.IsNullOrWhiteSpace(p.AccessCode))
                : (!string.IsNullOrWhiteSpace(p.PaxxUser) && !string.IsNullOrWhiteSpace(p.PaxxPassword)) || !string.IsNullOrWhiteSpace(p.AccessCode);
            var item = new ListViewItem(name);
            item.SubItems.Add(p.Type);
            item.SubItems.Add(p.Ip);
            var statusItem = new ListViewItem.ListViewSubItem(item, ready ? "Ready" : "Needs setup");
            statusItem.ForeColor = ready ? System.Drawing.Color.FromArgb(0, 128, 0) : System.Drawing.Color.FromArgb(200, 110, 0);
            item.SubItems.Add(statusItem);
            _list.Items.Add(item);
        }
        if (selected >= 0 && selected < _list.Items.Count) _list.Items[selected].Selected = true;
    }

    private void LoadFields(PrinterConfig p)
    {
        _name.Text = p.Name;
        _type.SelectedItem = p.Type == "snapmaker" ? "snapmaker" : "bambu";
        _ip.Text = p.Ip;
        _serial.Text = p.Serial ?? "";
        _access.Text = p.AccessCode ?? "";
        _paxxUser.Text = p.PaxxUser ?? "";
        _paxxPass.Text = p.PaxxPassword ?? "";
        _camera.Checked = p.Camera;
        _macLabel.Text = string.IsNullOrWhiteSpace(p.Mac) ? "MAC: not known yet" : $"MAC: {p.Mac}";
    }

    private void ClearFields()
    {
        _name.Text = ""; _type.SelectedIndex = 0; _ip.Text = "";
        _serial.Text = ""; _access.Text = ""; _paxxUser.Text = ""; _paxxPass.Text = ""; _camera.Checked = true;
        _macLabel.Text = "MAC: not known yet";
    }

    private bool ApplyFields()
    {
        var name = _name.Text.Trim();
        var type = _type.SelectedItem?.ToString() ?? "bambu";
        var ip = _ip.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Give the printer a name first.", "C3D Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("Fill in the printer's IP address first.", "C3D Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        for (var i = 0; i < _printers.Count; i++)
        {
            if (i != _editingIndex && string.Equals(_printers[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show($"A printer named '{name}' is already in the list. Names must be unique.", "C3D Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }
        if (type == "bambu" && (string.IsNullOrWhiteSpace(_serial.Text) || string.IsNullOrWhiteSpace(_access.Text)))
        {
            MessageBox.Show("For a Bambu printer, fill in the serial and the access code from the printer's screen before applying.", "C3D Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var printer = new PrinterConfig
        {
            Name = name, Type = type, Ip = ip,
            Serial = string.IsNullOrWhiteSpace(_serial.Text) ? null : _serial.Text.Trim(),
            AccessCode = string.IsNullOrWhiteSpace(_access.Text) ? null : _access.Text.Trim(),
            PaxxUser = string.IsNullOrWhiteSpace(_paxxUser.Text) ? null : _paxxUser.Text.Trim(),
            PaxxPassword = string.IsNullOrWhiteSpace(_paxxPass.Text) ? null : _paxxPass.Text,
            Camera = _camera.Checked,
            SnapshotUrl = _editingIndex >= 0 ? _printers[_editingIndex].SnapshotUrl : null,
            Port = _editingIndex >= 0 ? _printers[_editingIndex].Port : null,
            Mac = _editingIndex >= 0 ? _printers[_editingIndex].Mac : null,
        };
        if (_editingIndex >= 0) _printers[_editingIndex] = printer;
        else { _printers.Add(printer); _editingIndex = _printers.Count - 1; }
        RefreshList();
        if (_editingIndex >= 0 && _editingIndex < _list.Items.Count) _list.Items[_editingIndex].Selected = true;
        return true;
    }

    private void DeleteSelected()
    {
        if (_list.SelectedIndices.Count == 0 || _list.SelectedIndices[0] >= _printers.Count) return;
        var p = _printers[_list.SelectedIndices[0]];
        var answer = MessageBox.Show($"Delete printer '{p.Name}' from the bridge config? This does not touch the printer itself.",
            "C3D Print Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        _printers.RemoveAt(_list.SelectedIndices[0]);
        _editingIndex = -1;
        ClearFields();
        RefreshList();
    }

    private async Task ScanAsync(Button scanBtn)
    {
        scanBtn.Enabled = false;
        _status.Text = "Scanning this PC's network…";
        try
        {
            var candidates = await Discovery.ProbeCandidatesAsync();
            if (candidates.Count == 0)
            {
                _status.Text = "No printer candidates found. Check the printers are on, on the same network, and in Developer/LAN mode (Bambu) or with Paxx installed (U1).";
                return;
            }
            var added = 0; var updated = 0;
            foreach (var c in candidates)
            {
                var mac = MacResolver.Resolve(c.Ip);
                if (!string.IsNullOrWhiteSpace(mac))
                {
                    var byMac = _printers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Mac) && string.Equals(p.Mac, mac, StringComparison.OrdinalIgnoreCase));
                    if (byMac is not null)
                    {
                        if (!string.Equals(byMac.Ip, c.Ip, StringComparison.OrdinalIgnoreCase))
                        {
                            var idx = _printers.IndexOf(byMac);
                            _printers[idx] = byMac with { Ip = c.Ip };
                            updated++;
                        }
                        continue;
                    }
                    var byIp = _printers.FirstOrDefault(p => string.Equals(p.Ip, c.Ip, StringComparison.OrdinalIgnoreCase));
                    if (byIp is not null)
                    {
                        var idx2 = _printers.IndexOf(byIp);
                        _printers[idx2] = byIp with { Mac = mac };
                        continue;
                    }
                }
                else
                {
                    if (_printers.Any(p => string.Equals(p.Ip, c.Ip, StringComparison.OrdinalIgnoreCase))) continue;
                }
                var printer = new PrinterConfig { Name = "", Type = c.Kind, Ip = c.Ip, Camera = true, Mac = mac };
                printer = printer with { Name = AutoName(printer) };
                _printers.Add(printer);
                added++;
            }
            RefreshList();
            _status.Text = added > 0 || updated > 0
                ? $"Found {candidates.Count} port-open device(s). {added} new printer(s) added with model names from their IP, {updated} known printer(s) moved to a new IP by MAC. Select a printer, fill in serial + access code for Bambu (or Moonraker key / Paxx login for U1), Apply to list, then Save. Delete any that are not printers."
                : $"Found {candidates.Count} candidate(s), all already in the list. A port being open is not proof it is a printer.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            scanBtn.Enabled = true;
        }
    }

    // Name a printer from its IP, e.g. U1-233 or Bambu-125.
    // Appends -2, -3... if that name is already in the list.
    private string AutoName(PrinterConfig printer)
    {
        var last = printer.Ip.Trim().Split('.').LastOrDefault() ?? "printer";
        var prefix = printer.Type == "snapmaker" ? "U1" : printer.Type == "bambu" ? "Bambu" : printer.Type;
        var name = $"{prefix}-{last}";
        var n = 2;
        while (_printers.Any(p => p != printer && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{prefix}-{last}-{n++}";
        return name;
    }

    private void SaveAndClose()
    {
        // Apply any unapplied field edits first, if the fields hold a printer.
        if (!string.IsNullOrWhiteSpace(_name.Text) || !string.IsNullOrWhiteSpace(_ip.Text))
        {
            if (!ApplyFields()) return;
        }
        // A scanned printer you never set up must not block saving the
        // ones you did: drop entries with no IP, and auto-name entries
        // that have an IP but no name, instead of throwing an error.
        _printers.RemoveAll(p => string.IsNullOrWhiteSpace(p.Ip));
        for (var i = 0; i < _printers.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(_printers[i].Name))
                _printers[i] = _printers[i] with { Name = AutoName(_printers[i]) };
        }
        RefreshList();
        var incomplete = _printers.Where(p => p.Type == "bambu" &&
            (string.IsNullOrWhiteSpace(p.Serial) || string.IsNullOrWhiteSpace(p.AccessCode))).ToList();
        if (incomplete.Count > 0)
        {
            var answer = MessageBox.Show(
                $"These Bambu printers are missing a serial or access code and will stay offline until those are filled in: {string.Join(", ", incomplete.Select(p => p.Name))}. Save anyway?",
                "C3D Print Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }
        var cfg = _initial with
        {
            Printers = _printers,
            DashboardBaseUrl = string.IsNullOrWhiteSpace(_target.Text) ? _initial.DashboardBaseUrl : _target.Text.Trim(),
        };
        Config.Save(cfg);
        if (!string.IsNullOrWhiteSpace(_key.Text))
            Config.StoreKey(_key.Text);
        DialogResult = DialogResult.OK;
        Close();
    }
}
