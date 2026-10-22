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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace C3DBridge;

public sealed class ConfigureForm : Form
{
    private readonly BridgeConfig _initial;
    private readonly List<PrinterConfig> _printers;
    private int _editingIndex = -1;

    private readonly ListView _list = new();
    private readonly Panel _rightPanel = new();
    private const string LogoPngBase64 = "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";
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
        _list.MouseDoubleClick += (_, e) => {
            var hit = _list.HitTest(e.Location);
            if (hit.Item != null && hit.Item.Index < _printers.Count) {
                var ip = _printers[hit.Item.Index].Ip;
                if (!string.IsNullOrWhiteSpace(ip)) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://" + ip.Trim()) { UseShellExecute = true }); } catch { } }
            }
        };
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
        var openBtn = new Button { Text = "Open IP in browser", Left = 0, Top = 394, Width = 220, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        var pullBtn = new Button { Text = "Pull from dashboard", Left = 230, Top = 394, Width = 220, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        pullBtn.Click += async (_, _) => await PullFromDashboardAsync(pullBtn);
        leftPanel.Controls.Add(pullBtn);
        openBtn.Click += (_, _) => { if (_list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _printers.Count) { var ip = _printers[_list.SelectedIndices[0]].Ip; if (!string.IsNullOrWhiteSpace(ip)) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://" + ip.Trim()) { UseShellExecute = true }); } catch { } } } };
        leftPanel.Controls.Add(openBtn);
        var deleteBtn = new Button { Text = "Delete selected", Left = 230, Top = 354, Width = 220, Height = 32, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        deleteBtn.Click += (_, _) => DeleteSelected();
        leftPanel.Controls.Add(deleteBtn);
        _status.Left = 0; _status.Top = 434; _status.Width = 456; _status.Height = 60; _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _status.Text = "Scan only sees printers on this PC's own network. A found port is a candidate, not a confirmed printer.";
        leftPanel.Controls.Add(_status);
        Controls.Add(leftPanel);

        // Bottom panel: Save/Cancel always visible, never scrolled off
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12) };
        try {
            var logoBytes = Convert.FromBase64String(LogoPngBase64);
            using var logoStream = new System.IO.MemoryStream(logoBytes);
            var logoImg = System.Drawing.Image.FromStream(logoStream);
            var logoBox = new PictureBox { Image = logoImg, Left = 0, Top = 8, Width = 40, Height = 40, SizeMode = PictureBoxSizeMode.StretchImage, Cursor = Cursors.Hand };
            logoBox.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://creat3dworld.com") { UseShellExecute = true }); } catch { } };
            bottomPanel.Controls.Add(logoBox);
        } catch { }
        var saveHint = new Label { Text = "Save writes everything to disk. Click the logo to visit creat3dworld.com.", Left = 48, Top = 16, Width = 300 };
        bottomPanel.Controls.Add(saveHint);
        var saveBtn = new Button { Text = "Save", Width = 100, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = System.Drawing.Color.FromArgb(0, 120, 215), ForeColor = System.Drawing.Color.White, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
        saveBtn.Left = Width - 230; saveBtn.Top = 12;
        saveBtn.Click += (_, _) => SaveAndClose();
        bottomPanel.Controls.Add(saveBtn);
        var cancelBtn = new Button { Text = "Cancel", Width = 100, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        cancelBtn.Left = Width - 120; cancelBtn.Top = 12;
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottomPanel.Controls.Add(cancelBtn);
        // Keep Save/Cancel at the right edge when the form resizes
        bottomPanel.Resize += (_, _) => { saveBtn.Left = bottomPanel.Width - 230; cancelBtn.Left = bottomPanel.Width - 120; };
        Controls.Add(bottomPanel);

        // Right panel: explicit bounds, NOT Dock Fill. Dock Fill + BringToFront
        // put this panel's fields (at x=12) underneath the 480px left panel,
        // which is why the right side looked gray with no controls.
        _rightPanel.Left = 492; _rightPanel.Top = 6;
        _rightPanel.Width = Width - 504; _rightPanel.Height = Height - 62;
        _rightPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _rightPanel.AutoScroll = true;
        _rightPanel.Padding = new Padding(12);
        Controls.Add(_rightPanel);

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

        // Migrate old "Candidate-..." names: name becomes the IP itself.
        // Type + IP columns already identify the printer, so no invented name.
        for (var i = 0; i < _printers.Count; i++) {
            if ((string.IsNullOrWhiteSpace(_printers[i].Name) || _printers[i].Name.StartsWith("Candidate", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(_printers[i].Ip))
                _printers[i] = _printers[i] with { Name = _printers[i].Ip.Trim() };
        }
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
            var name = string.IsNullOrWhiteSpace(p.Name) ? p.Ip : p.Name;
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
                var printer = new PrinterConfig { Name = c.Ip, Type = c.Kind, Ip = c.Ip, Camera = true, Mac = mac };
                _printers.Add(printer);
                added++;
            }
            RefreshList();
            _status.Text = added > 0 || updated > 0
                ? $"Found {candidates.Count} port-open device(s). {added} new printer(s) added, named by their IP, {updated} known printer(s) moved to a new IP by MAC. Select a printer, fill in serial + access code for Bambu (or Moonraker key / Paxx login for U1), Apply to list, then Save. Delete any that are not printers."
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


    private async Task PullFromDashboardAsync(Button btn)
    {
        var key = Config.ReadKey();
        if (string.IsNullOrWhiteSpace(key)) { MessageBox.Show("No dashboard key stored yet. Paste the dashboard key, Save, then Pull.", "C3D Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var host = string.IsNullOrWhiteSpace(_target.Text) ? _initial.DashboardBaseUrl : _target.Text.Trim();
        btn.Enabled = false; _status.Text = "Pulling device config from dashboard…";
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var res = await http.GetAsync(host.TrimEnd('/') + "/wp-json/c3d-shipping/v1/bridge/device-config");
            if (!res.IsSuccessStatusCode) { _status.Text = "Pull failed: dashboard returned " + (int)res.StatusCode + ". Nothing changed locally."; return; }
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("printers", out var arr) || arr.ValueKind != JsonValueKind.Array) { _status.Text = "No device config stored on the dashboard yet."; return; }
            var added = 0; var updated = 0;
            foreach (var el in arr.EnumerateArray()) {
                string Get(string n) => el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var ip = Get("ip").Trim(); if (string.IsNullOrWhiteSpace(ip)) continue;
                var pr = new PrinterConfig { Name = string.IsNullOrWhiteSpace(Get("name")) ? ip : Get("name").Trim(), Type = Get("type").Trim().ToLowerInvariant() == "snapmaker" ? "snapmaker" : "bambu", Ip = ip, Serial = string.IsNullOrWhiteSpace(Get("serial")) ? null : Get("serial").Trim(), AccessCode = string.IsNullOrWhiteSpace(Get("accessCode")) ? null : Get("accessCode").Trim(), PaxxUser = string.IsNullOrWhiteSpace(Get("paxxUser")) ? null : Get("paxxUser").Trim(), PaxxPassword = string.IsNullOrWhiteSpace(Get("paxxPassword")) ? null : Get("paxxPassword"), Camera = true, Mac = string.IsNullOrWhiteSpace(Get("mac")) ? null : Get("mac").Trim() };
                if (el.TryGetProperty("camera", out var cam) && cam.ValueKind == JsonValueKind.False) pr = pr with { Camera = false };
                var existing = _printers.FindIndex(x => string.Equals(x.Ip, ip, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) { _printers[existing] = pr; updated++; } else { _printers.Add(pr); added++; }
            }
            RefreshList();
            _status.Text = $"Pulled from dashboard: {added} new, {updated} updated. Click Save to write this to disk.";
        } catch (Exception ex) { _status.Text = "Pull failed: " + ex.Message + ". Nothing changed locally."; }
        finally { btn.Enabled = true; }
    }

    private void PushToDashboard()
    {
        try {
            var key = Config.ReadKey();
            if (string.IsNullOrWhiteSpace(key)) return;
            var host = string.IsNullOrWhiteSpace(_target.Text) ? _initial.DashboardBaseUrl : _target.Text.Trim();
            if (!host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            var list = new List<Dictionary<string, object?>>();
            foreach (var pr in _printers) {
                if (string.IsNullOrWhiteSpace(pr.Ip)) continue;
                list.Add(new Dictionary<string, object?> { ["name"] = pr.Name ?? pr.Ip, ["type"] = pr.Type, ["ip"] = pr.Ip, ["serial"] = pr.Serial ?? "", ["accessCode"] = pr.AccessCode ?? "", ["paxxUser"] = pr.PaxxUser ?? "", ["paxxPassword"] = pr.PaxxPassword ?? "", ["camera"] = pr.Camera, ["mac"] = pr.Mac ?? "" });
            }
            var payload = JsonSerializer.Serialize(new { bridge = "shop-pc", printers = list });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var res = http.PostAsync(host.TrimEnd('/') + "/wp-json/c3d-shipping/v1/bridge/device-config", new StringContent(payload, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Program.Log(res.IsSuccessStatusCode ? "Device config pushed to dashboard (encrypted there)." : "Device config push to dashboard failed: " + (int)res.StatusCode + " (local save still succeeded).");
        } catch (Exception ex) { Program.Log("Device config push to dashboard failed (local save still succeeded): " + ex.Message); }
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
            if (string.IsNullOrWhiteSpace(_printers[i].Name) && !string.IsNullOrWhiteSpace(_printers[i].Ip))
                _printers[i] = _printers[i] with { Name = _printers[i].Ip.Trim() };
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
        PushToDashboard();
        DialogResult = DialogResult.OK;
        Close();
    }
}
