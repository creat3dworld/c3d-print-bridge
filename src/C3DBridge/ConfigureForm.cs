// C3D Print Bridge — tray Configure window (compact, flyout style).
//
// Main window is list-only, so it stays small: printers table,
// Scan / Delete / Open IP / Pull buttons in a docked button area
// (two rows, equal columns, so no button is ever cut in half or
// pushed off-screen), plus Bridge settings and Configure buttons.
// There is NO Save / Cancel on the main screen: every change
// writes to disk immediately. The logo sits top-left.
//
// Click a printer's "Needs Setup" status, double-click a row, or
// click Configure selected, and a flyout panel slides in on the
// right with that printer's fields. Close the flyout and the
// window shrinks back — no always-on right pane eating screen.
//
// Login fields are generic, not brand-named boxes. The boxes
// shown and their labels adjust by brand:
//   Bambu: Device ID + Access code shown, Username hidden.
//   Snapmaker: Username + Paxx password shown, Device ID hidden.
//   Other: Device ID + Username + Password / API key shown.
// Type is an editable dropdown (bambu / snapmaker / other) —
// any other brand/firmware can be typed in, and its config
// is saved with the generic adapter, never rejected.

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
    private PrinterConfig? _loadedPrinter;
    private string? _prevBrand;
    private bool _suppressBrandSync;
    private bool _flyoutPrinterMode = true;

    private readonly ListView _list = new();
    private readonly Panel _flyout = new();
    private readonly Label _flyTitle = new();
    private readonly Button _flyClose = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _type = new();
    private readonly TextBox _ip = new();
    private readonly TextBox _deviceId = new();
    private readonly TextBox _loginUser = new();
    private readonly TextBox _loginPass = new();
    private readonly Button _eyeBtn = new();
    private readonly TextBox _apiKeyBox = new();
    private readonly TextBox _key = new();
    private readonly TextBox _target = new();
    private readonly Label _portHint = new();
    private readonly Label _status = new();
    private Label _deviceIdLabel = new();
    private Label _loginUserLabel = new();
    private Label _loginPassLabel = new();
    private Label _nameLabel = new();
    private Label _typeLabel = new();
    private Label _ipLabel = new();
    private Label _apiKeyLabel = new();
    private Label _keyLabel = new();
    private Label _targetLabel = new();
    private readonly FlowLayoutPanel _flyFlow = new();
    private readonly Panel _namePanel = new();
    private readonly Panel _brandIpPanel = new();
    private readonly Panel _deviceIdPanel = new();
    private readonly Panel _loginUserPanel = new();
    private readonly Panel _loginPassPanel = new();
    private readonly Panel _apiKeyPanel = new();
    private readonly Panel _keyPanel = new();
    private readonly Panel _targetPanel = new();
    private readonly Panel _portHintPanel = new();
    private static string Capitalize(string s) => string.IsNullOrWhiteSpace(s) ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    public ConfigureForm(BridgeConfig initial, bool hasKey)
    {
        _initial = initial;
        _printers = initial.Printers.ToList();
        for (var i = 0; i < _printers.Count; i++) {
            if ((string.IsNullOrWhiteSpace(_printers[i].Name) || _printers[i].Name.StartsWith("Candidate", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(_printers[i].Ip))
                _printers[i] = _printers[i] with { Name = _printers[i].Ip.Trim() };
        }

        Text = "C3D Print Bridge — Configure";
        DoubleBuffered = true;
        // Closing the window is NEVER blocked by a flyout
        // validation: CloseFlyout always hides the flyout (invalid
        // changes are discarded), and this handler forces the
        // flyout closed rather than cancelling. A validation
        // error must never force a Task Kill to close the app.
        FormClosing += (_, e) => { if (_flyout.Visible) { CloseFlyout(); _flyout.Visible = false; } DialogResult = DialogResult.OK; };
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new System.Drawing.Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 840, 560);
        // Size as a proportion of whatever display this mini-PC is on
        // right now (laptop or TV) — never hard-coded to one screen's Zoom.
        Width = Math.Max(600, Math.Min(900, wa.Width * 55 / 100));
        Height = Math.Max(500, Math.Min(750, wa.Height * 75 / 100));
        MinimumSize = new Size(560, 460);
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);

        // Top bar: logo top-left (clickable to creat3dworld.com),
        // title, and the auto-save note. No Save / Cancel here.
        var topBar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = System.Drawing.Color.White };
        var accentBar = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = System.Drawing.Color.FromArgb(0, 150, 136) };
        topBar.Controls.Add(accentBar);
        try {
            var logoBytes = Convert.FromBase64String(LogoPngBase64);
            using var logoStream = new System.IO.MemoryStream(logoBytes);
            var logoImg = System.Drawing.Image.FromStream(logoStream);
            var logoBox = new PictureBox { Image = logoImg, Left = 8, Top = 10, Width = 32, Height = 32, SizeMode = PictureBoxSizeMode.StretchImage, Cursor = Cursors.Hand };
            logoBox.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://creat3dworld.com") { UseShellExecute = true }); } catch { } };
            topBar.Controls.Add(logoBox);
        } catch { }
        var titleLabel = new Label { Text = "C3D Print Bridge — Configure", Left = 48, Top = 6, Width = 400, Height = 20, Font = new System.Drawing.Font("Segoe UI", 11, System.Drawing.FontStyle.Bold) };
        topBar.Controls.Add(titleLabel);
        var autoSaveNote = new Label { Text = "Changes save to disk automatically.", Left = 48, Top = 26, Width = 400, Height = 18, ForeColor = System.Drawing.Color.Gray };
        topBar.Controls.Add(autoSaveNote);
        Controls.Add(topBar);

        // Flyout (right, hidden by default). Fields live in a
        // FlowLayoutPanel, one panel per field, so fields can never
        // stack on top of each other: hiding a field collapses its
        // panel and the rest reflow. Absolute Top math is gone.
        SetDoubleBuffered(_flyout);
        _flyout.Dock = DockStyle.Right;
        _flyout.Width = 360;
        _flyout.Visible = false;
        _flyout.BackColor = System.Drawing.Color.FromArgb(245, 245, 245);
        _flyout.Padding = new Padding(0);
        var flyTitlePanel = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = System.Drawing.Color.FromArgb(245, 245, 245) };
        _flyTitle.Left = 12; _flyTitle.Top = 8; _flyTitle.Width = 280; _flyTitle.Height = 20; _flyTitle.Font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold);
        flyTitlePanel.Controls.Add(_flyTitle);
        _flyClose.Text = "X"; _flyClose.Left = 312; _flyClose.Top = 4; _flyClose.Width = 32; _flyClose.Height = 28;
        _flyClose.Click += (_, _) => CloseFlyout();
        flyTitlePanel.Controls.Add(_flyClose);
        _flyout.Controls.Add(flyTitlePanel);
        // REVERT to 2264: flyout fields live in a FlowLayoutPanel,
        // one panel per field, in order. The 2265 TableLayout
        // experiment is removed. Unlike the original 2264 code,
        // field panels are given a FIXED size (320x48) instead of
        // Dock Fill — Dock Fill inside a flow child was the
        // 2264 collapse that stacked labels on fields. A fixed
        // size panel with label at Top 0 and field at Top 18
        // cannot collapse, and FlowLayout skips invisible
        // panels, so hidden fields leave no gaps.
        _flyFlow.Dock = DockStyle.Fill;
        _flyFlow.FlowDirection = FlowDirection.TopDown;
        _flyFlow.WrapContents = false;
        _flyFlow.AutoScroll = false;
        _flyFlow.Padding = new Padding(12);
        _flyFlow.BackColor = System.Drawing.Color.FromArgb(245, 245, 245);
        SetDoubleBuffered(_flyFlow);
        _flyout.Controls.Add(_flyFlow);
        // Field panels: label at Top 0, field at Top 18, all
        // positioned absolutely inside a fixed-size panel.
        void FieldPanel(Panel pn, Label label, Control control) {
            pn.Size = new Size(320, 48); pn.Margin = new Padding(0);
            label.Left = 0; label.Top = 0; label.Width = 320; label.Height = 18;
            control.Left = 0; control.Top = 18; control.Width = 320; control.Height = 28;
            pn.Controls.Add(label); pn.Controls.Add(control);
        }
        FieldPanel(_namePanel, _nameLabel, _name);
        _brandIpPanel.Size = new Size(320, 48); _brandIpPanel.Margin = new Padding(0);
        var brandPanel = new Panel { Left = 0, Top = 0, Width = 152, Height = 48 };
        _typeLabel.Left = 0; _typeLabel.Top = 0; _typeLabel.Width = 152; _typeLabel.Height = 18;
        _type.Left = 0; _type.Top = 18; _type.Width = 152; _type.Height = 28;
        brandPanel.Controls.Add(_typeLabel); brandPanel.Controls.Add(_type);
        var ipPanel = new Panel { Left = 168, Top = 0, Width = 152, Height = 48 };
        _ipLabel.Left = 0; _ipLabel.Top = 0; _ipLabel.Width = 152; _ipLabel.Height = 18;
        _ip.Left = 0; _ip.Top = 18; _ip.Width = 152; _ip.Height = 28;
        ipPanel.Controls.Add(_ipLabel); ipPanel.Controls.Add(_ip);
        _brandIpPanel.Controls.Add(brandPanel); _brandIpPanel.Controls.Add(ipPanel);
        FieldPanel(_deviceIdPanel, _deviceIdLabel, _deviceId);
        FieldPanel(_loginUserPanel, _loginUserLabel, _loginUser);
        _loginPassPanel.Size = new Size(320, 48); _loginPassPanel.Margin = new Padding(0);
        _loginPassLabel.Left = 0; _loginPassLabel.Top = 0; _loginPassLabel.Width = 320; _loginPassLabel.Height = 18;
        var passRow = new Panel { Left = 0, Top = 18, Width = 320, Height = 28 };
        _loginPass.Left = 0; _loginPass.Top = 0; _loginPass.Width = 280; _loginPass.Height = 28;
        _eyeBtn.Left = 288; _eyeBtn.Top = 0; _eyeBtn.Width = 32; _eyeBtn.Height = 28;
        passRow.Controls.Add(_loginPass); passRow.Controls.Add(_eyeBtn);
        _loginPassPanel.Controls.Add(_loginPassLabel); _loginPassPanel.Controls.Add(passRow);
        FieldPanel(_apiKeyPanel, _apiKeyLabel, _apiKeyBox);
        FieldPanel(_keyPanel, _keyLabel, _key);
        FieldPanel(_targetPanel, _targetLabel, _target);
        _portHintPanel.Size = new Size(320, 48); _portHintPanel.Margin = new Padding(0);
        _portHint.Left = 0; _portHint.Top = 0; _portHint.Width = 320; _portHint.Height = 48; _portHint.AutoSize = false; _portHint.ForeColor = System.Drawing.Color.Gray;
        _portHintPanel.Controls.Add(_portHint);
        var flyPanels = new Panel[] { _namePanel, _brandIpPanel, _deviceIdPanel, _loginUserPanel, _loginPassPanel, _apiKeyPanel, _keyPanel, _targetPanel, _portHintPanel };
        foreach (var pn in flyPanels) _flyFlow.Controls.Add(pn);

        // Main list area (fills remaining space, scales with window).
        // No description label above the table: just the table.
        var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        SetDoubleBuffered(main);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.MultiSelect = false;
        _list.Columns.Add("Brand", 120);
        _list.Columns.Add("IP Address", 150);
        _list.Columns.Add("Status", 130);
        _list.Left = 0; _list.Top = 0;
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        // NOTE: selection alone does NOT set _editingIndex.
        // That used to write flyout fields into the wrong printer
        // when a row was clicked while the flyout was open for
        // another printer, creating duplicate names that then
        // blocked every close. _editingIndex is set only when a
        // flyout actually opens for that printer.
        // IP click must behave the same whether the flyout is open or
        // closed, so the column is worked out from the X coordinate
        // and the actual column widths, not from HitTest's subitem,
        // which can report the wrong column after a resize/flyout.
        // IP (column 1) always opens the printer in a browser and
        // never opens setup. Status (column 2) opens setup only on
        // the blue "Needs setup" link. Any other single click only
        // selects the row.
        _list.MouseClick += (_, e) => {
            var hit = _list.HitTest(e.Location);
            if (hit.Item == null || hit.Item.Index >= _printers.Count) return;
            var col = ColumnAtX(e.X);
            if (col == 1) {
                var pr = _printers[hit.Item.Index];
                if (string.Equals((pr.Type ?? "").Trim(), "bambu", StringComparison.OrdinalIgnoreCase)) { _status.Text = "Bambu printers have no browser page — use Bambu Studio or Bambu Handy for this printer. Its IP is only for the bridge's MQTT connection."; return; }
                if (!string.Equals((pr.Type ?? "").Trim(), "snapmaker", StringComparison.OrdinalIgnoreCase)) { _status.Text = "This printer type has no confirmed browser page, so its IP is not a link. Use its own app or screen for it."; return; }
                var ip = pr.Ip; if (!string.IsNullOrWhiteSpace(ip)) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://" + ip.Trim()) { UseShellExecute = true }); } catch { } }
            }
            else if (col == 2 && hit.Item.SubItems.Count > 2 && hit.Item.SubItems[2].Text == "Needs setup") { _editingIndex = hit.Item.Index; OpenFlyoutPrinter(_printers[_editingIndex]); }
        };
        _list.MouseDoubleClick += (_, e) => {
            var hit = _list.HitTest(e.Location);
            if (hit.Item == null || hit.Item.Index >= _printers.Count) return;
            if (ColumnAtX(e.X) == 1) return; // double-clicking IP is still just "open the printer", never setup
            _editingIndex = hit.Item.Index; OpenFlyoutPrinter(_printers[_editingIndex]);
        };
        _list.Resize += (_, _) => ScaleColumns();
        main.Controls.Add(_list);
        // Button area: docked at the bottom of main, two rows of
        // four equal columns. Buttons can never be pushed off the
        // bottom or cut in half by the flyout, because the layout
        // engine sizes them from the panel, not from absolute Top
        // math that used to hide the second row.
        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 84 };
        SetDoubleBuffered(buttonPanel);
        var buttonGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0) };
        for (var i = 0; i < 3; i++) buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        buttonGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        buttonGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var scanBtn = new Button { Text = "Scan WiFi", Dock = DockStyle.Fill, Margin = new Padding(4) };
        scanBtn.Click += async (_, _) => await ScanAsync(scanBtn);
        var deleteBtn = new Button { Text = "Delete selected", Dock = DockStyle.Fill, Margin = new Padding(4) };
        deleteBtn.Click += (_, _) => DeleteSelected();
        // No "Open IP in browser" button any more: the IP in the
        // table itself is the link for Snapmaker, and Bambu has no
        // browser page, so the button was only taking a slot.
        var pullBtn = new Button { Text = "Pull from dashboard", Dock = DockStyle.Fill, Margin = new Padding(4) };
        pullBtn.Click += async (_, _) => await PullFromDashboardAsync(pullBtn);
        var bridgeBtn = new Button { Text = "Bridge settings", Dock = DockStyle.Fill, Margin = new Padding(4) };
        bridgeBtn.Click += (_, _) => OpenFlyoutBridge();
        var confBtn = new Button { Text = "Configure selected", Dock = DockStyle.Fill, Margin = new Padding(4) };
        confBtn.Click += (_, _) => { if (_list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _printers.Count) { _editingIndex = _list.SelectedIndices[0]; OpenFlyoutPrinter(_printers[_editingIndex]); } };
        // No status text in the grid any more: the lower-right
        // status field is removed entirely, per the user.
        // Its slot becomes Add printer, so a printer Scan
        // misses can still be added by hand by IP and brand.
        var addBtn = new Button { Text = "Add printer", Dock = DockStyle.Fill, Margin = new Padding(4) };
        addBtn.Click += (_, _) => { _editingIndex = -1; OpenFlyoutPrinter(null); };
        buttonGrid.Controls.Add(scanBtn, 0, 0); buttonGrid.Controls.Add(deleteBtn, 1, 0); buttonGrid.Controls.Add(pullBtn, 2, 0);
        buttonGrid.Controls.Add(bridgeBtn, 0, 1); buttonGrid.Controls.Add(confBtn, 1, 1); buttonGrid.Controls.Add(addBtn, 2, 1);
        buttonPanel.Controls.Add(buttonGrid);
        main.Controls.Add(buttonPanel);
        main.Resize += (_, _) => {
            _list.Width = main.ClientSize.Width - 24;
            _list.Height = Math.Max(100, main.ClientSize.Height - buttonPanel.Height - 24);
        };
        Controls.Add(main);
        // Flyout fields (created once, laid out by the FlowLayoutPanel)
        _nameLabel.Text = "Name"; _typeLabel.Text = "Brand"; _ipLabel.Text = "IP Address"; _apiKeyLabel.Text = "API Key";
        _deviceIdLabel.Text = "Device ID"; _loginUserLabel.Text = "Username"; _loginPassLabel.Text = "Password";
        _type.DropDownStyle = ComboBoxStyle.DropDown; _type.Items.Add("Bambu"); _type.Items.Add("Snapmaker"); _type.Items.Add("Other");
        _type.TextChanged += (_, _) => UpdateLoginVisibility();
        _loginPass.UseSystemPasswordChar = true;
        _apiKeyBox.UseSystemPasswordChar = true;
        _key.UseSystemPasswordChar = true;
        // Eye button: toggles the password / API key fields between
        // masked and visible, for every brand including Bambu.
        _eyeBtn.Text = "👁"; _eyeBtn.Click += (_, _) => { var show = _loginPass.UseSystemPasswordChar; _loginPass.UseSystemPasswordChar = !show; _apiKeyBox.UseSystemPasswordChar = !show; _key.UseSystemPasswordChar = !show; };
        _target.Text = _initial.DashboardBaseUrl;
        _target.TextChanged += (_, _) => UpdatePortHint();
        UpdatePortHint();
        _keyLabel.Text = "Dashboard API Key"; _targetLabel.Text = "Dashboard host URL";
        // Bridge panels are the bridge-mode fields; printer panels
        // are the printer-mode fields. FlowLayout reflows them.
        foreach (var pn in new Panel[] { _keyPanel, _targetPanel, _portHintPanel }) _bridgeControls.Add(pn);
        RefreshList();
        if (_printers.Count > 0) { _list.Items[0].Selected = true; }
        ScaleColumns();
    }
    private List<Control> _bridgeControls = new();
    private const string LogoPngBase64 = "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";
    private static void SetDoubleBuffered(Control c) {
        // Panel.DoubleBuffered is protected, so set it by reflection.
        // This plus explicit Invalidate/Update after every flyout
        // change is the fix for the ghosting: without it, switching
        // flyout modes left the old fields painted on top of the new
        // ones until the window was moved (which forced a repaint).
        try { typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(c, true); } catch { }
    }
    private void RefreshFlyout() {
        // Force a full repaint now, instead of waiting for a move or
        // resize to repaint: this is what makes the ghost go away.
        _flyout.AutoScrollPosition = new System.Drawing.Point(0, 0);
        _flyFlow.Invalidate(true);
        _flyFlow.Update();
        _flyout.Invalidate(true);
        _flyout.Update();
        Invalidate(true);
        Update();
    }
    private int ColumnAtX(int x) {
        var acc = 0;
        for (var i = 0; i < _list.Columns.Count; i++) { acc += _list.Columns[i].Width; if (x < acc) return i; }
        return -1;
    }
    private void ScaleColumns() {
        if (_list.Columns.Count < 3 || _list.ClientSize.Width <= 0) return;
        // Columns grow with the window only up to a cap, then stop:
        // long values are shown truncated with an ellipsis in
        // RefreshList, except IP, which is never truncated and gets
        // a fixed width wide enough for any IPv4 address.
        var w = _list.ClientSize.Width;
        _list.Columns[0].Width = Math.Min(160, Math.Max(90, w * 22 / 100));
        _list.Columns[1].Width = 150;
        _list.Columns[2].Width = Math.Min(170, Math.Max(110, w * 25 / 100));
    }
    private static string Ellipsis(string s, int maxChars) {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
        return s[..Math.Max(1, maxChars - 1)] + "…";
    }
    private void SetFlyoutRowHeights() {
        // FlowLayout skips invisible panels on its own, so no
        // row-height bookkeeping is needed in the 2264 layout.
        _flyFlow.PerformLayout();
    }
    private void OpenFlyoutPrinter(PrinterConfig? pr) {
        _flyout.SuspendLayout();
        _flyoutPrinterMode = true;
        _flyTitle.Text = pr == null ? "Add printer" : "Configure printer";
        _loadedPrinter = pr;
        _editingIndex = pr == null ? -1 : _printers.IndexOf(pr);
        if (pr != null) LoadFields(pr); else ClearFields();
        foreach (var c in _bridgeControls) c.Visible = false;
        foreach (var c in new Control[] { _namePanel, _brandIpPanel, _deviceIdPanel, _loginUserPanel, _loginPassPanel, _apiKeyPanel }) c.Visible = true;
        UpdateLoginVisibility();
        SetFlyoutRowHeights();
        _flyout.ResumeLayout(true);
        ShowFlyout();
        RefreshFlyout();
    }
    private void OpenFlyoutBridge() {
        _flyout.SuspendLayout();
        _flyoutPrinterMode = false;
        _flyTitle.Text = "Bridge settings";
        foreach (var c in new Control[] { _namePanel, _brandIpPanel, _deviceIdPanel, _loginUserPanel, _loginPassPanel, _apiKeyPanel }) c.Visible = false;
        foreach (var c in _bridgeControls) c.Visible = true;
        SetFlyoutRowHeights();
        _flyout.ResumeLayout(true);
        ShowFlyout();
        RefreshFlyout();
    }
    private void ShowFlyout() {
        if (!_flyout.Visible) {
            _flyout.Visible = true;
            var wa0 = Screen.FromControl(this)?.WorkingArea ?? Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 840, 560);
            // Never grow past the screen the window is actually on
            // (laptop vs TV): if the flyout would push the right edge
            // off-screen, shrink to fit instead of cutting buttons off.
            Width = Width + 360 > wa0.Width - 20 ? Math.Max(MinimumSize.Width, wa0.Width - 20) : Width + 360;
            Recenter();
        }
        // Focus the first field so the user can type immediately.
        if (_flyoutPrinterMode) { _name.Focus(); _name.SelectAll(); }
        else { _key.Focus(); }
        RefreshFlyout();
    }
    private void Recenter() {
        // Use the screen this window is actually on, not always the
        // primary screen — the mini-PC moves between laptop and TV,
        // and recentering to the wrong screen cut the right edge off.
        var wa = Screen.FromControl(this)?.WorkingArea ?? Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 840, 560);
        if (Width > wa.Width - 20) Width = Math.Max(MinimumSize.Width, wa.Width - 20);
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        if (Left + Width > wa.Right) Left = Math.Max(wa.Left, wa.Right - Width);
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
    }
    private void CloseFlyout() {
        if (!_flyout.Visible) return;
        // No Save button any more: closing the flyout applies the
        // fields AND writes them to disk immediately. If the fields
        // are invalid, the warning shows once and the flyout still
        // closes (those changes are discarded, the saved list is
        // untouched) — closing must never be trapped by a warning.
        var applied = true;
        if (_flyoutPrinterMode && (_name.Text.Trim().Length > 0 || _ip.Text.Trim().Length > 0 || _loadedPrinter != null)) {
            applied = ApplyFields();
        }
        SaveToDisk();
        if (!applied) _status.Text = "Closed without saving those fields.";
        else if (!_flyoutPrinterMode) _status.Text = "Saved to disk.";
        _flyout.Visible = false; Width = Math.Max(MinimumSize.Width, Width - 360); Recenter();
        RefreshFlyout();
    }
    // No absolute flyout layout any more: the FlowLayoutPanel
    // stacks the visible field panels, so fields cannot stack
    // on top of each other or hide behind the title, whichever
    // path opened the flyout (Needs setup link, row double-click,
    // or Configure selected all run the same OpenFlyoutPrinter).
    private void UpdateLoginVisibility() {
        var type = (_type.Text ?? "").Trim().ToLowerInvariant();
        // Brand-change sync: carry the CURRENT edited credential,
        // never the original loaded value. That is the access-code
        // restore bug: Other's password box used to be filled with
        // the API key on load, so deleting the API key left the old
        // value in the password box, and switching to Bambu showed
        // it again as the Access code. Now: Other -> Bambu carries
        // the current API key box (empty if deleted); Bambu -> Other
        // carries the current Access code into the API key box and
        // starts the password empty; Bambu <-> Snapmaker clears the
        // password, because a Paxx password is not a Bambu code.
        // Snapmaker <-> Other share the password box, so it stays.
        // Sync runs only on a real user brand change to a full
        // brand name, never while LoadFields/ClearFields set the
        // brand programmatically.
        if (!_suppressBrandSync && _flyoutPrinterMode && _prevBrand != null && _prevBrand != type
            && (type == "bambu" || type == "snapmaker" || type == "other")
            && (_prevBrand == "bambu" || _prevBrand == "snapmaker" || _prevBrand == "other")) {
            if (_prevBrand == "other" && type == "bambu") { _loginPass.Text = _apiKeyBox.Text; }
            else if (_prevBrand == "bambu" && type == "other") { _apiKeyBox.Text = _loginPass.Text; _loginPass.Text = ""; }
            else if (_prevBrand == "bambu" && type == "snapmaker") { _loginPass.Text = ""; }
            else if (_prevBrand == "snapmaker" && type == "bambu") { _loginPass.Text = ""; }
            _prevBrand = type;
        }
        if (_flyoutPrinterMode && _prevBrand == null) _prevBrand = type;
        if (type == "bambu") {
            _deviceIdLabel.Text = "Device ID / Serial"; _deviceIdPanel.Visible = true;
            _loginUserPanel.Visible = false;
            _loginPassLabel.Text = "Access Code"; _loginPassPanel.Visible = true;
            _apiKeyPanel.Visible = false;
        } else if (type == "snapmaker") {
            _deviceIdPanel.Visible = false;
            _loginUserLabel.Text = "Username"; _loginUserPanel.Visible = true;
            _loginPassLabel.Text = "Password"; _loginPassPanel.Visible = true;
            _apiKeyPanel.Visible = false;
        } else {
            _deviceIdLabel.Text = "Device ID / Serial"; _deviceIdPanel.Visible = true;
            _loginUserLabel.Text = "Username"; _loginUserPanel.Visible = true;
            _loginPassLabel.Text = "Password"; _loginPassPanel.Visible = true;
            _apiKeyPanel.Visible = true;
        }
        SetFlyoutRowHeights();
    }
    private void UpdatePortHint() {
        var url = _target.Text.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsDefaultPort) _portHint.Text = $"Port: {uri.Port} (from the URL above).";
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) _portHint.Text = "Port: 443 (https default). Change the URL above to point the bridge elsewhere.";
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) _portHint.Text = "Port: 80 (http default). Change the URL above to point the bridge elsewhere.";
        else _portHint.Text = "Port: shown here once a valid http(s) URL is entered above.";
    }
    private static bool IsReady(PrinterConfig p) {
        var type = (p.Type ?? "").Trim().ToLowerInvariant();
        if (type == "bambu") return !string.IsNullOrWhiteSpace(p.Serial) && !string.IsNullOrWhiteSpace(p.AccessCode);
        if (type == "snapmaker") return (!string.IsNullOrWhiteSpace(p.PaxxUser) && !string.IsNullOrWhiteSpace(p.PaxxPassword)) || !string.IsNullOrWhiteSpace(p.AccessCode) || !string.IsNullOrWhiteSpace(p.LoginPassword);
        return !string.IsNullOrWhiteSpace(p.LoginPassword) || (!string.IsNullOrWhiteSpace(p.LoginUser) && !string.IsNullOrWhiteSpace(p.LoginPassword));
    }
    private void RefreshList() {
        int selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        _list.Items.Clear();
        foreach (var p in _printers) {
            var ready = IsReady(p);
            var item = new ListViewItem(Ellipsis(Capitalize(p.Type ?? ""), 18));
            // Without this, WinForms ignores every subitem ForeColor/Font
            // and paints all columns in the item's own style — which is
            // why the blue IP / blue Needs setup links never looked blue.
            item.UseItemStyleForSubItems = false;
            var ipItem = new ListViewItem.ListViewSubItem(item, p.Ip ?? "");
            if (string.Equals((p.Type ?? "").Trim(), "snapmaker", StringComparison.OrdinalIgnoreCase)) {
                ipItem.ForeColor = System.Drawing.Color.Blue;
                ipItem.Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Underline);
            }
            item.SubItems.Add(ipItem);
            var statusItem = new ListViewItem.ListViewSubItem(item, ready ? "Ready" : "Needs setup");
            if (ready) { statusItem.ForeColor = System.Drawing.Color.FromArgb(0, 128, 0); }
            else { statusItem.ForeColor = System.Drawing.Color.Blue; statusItem.Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Underline); }
            item.SubItems.Add(statusItem);
            _list.Items.Add(item);
        }
        if (selected >= 0 && selected < _list.Items.Count) _list.Items[selected].Selected = true;
        ScaleColumns();
    }
    private void LoadFields(PrinterConfig p) {
        // Each box holds ONLY its own brand's value. The old code
        // filled Other's password box with the API key and
        // Snapmaker's password box with the Moonraker key, so
        // deleting one credential left a shadow copy that came
        // back on a brand change. No cross-population any more.
        _suppressBrandSync = true;
        _name.Text = p.Name;
        _type.Text = Capitalize(p.Type);
        _ip.Text = p.Ip;
        _deviceId.Text = p.Serial ?? "";
        var type = (p.Type ?? "").Trim().ToLowerInvariant();
        if (type == "bambu") { _loginUser.Text = ""; _loginPass.Text = p.AccessCode ?? ""; _apiKeyBox.Text = ""; }
        else if (type == "snapmaker") { _loginUser.Text = p.PaxxUser ?? p.LoginUser ?? ""; _loginPass.Text = p.PaxxPassword ?? p.LoginPassword ?? ""; _apiKeyBox.Text = p.AccessCode ?? ""; }
        else { _loginUser.Text = p.LoginUser ?? ""; _loginPass.Text = p.LoginPassword ?? ""; _apiKeyBox.Text = p.AccessCode ?? ""; }
        _prevBrand = type;
        _suppressBrandSync = false;
        // Camera is always on now; the checkbox is gone.
    }
    private void ClearFields() {
        _suppressBrandSync = true;
        _name.Text = ""; _type.Text = "Other"; _ip.Text = "";
        _deviceId.Text = ""; _loginUser.Text = ""; _loginPass.Text = ""; _apiKeyBox.Text = "";
        _prevBrand = "other";
        _suppressBrandSync = false;
    }
    private bool ApplyFields() {
        var name = _name.Text.Trim();
        var type = (_type.Text ?? "").Trim().ToLowerInvariant();
        var ip = _ip.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip)) { MessageBox.Show("Fill in the printer's IP address first.", "C3D Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        if (string.IsNullOrWhiteSpace(name)) name = ip; // internal only; the list shows Type + IP, never a duplicate Name column
        if (string.IsNullOrWhiteSpace(type)) { MessageBox.Show("Pick or type a brand / firmware type first.", "C3D Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        // Target is the printer the flyout actually opened, by
        // reference — never by a selection that may have moved
        // while the flyout was open. That mismatch used to write
        // one printer's fields over another's, creating the
        // duplicate that then blocked every close.
        var targetIndex = _loadedPrinter != null ? _printers.IndexOf(_loadedPrinter) : -1;
        if (targetIndex < 0 && _editingIndex >= 0 && _editingIndex < _printers.Count) targetIndex = _editingIndex;
        // Names must stay unique, but a duplicate must never
        // block saving or closing: auto-rename instead of a
        // warning loop (e.g. "10.0.0.214 2").
        var renamed = false;
        var baseName = name;
        var suffix = 2;
        while (_printers.Where((_, i) => i != targetIndex).Any(pr => string.Equals(pr.Name, name, StringComparison.OrdinalIgnoreCase))) { name = $"{baseName} {suffix}"; suffix++; renamed = true; }
        var deviceId = string.IsNullOrWhiteSpace(_deviceId.Text) ? null : _deviceId.Text.Trim();
        var user = string.IsNullOrWhiteSpace(_loginUser.Text) ? null : _loginUser.Text.Trim();
        var pass = string.IsNullOrWhiteSpace(_loginPass.Text) ? null : _loginPass.Text;
        var apiKey = string.IsNullOrWhiteSpace(_apiKeyBox.Text) ? null : _apiKeyBox.Text;
        // Bambu printers CAN be saved incomplete (no Device ID /
        // Access code yet): they simply stay "Needs setup" until
        // those are filled in. Blocking close on missing credentials
        // trapped the user in the flyout with no way out.
        var basePr = targetIndex >= 0 ? _printers[targetIndex] : new PrinterConfig();
        var printer = basePr with { Name = name, Type = type, Ip = ip, Serial = deviceId, LoginUser = user, LoginPassword = pass, Camera = true };
        if (type == "bambu") printer = printer with { AccessCode = pass, PaxxUser = null, PaxxPassword = null };
        else if (type == "snapmaker") {
            if (user != null) printer = printer with { PaxxUser = user, PaxxPassword = pass, AccessCode = basePr.AccessCode };
            else printer = printer with { PaxxUser = null, PaxxPassword = null, AccessCode = pass };
        } else printer = printer with { PaxxUser = null, PaxxPassword = null, AccessCode = apiKey };
        if (targetIndex >= 0) _printers[targetIndex] = printer;
        else { _printers.Add(printer); targetIndex = _printers.Count - 1; }
        _editingIndex = targetIndex;
        _loadedPrinter = printer;
        RefreshList();
        if (_editingIndex >= 0 && _editingIndex < _list.Items.Count) _list.Items[_editingIndex].Selected = true;
        _status.Text = renamed ? $"Saved as '{name}'." : "Saved to disk.";
        return true;
    }
    private void DeleteSelected() {
        if (_list.SelectedIndices.Count == 0 || _list.SelectedIndices[0] >= _printers.Count) return;
        var p = _printers[_list.SelectedIndices[0]];
        var answer = MessageBox.Show($"Delete printer '{p.Name}' from this bridge only? This does not touch the printer itself, and it does NOT delete it from the dashboard — dashboard deletes come from the dashboard.", "C3D Print Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        var wasLoaded = _loadedPrinter == p;
        _printers.RemoveAt(_list.SelectedIndices[0]);
        _editingIndex = -1;
        if (wasLoaded) { _loadedPrinter = null; if (_flyout.Visible) { _flyout.Visible = false; Width = Math.Max(MinimumSize.Width, Width - 360); Recenter(); } }
        ClearFields();
        RefreshList();
        // Local delete only: do NOT push to the dashboard, so a
        // local delete can never remove a dashboard printer.
        SaveToDisk(push: false);
        _status.Text = "Deleted locally. Dashboard keeps it until deleted there.";
    }
    private async Task ScanAsync(Button scanBtn) {
        scanBtn.Enabled = false;
        _status.Text = "Scanning this PC's network…";
        try {
            var candidates = await Discovery.ProbeCandidatesAsync();
            if (candidates.Count == 0) { _status.Text = "No printer candidates found. Check the printers are on, on the same network, and in LAN mode for their brand."; return; }
            var added = 0; var updated = 0; var keysPulled = 0;
            foreach (var c in candidates) {
                var mac = MacResolver.Resolve(c.Ip);
                PrinterConfig? target = null;
                if (!string.IsNullOrWhiteSpace(mac)) {
                    var byMac = _printers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Mac) && string.Equals(p.Mac, mac, StringComparison.OrdinalIgnoreCase));
                    if (byMac is not null) { if (!string.Equals(byMac.Ip, c.Ip, StringComparison.OrdinalIgnoreCase)) { var idx = _printers.IndexOf(byMac); _printers[idx] = byMac with { Ip = c.Ip }; updated++; } target = _printers.FirstOrDefault(p => string.Equals(p.Ip, c.Ip, StringComparison.OrdinalIgnoreCase)); }
                    else {
                        var byIp = _printers.FirstOrDefault(p => string.Equals(p.Ip, c.Ip, StringComparison.OrdinalIgnoreCase));
                        if (byIp is not null) { var idx2 = _printers.IndexOf(byIp); _printers[idx2] = byIp with { Mac = mac }; target = _printers[idx2]; }
                    }
                } else { target = _printers.FirstOrDefault(p => string.Equals(p.Ip, c.Ip, StringComparison.OrdinalIgnoreCase)); }
                if (target is null) {
                    var printer = new PrinterConfig { Name = c.Ip, Type = c.Kind, Ip = c.Ip, Camera = true, Mac = mac };
                    _printers.Add(printer); added++; target = printer;
                }
                // Snapmaker auto-pull: if the printer's UI opens without a
                // password, Moonraker answers /access/api_key to a trusted
                // LAN client, so Scan can fill the key with no typing.
                // A 401 means it is not trusted — leave it for Paxx login.
                if (c.Kind == "snapmaker" && string.IsNullOrWhiteSpace(target.AccessCode)) {
                    var key = await C3DBridge.Printers.SnapmakerAdapter.TryFetchApiKeyAsync(c.Ip, c.Port);
                    if (!string.IsNullOrWhiteSpace(key)) {
                        var idx3 = _printers.IndexOf(target); if (idx3 >= 0) _printers[idx3] = target with { AccessCode = key }; keysPulled++;
                    }
                }
            }
            RefreshList();
            SaveToDisk();
            _status.Text = added > 0 || updated > 0 || keysPulled > 0 ? "Saved to disk." : "No new printers found.";
        } catch (Exception ex) { _status.Text = $"Scan failed: {ex.Message}"; }
        finally { scanBtn.Enabled = true; }
    }
    private async Task PullFromDashboardAsync(Button btn) {
        var key = Config.ReadKey();
        if (string.IsNullOrWhiteSpace(key)) { MessageBox.Show("No dashboard key stored yet. Paste the dashboard key in Bridge settings, then Pull.", "C3D Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
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
                string GetAny(params string[] names) { foreach (var n in names) { var v = Get(n); if (!string.IsNullOrWhiteSpace(v)) return v; } return ""; }
                var ip = GetAny("ip").Trim(); if (string.IsNullOrWhiteSpace(ip)) continue;
                var type = GetAny("type").Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(type)) type = "other";
                var name = GetAny("name"); if (string.IsNullOrWhiteSpace(name)) name = ip;
                var deviceId = GetAny("deviceId", "serial");
                var user = GetAny("username", "paxxUser", "loginUser");
                var pass = GetAny("password", "paxxPassword", "accessCode", "loginPassword");
                var pr = new PrinterConfig { Name = name.Trim(), Type = type, Ip = ip, Serial = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(), LoginUser = string.IsNullOrWhiteSpace(user) ? null : user.Trim(), LoginPassword = string.IsNullOrWhiteSpace(pass) ? null : pass, Camera = true, Mac = string.IsNullOrWhiteSpace(GetAny("mac")) ? null : GetAny("mac").Trim() };
                if (type == "bambu") pr = pr with { AccessCode = pass, PaxxUser = null, PaxxPassword = null };
                else if (type == "snapmaker") { if (!string.IsNullOrWhiteSpace(user)) pr = pr with { PaxxUser = user.Trim(), PaxxPassword = pass }; else pr = pr with { AccessCode = pass }; }
                var existing = _printers.FindIndex(x => string.Equals(x.Ip, ip, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) { _printers[existing] = pr; updated++; } else { _printers.Add(pr); added++; }
            }
            RefreshList();
            SaveToDisk();
            _status.Text = "Pulled and saved.";
        } catch (Exception ex) { _status.Text = "Pull failed: " + ex.Message + ". Nothing changed locally."; }
        finally { btn.Enabled = true; }
    }
    private void PushToDashboard() {
        try {
            var key = Config.ReadKey();
            if (string.IsNullOrWhiteSpace(key)) return;
            var host = string.IsNullOrWhiteSpace(_target.Text) ? _initial.DashboardBaseUrl : _target.Text.Trim();
            if (!host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            var list = new List<Dictionary<string, object?>>();
            foreach (var pr in _printers) {
                if (string.IsNullOrWhiteSpace(pr.Ip)) continue;
                var user = pr.LoginUser ?? pr.PaxxUser ?? "";
                var pass = pr.LoginPassword ?? pr.PaxxPassword ?? pr.AccessCode ?? "";
                list.Add(new Dictionary<string, object?> { ["name"] = pr.Name ?? pr.Ip, ["type"] = pr.Type, ["ip"] = pr.Ip, ["deviceId"] = pr.Serial ?? "", ["username"] = user, ["password"] = pass, ["apiKey"] = pr.AccessCode ?? "", ["serial"] = pr.Serial ?? "", ["accessCode"] = pr.AccessCode ?? "", ["paxxUser"] = pr.PaxxUser ?? "", ["paxxPassword"] = pr.PaxxPassword ?? "", ["loginUser"] = user, ["loginPassword"] = pass, ["camera"] = pr.Camera, ["mac"] = pr.Mac ?? "" });
            }
            var payload = JsonSerializer.Serialize(new { bridge = "shop-pc", printers = list });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var res = http.PostAsync(host.TrimEnd('/') + "/wp-json/c3d-shipping/v1/bridge/device-config", new StringContent(payload, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            Program.Log(res.IsSuccessStatusCode ? "Device config pushed to dashboard (encrypted there)." : "Device config push to dashboard failed: " + (int)res.StatusCode + " (local save still succeeded).");
        } catch (Exception ex) { Program.Log("Device config push to dashboard failed (local save still succeeded): " + ex.Message); }
    }
    private void SaveToDisk(bool push = true) {
        // Auto-save: every change writes the whole config to disk
        // immediately. No dialog, no incomplete-printer popup — a
        // Bambu missing a Device ID or Access code simply stays
        // offline until those are filled in, and the status line
        // says so instead of blocking the save.
        _printers.RemoveAll(p => string.IsNullOrWhiteSpace(p.Ip));
        for (var i = 0; i < _printers.Count; i++) { if (string.IsNullOrWhiteSpace(_printers[i].Name)) _printers[i] = _printers[i] with { Name = _printers[i].Ip.Trim() }; }
        RefreshList();
        var cfg = _initial with { Printers = _printers, DashboardBaseUrl = string.IsNullOrWhiteSpace(_target.Text) ? _initial.DashboardBaseUrl : _target.Text.Trim() };
        Config.Save(cfg);
        if (!string.IsNullOrWhiteSpace(_key.Text)) Config.StoreKey(_key.Text);
        // Push is the automatic dashboard update: every add or
        // edit saved here is sent to the dashboard at once, with
        // no manual Pull needed. Deletes pass push:false above,
        // so they stay local-only.
        if (push) PushToDashboard();
    }
}
