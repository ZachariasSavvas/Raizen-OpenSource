using System.ServiceProcess;

namespace Raizen.Server.Setup.Pages;

/// <summary>
/// Checks prerequisites.
/// PostgreSQL: if already installed the wizard connects to it.
/// If not installed, looks for a bundled installer in the 'postgres\' subfolder —
/// the actual installation happens later (Installing step) using the password
/// the admin enters on the Database page.
/// .NET runtime: not required — server binaries are self-contained.
/// </summary>
public class PrerequisitesPage : WizardPage
{
    private readonly WizardState _state;
    private bool _done;
    private Label _pgStatus      = null!;
    private ProgressBar _progress = null!;
    private Label _overallStatus  = null!;

    public override string Title    => "Prerequisites";
    public override bool CanProceed => _done;

    public PrerequisitesPage(WizardState state)
    {
        _state = state;
        BuildUi();
    }

    private void BuildUi()
    {
        BackColor = Color.White;

        var header = new Label
        {
            Text      = "Checking prerequisites…",
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 43, 75),
            AutoSize  = true,
            Location  = new Point(32, 24)
        };

        _pgStatus = new Label
        {
            Text      = "◌  PostgreSQL 15+  —  checking…",
            Font      = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(107, 114, 128),
            AutoSize  = true,
            Location  = new Point(32, 70)
        };

        var hint = new Label
        {
            Text      = "Air-gapped install: place a PostgreSQL Windows installer\n" +
                        "(postgresql-*.exe) in a 'postgres\\' folder next to this\n" +
                        "setup file and setup will run it automatically.",
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(107, 114, 128),
            AutoSize  = false,
            Size      = new Size(532, 52),
            Location  = new Point(32, 108)
        };

        _progress = new ProgressBar
        {
            Location              = new Point(32, 172),
            Size                  = new Size(596, 18),
            Style                 = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Visible               = false
        };

        _overallStatus = new Label
        {
            Text      = "",
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(55, 65, 81),
            AutoSize  = false,
            Size      = new Size(532, 160),
            Location  = new Point(32, 200)
        };

        Controls.AddRange(new Control[] { header, _pgStatus, hint, _progress, _overallStatus });
    }

    public override async Task OnActivatedAsync()
    {
        _done = false;
        _state.PgNeedsInstall  = false;
        _state.PgInstallerPath = "";
        RaiseCanProceedChanged();
        _progress.Visible = true;

        await Task.Run(() =>
        {
            SetStatus(_pgStatus, "Checking PostgreSQL…", Color.FromArgb(107, 114, 128));

            if (DetectPostgreSQL())
            {
                SetStatus(_pgStatus, "✓  PostgreSQL  —  already installed", Color.FromArgb(21, 128, 61));
                SetOverall("All prerequisites are ready. Click Next to continue.");
                _done = true;
            }
            else
            {
                var setupDir    = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
                var postgresDir = Path.Combine(setupDir, "postgres");
                var installer   = Directory.Exists(postgresDir)
                    ? Directory.GetFiles(postgresDir, "postgresql-*.exe").FirstOrDefault()
                    : null;

                if (installer != null)
                {
                    _state.PgNeedsInstall  = true;
                    _state.PgInstallerPath = installer;
                    SetStatus(_pgStatus,
                        $"✓  PostgreSQL installer found  —  {Path.GetFileName(installer)}",
                        Color.FromArgb(161, 98, 7));
                    SetOverall(
                        "PostgreSQL will be installed automatically during setup.\n\n" +
                        "On the next page, enter the PostgreSQL superuser password you want to use.\n" +
                        "That password will be used to initialise PostgreSQL.");
                    _done = true;
                }
                else
                {
                    SetStatus(_pgStatus,
                        "✗  PostgreSQL  —  not found",
                        Color.FromArgb(153, 27, 27));
                    SetOverall(
                        "PostgreSQL is not installed and no installer was found.\n\n" +
                        "Options:\n" +
                        "  - Install PostgreSQL 15+ manually, then click Back and Next to retry.\n" +
                        "  - Place a postgresql-*.exe installer in a 'postgres\\' folder\n" +
                        "    next to this setup file and retry.");
                    _done = false;
                }
            }

            Invoke(() => { _progress.Visible = false; RaiseCanProceedChanged(); });
        });
    }

    private static bool DetectPostgreSQL()
    {
        try
        {
            return ServiceController.GetServices()
                .Any(s => s.ServiceName.StartsWith("postgresql", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private void SetStatus(Label lbl, string text, Color color) =>
        Invoke(() => { lbl.Text = text; lbl.ForeColor = color; });

    private void SetOverall(string text) =>
        Invoke(() => _overallStatus.Text = text);
}
