// C3D Print Bridge — Action1 download-link prompt (2282).
//
// The tray menu no longer forces the operator to download
// and pick a file by hand: they paste the account-tied
// download link from their own Action1 console once, here.
// The link is stored only in bridge-config.json on this PC
// (local only, preserved by the installer), never logged,
// never sent to the dashboard, and never written to the repo.
// "Pick a file instead" keeps the original 2277 flow for an
// operator who already downloaded the installer by hand.

using System.Drawing;
using System.Windows.Forms;

namespace C3DBridge;

public sealed class Action1LinkForm : Form
{
    public string DownloadUrl => _box.Text.Trim();
    public bool PickFileInstead { get; private set; }

    private readonly TextBox _box;

    public Action1LinkForm()
    {
        Text = "Install Action1 client — console download link";
        Size = new Size(560, 260);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var label = new Label
        {
            Text = "Paste the Action1 agent download link from your Action1 console:\nEndpoints → Install Agent → Other Options → Direct download.\n\nIt is saved only on this PC (bridge-config.json) and is never logged or sent anywhere else. The bridge will then download and install the agent from that link.",
            Dock = DockStyle.Top,
            Height = 90,
            Font = new Font("Segoe UI", 9),
        };
        _box = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 9),
        };
        var ok = new Button
        {
            Text = "Download and install",
            Dock = DockStyle.Bottom,
            Height = 36,
            DialogResult = DialogResult.OK,
        };
        var pickFile = new Button
        {
            Text = "Pick a file instead…",
            Dock = DockStyle.Bottom,
            Height = 36,
        };
        pickFile.Click += (_, _) => { PickFileInstead = true; DialogResult = DialogResult.OK; Close(); };
        var cancel = new Button
        {
            Text = "Cancel",
            Dock = DockStyle.Bottom,
            Height = 36,
            DialogResult = DialogResult.Cancel,
        };
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(_box);
        Controls.Add(label);
        Controls.Add(ok);
        Controls.Add(pickFile);
        Controls.Add(cancel);
    }
}
