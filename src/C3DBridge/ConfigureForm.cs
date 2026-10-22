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

    private readonly ListBox _list = new();
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
        Width = 780;
        Height = 860;
        MinimumSize = new Size(720, 800);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;

        var listLabel = new Label { Text = "Printers", Left = 12, Top = 10, Width = 240 };
        _list.Left = 12; _list.Top = 36; _list.Width = 240; _list.Height = 320; _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _printers.Count)
            {
                _editingIndex = _list.SelectedIndex;
                LoadFields(_printers[_editingIndex]);
            }
        };
        Controls.Add(listLabel);
        Controls.Add(_list);

        var scanBtn = new Button { Text = "Scan WiFi for printers", Left = 12, Top = 366, Width = 240, Height = 32 };
        scanBtn.Click += async (_, _) => await ScanAsync(scanBtn);
        Controls.Add(scanBtn);

        var deleteBtn = new Button { Text = "Delete selected", Left = 12, Top = 406, Width = 240, Height = 32 };
        deleteBtn.Click += (_, _) => DeleteSelected();
        Controls.Add(deleteBtn);

        _status.Left = 12; _status.Top = 448; _status.Width = 240; _status.Height = 120;
        _status.Text = "Scan only sees printers on this PC's own network. A found port is a candidate, not a confirmed printer.";
        Controls.Add(_status);

        var x = 272;
        AddField("Name", _name, x, 36);
        var typeLabel = new Label { Text = "Type", Left = x, Top = 76, Width = 440 };
        _type.Left = x; _type.Top = 96; _type.Width = 440; _type.DropDownStyle = ComboBoxStyle.DropDownList; _type.DropDownHeight = 80; _type.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _type.Items.Add("bambu"); _type.Items.Add("snapmaker"); _type.SelectedIndex = 0;
        Controls.Add(typeLabel); Controls.Add(_type);
        AddField("IP address", _ip, x, 158);
        _macLabel.Left = x; _macLabel.Top = 184; _macLabel.Width = 440; _macLabel.Height = 16; _macLabel.ForeColor = System.Drawing.Color.Gray; _macLabel.Text = "MAC: not known yet";
        Controls.Add(_macLabel);
        AddField("Serial (Bambu, from printer screen)", _serial, x, 220);
        AddField("Access code (Bambu / U1 Moonraker key, from printer screen or your saved key)", _access, x, 282);
        AddField("Paxx username (Snapmaker U1, from Paxx API settings)", _paxxUser, x, 348);
        AddField("Paxx password (Snapmaker U1, from Paxx API settings)", _paxxPass, x, 414);
        _paxxPass.UseSystemPasswordChar = true;
        _access.UseSystemPasswordChar = true;
        _showAccess.Text = "Show access / Paxx passwords"; _showAccess.Left = x; _showAccess.Top = 450; _showAccess.Width = 200;
        _showAccess.CheckedChanged += (_, _) => { _access.UseSystemPasswordChar = !_showAccess.Checked; _paxxPass.UseSystemPasswordChar = !_showAccess.Checked; };
        Controls.Add(_showAccess);
        _camera.Text = "Has camera"; _camera.Left = x; _camera.Top = 476; _camera.Width = 200; _camera.Checked = true;
        Controls.Add(_camera);

        var btnHint = new Label { Text = "Add new clears these fields for a new printer. Apply to list saves these fields into the printers list on the left. Neither saves to disk until you click Save below.", Left = x, Top = 504, Width = 440, Height = 28 };
        Controls.Add(btnHint);
        var addBtn = new Button { Text = "Add new", Left = x, Top = 536, Width = 140, Height = 32 };
        addBtn.Click += (_, _) => { _editingIndex = -1; _list.ClearSelected(); ClearFields(); _name.Focus(); };
        Controls.Add(addBtn);
        var applyBtn = new Button { Text = "Apply to list", Left = x + 150, Top = 536, Width = 140, Height = 32 };
        applyBtn.Click += (_, _) => ApplyFields();
        Controls.Add(applyBtn);

        AddField("Dashboard API key (leave blank to keep current)", _key, x, 602);
        _key.UseSystemPasswordChar = true;
        var keyHint = new Label
        {
            Text = hasKey ? "A key is already stored. Only type a new one to replace it."
                : "No key stored yet. Paste the key from the dashboard Bridge key page.",
            Left = x, Top = 628, Width = 440, Height = 30,
        };
        Controls.Add(keyHint);

        AddField("Dashboard host URL - where the bridge sends status", _target, x, 684);
        _target.Text = _initial.DashboardBaseUrl;
        _target.TextChanged += (_, _) => UpdatePortHint();
        _portHint.Left = x; _portHint.Top = 710; _portHint.Width = 440; _portHint.Height = 28; _portHint.ForeColor = System.Drawing.Color.Gray;
        Controls.Add(_portHint);
        UpdatePortHint();
        var saveBtn = new Button { Text = "Save", Left = 460, Top = 750, Width = 100, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        saveBtn.Click += (_, _) => SaveAndClose();
        Controls.Add(saveBtn);
        var cancelBtn = new Button { Text = "Cancel", Left = 570, Top = 750, Width = 100, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelBtn);

        RefreshList();
        if (_printers.Count > 0) { _list.SelectedIndex = 0; }
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
        var l = new Label { Text = label, Left = x, Top = top - 20, Width = 440 };
        box.Left = x; box.Top = top; box.Width = 440; box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(l); Controls.Add(box);
    }

    private void RefreshList()
    {
        var selected = _list.SelectedIndex;
        _list.Items.Clear();
        foreach (var p in _printers)
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? "(fill in a name)" : p.Name;
            _list.Items.Add($"{name} ({p.Type}) — {p.Ip}");
        }
        if (selected >= 0 && selected < _list.Items.Count) _list.SelectedIndex = selected;
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
        _serial.Text = ""; _access.Text = ""; _camera.Checked = true;
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
        _list.SelectedIndex = _editingIndex;
        return true;
    }

    private void DeleteSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _printers.Count) return;
        var p = _printers[_list.SelectedIndex];
        var answer = MessageBox.Show($"Delete printer '{p.Name}' from the bridge config? This does not touch the printer itself.",
            "C3D Print Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        _printers.RemoveAt(_list.SelectedIndex);
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
                var printer = new PrinterConfig { Name = $"Candidate {c.Ip}", Type = c.Kind, Ip = c.Ip, Camera = true, Mac = mac };
                _printers.Add(printer);
                added++;
            }
            RefreshList();
            _status.Text = added > 0 || updated > 0
                ? $"Found {candidates.Count} port-open device(s). These are candidates, not confirmed printers. {added} new candidate(s) added, {updated} known printer(s) moved to a new IP by MAC. Select a candidate, fill in serial + access code for Bambu (or Moonraker key for U1), Apply to list, then Save. Delete any that are not printers."
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
