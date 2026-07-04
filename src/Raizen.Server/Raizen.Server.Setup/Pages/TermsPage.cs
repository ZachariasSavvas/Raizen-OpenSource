namespace Raizen.Server.Setup.Pages;

public class TermsPage : WizardPage
{
    private bool _agreed;
    private CheckBox _agreeBox = null!;

    public override string Title    => "Open Source Notice";
    public override bool CanProceed => _agreed;

    private const string EulaText =
        "RAIZEN SECURITY SOFTWARE\r\n" +
        "OPEN SOURCE NOTICE\r\n" +
        "\r\n" +
        "Raizen is distributed as open-source software. Review the repository LICENSE.md file " +
        "for the full license text before deploying this software.\r\n" +
        "\r\n" +
        "SECURITY AND DATA COLLECTION\r\n" +
        "The Software operates as a privileged action broker and server. It stores machine " +
        "hardware identifiers, Windows user principal names (UPNs), action requests, approval " +
        "decisions, and execution results in a PostgreSQL database on the Licensee's own " +
        "infrastructure. No data is transmitted to Raizen Security's servers. All client " +
        "communication is encrypted in transit via TLS.\r\n" +
        "\r\n" +
        "NO WARRANTY\r\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\" WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, " +
        "INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A " +
        "PARTICULAR PURPOSE, AND NON-INFRINGEMENT. RAIZEN SECURITY DOES NOT WARRANT THAT " +
        "THE SOFTWARE WILL MEET LICENSEE'S REQUIREMENTS OR THAT OPERATION WILL BE " +
        "UNINTERRUPTED OR ERROR FREE.\r\n";

    public TermsPage()
    {
        BuildUi();
    }

    private void BuildUi()
    {
        BackColor = Color.White;

        var header = new Label
        {
            Text      = "Please review and accept the open-source notice to continue.",
            Font      = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(55, 65, 81),
            AutoSize  = true,
            Location  = new Point(32, 20)
        };

        var rtb = new RichTextBox
        {
            Text        = EulaText,
            Font        = new Font("Segoe UI", 8.5f),
            ReadOnly    = true,
            BackColor   = Color.FromArgb(249, 250, 251),
            ForeColor   = Color.FromArgb(31, 41, 55),
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Location    = new Point(32, 48),
            Size        = new Size(532, 390),
            TabStop     = false
        };

        _agreeBox = new CheckBox
        {
            Text      = "I have read and accept the Raizen open-source notice.",
            Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 43, 75),
            AutoSize  = true,
            Location  = new Point(32, 454),
            Cursor    = Cursors.Hand
        };
        _agreeBox.CheckedChanged += (_, _) =>
        {
            _agreed = _agreeBox.Checked;
            RaiseCanProceedChanged();
        };

        Controls.AddRange(new Control[] { header, rtb, _agreeBox });
    }
}
