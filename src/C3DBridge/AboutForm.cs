// C3D Print Bridge — About dialog (2278).
//
// The tray menu had no About entry. This dialog shows, in one
// place: the product name, the running version (taken from
// Program.Version, never hard-coded, so it can never disagree
// with the build), the licence of use, the disclaimer, and the
// legal notes. The text is shown read-only, with an OK button
// to close it. No web link is opened from this dialog.

using System.Drawing;
using System.Windows.Forms;

namespace C3DBridge;

public sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "About C3D Print Bridge";
        Size = new Size(520, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10),
            Text = AboutText,
        };
        var ok = new Button
        {
            Text = "OK",
            Dock = DockStyle.Bottom,
            Height = 36,
            DialogResult = DialogResult.OK,
        };
        AcceptButton = ok;
        Controls.Add(text);
        Controls.Add(ok);
    }

    // General licence of use, disclaimer, and legal notes for
    // Creat3d World's own shop software. This is a general
    // notice for the tool itself, not legal advice, and it does
    // not change any term of sale for Creat3d World products.
    public static string AboutText => $@"C3D Print Bridge
Version {Program.Version}
© 2026 Creat3d World, USA
creat3dworld.com

LICENCE OF USE
C3D Print Bridge is licensed for use by Creat3d World on its
own shop computers, to connect its own printers to its own
shipping dashboard. It may not be redistributed, resold,
rented, or used by any other person or business without
written permission from Creat3d World. The licence covers
use of the software as provided; it does not transfer
ownership of the software, and it does not grant any right
to Creat3d World's product files, designs, or trademarks.

DISCLAIMER
This software is provided ""as is"", without warranty of any
kind, express or implied, including warranties of
merchantability or fitness for a particular purpose. Use
is at the operator's own risk. The bridge is a thin relay
only: it never slices a file, never decides which printer
runs a job, and never orders a queue — those decisions
come from the dashboard. The operator remains responsible
for confirming a print is safe to start, including bed
state and filament, before any job runs. Creat3d World is
not liable for any loss, damage, or failed print arising
from use of this software, to the fullest extent allowed
by law.

GOVERNING LAW
This licence and disclaimer are governed by the laws of
the United States and the State of Florida, with venue in
Duval County, Florida, unless otherwise required by law.
Using ""USA"" in the copyright line above does not change
that governing law or venue.

LEGAL NOTES
Printer credentials, access codes, and API keys used by
the bridge are stored only on the local shop computer and
are never sent to any third party except the printer or
dashboard they belong to. Bambu Lab, Snapmaker, Action1,
and all other product and company names mentioned in this
software are trademarks of their respective owners. No
affiliation with, or endorsement by, any of those owners
is claimed or implied.
";
}
