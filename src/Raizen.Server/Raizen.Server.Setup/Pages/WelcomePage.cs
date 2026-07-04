namespace Raizen.Server.Setup.Pages;

public class WelcomePage : WizardPage
{
    private readonly WizardState _state;
    private Label _warningLabel = null!;

    public WelcomePage(WizardState state)
    {
        _state = state;
        BuildUi();
    }

    public override string Title => "Welcome";

    private void BuildUi()
    {
        BackColor = Color.White;

        var subtitle = new Label
        {
            Text      = "Raizen Security Server — Windows Installation",
            Font      = new Font("Segoe UI", 12, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 43, 75),
            AutoSize  = true,
            Location  = new Point(32, 24)
        };

        var desc = new Label
        {
            Text      = "This wizard installs and configures the Raizen server on this machine.\n" +
                        "It runs as two Windows Services (API and Admin Portal) and requires\n" +
                        "a PostgreSQL database.",
            Font      = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(75, 85, 99),
            AutoSize  = false,
            Size      = new Size(532, 56),
            Location  = new Point(32, 60)
        };

        var stepsHeader = new Label
        {
            Text      = "The wizard will perform the following steps:",
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(55, 65, 81),
            AutoSize  = true,
            Location  = new Point(32, 130)
        };

        var steps = new Label
        {
            Text      = "  1.  Check prerequisites — detect or install PostgreSQL\n" +
                        "  2.  Configure the database connection and passwords\n" +
                        "  3.  Set server hostname and ports\n" +
                        "  4.  Generate RSA poll-signing key pair\n" +
                        "  5.  Generate and install a self-signed TLS certificate\n" +
                        "  6.  Install RaizenApi and RaizenWeb as Windows Services\n" +
                        "  7.  Configure Windows Firewall rules\n" +
                        "  8.  Start and verify both services",
            Font      = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(55, 65, 81),
            AutoSize  = false,
            Size      = new Size(532, 192),
            Location  = new Point(32, 156)
        };

        var note = new Label
        {
            Text      = "You must run this wizard as Administrator.",
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(146, 64, 14),
            AutoSize  = true,
            Location  = new Point(32, 360)
        };

        _warningLabel = new Label
        {
            Text      = "",
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(153, 27, 27),
            AutoSize  = false,
            Size      = new Size(532, 48),
            Location  = new Point(32, 392)
        };

        Controls.AddRange(new Control[] { subtitle, desc, stepsHeader, steps, note, _warningLabel });
    }

    public override Task OnActivatedAsync()
    {
        var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var missing = new List<string>();
        if (!Directory.Exists(Path.Combine(baseDir, "api"))) missing.Add("api\\");
        if (!Directory.Exists(Path.Combine(baseDir, "web"))) missing.Add("web\\");

        _warningLabel.Text = missing.Count > 0
            ? $"Missing folder(s) next to this exe: {string.Join(", ", missing)}\n" +
              "Place api\\ and web\\ alongside RaizenServer-Setup.exe before continuing."
            : "";

        return Task.CompletedTask;
    }
}
