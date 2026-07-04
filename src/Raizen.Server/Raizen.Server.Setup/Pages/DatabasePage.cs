using Raizen.Server.Setup.Installer;

namespace Raizen.Server.Setup.Pages;

public class DatabasePage : WizardPage
{
    private readonly WizardState _state;
    private bool _tested;

    private TextBox _host        = null!;
    private TextBox _port        = null!;
    private TextBox _superUser   = null!;
    private TextBox _superPass   = null!;
    private TextBox _dbPass      = null!;
    private Button  _testBtn     = null!;
    private Label   _testStatus  = null!;
    private Label   _pgInstallNote = null!;

    public override string Title    => "Database";
    public override bool CanProceed => _tested;

    public DatabasePage(WizardState state)
    {
        _state = state;
        BuildUi();
    }

    private void BuildUi()
    {
        BackColor = Color.White;

        var header = new Label
        {
            Text      = "PostgreSQL connection",
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 43, 75),
            AutoSize  = true,
            Location  = new Point(32, 24)
        };

        int y = 60;
        _host      = AddField("PostgreSQL host",           _state.PgHost,       ref y, false);
        _port      = AddField("PostgreSQL port",           _state.PgPort.ToString(), ref y, false);
        _superUser = AddField("Superuser name",            _state.PgSuperUser,  ref y, false);
        _superPass = AddField("Superuser password",        "",                   ref y, true);
        _dbPass    = AddField("Raizen app DB password",   _state.DbPass,        ref y, true);

        var hint = new Label
        {
            Text      = "The app password is used by Raizen services (not the superuser password).",
            Font      = new Font("Segoe UI", 8),
            ForeColor = Color.FromArgb(107, 114, 128),
            AutoSize  = true,
            Location  = new Point(195, y)
        };
        y += 24;

        _pgInstallNote = new Label
        {
            Text      = "PostgreSQL will be installed using these credentials. Choose the superuser password you want to set.",
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(146, 64, 14),
            AutoSize  = false,
            Size      = new Size(360, 36),
            Location  = new Point(195, y),
            Visible   = false
        };
        _testBtn = new Button
        {
            Text      = "Test Connection",
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(37, 99, 235),
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(148, 32),
            Location  = new Point(195, y + 8),
            Cursor    = Cursors.Hand
        };
        _testBtn.FlatAppearance.BorderSize = 0;
        _testBtn.Click += TestBtn_Click;

        _testStatus = new Label
        {
            Text      = "",
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.Gray,
            AutoSize  = false,
            Size      = new Size(360, 48),
            Location  = new Point(195, y + 48)
        };

        Controls.AddRange(new Control[] { header, hint, _pgInstallNote, _testBtn, _testStatus });
    }

    private TextBox AddField(string label, string value, ref int y, bool password)
    {
        // Fixed-width label (155px) ensures the longest label "Raizen app DB password"
        // never overlaps the textbox that starts at x=195.
        var lbl = new Label
        {
            Text      = label,
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(55, 65, 81),
            AutoSize  = false,
            Size      = new Size(155, 26),
            Location  = new Point(32, y + 2),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var box = new TextBox
        {
            Text              = value,
            Font              = new Font("Segoe UI", 10),
            Location          = new Point(195, y),
            Size              = new Size(300, 26),
            UseSystemPasswordChar = password
        };
        box.TextChanged += (_, _) => ResetTest();
        Controls.Add(lbl);
        Controls.Add(box);
        y += 38;
        return box;
    }

    public override Task OnActivatedAsync()
    {
        if (_state.PgNeedsInstall)
        {
            _pgInstallNote.Visible = true;
            _testBtn.Text          = "Confirm";
            _testBtn.Location      = new Point(195, _pgInstallNote.Bottom + 8);
            _testStatus.Location   = new Point(195, _pgInstallNote.Bottom + 48);
            _superPass.Text        = "";
            _testStatus.Text       = "";
            _tested                = false;
            RaiseCanProceedChanged();
        }
        else
        {
            _pgInstallNote.Visible = false;
            _testBtn.Text          = "Test Connection";
            _testBtn.Location      = new Point(195, _pgInstallNote.Top + 8);
            _testStatus.Location   = new Point(195, _pgInstallNote.Top + 48);
        }
        return Task.CompletedTask;
    }

    private void ResetTest()
    {
        _tested = false;
        _testStatus.Text      = "";
        _testStatus.ForeColor = Color.Gray;
        RaiseCanProceedChanged();
    }

    private async void TestBtn_Click(object? sender, EventArgs e)
    {
        _testBtn.Enabled      = false;
        _testStatus.ForeColor = Color.FromArgb(107, 114, 128);

        var host      = _host.Text.Trim();
        var port      = int.TryParse(_port.Text, out var p) ? p : 5432;
        var superUser = _superUser.Text.Trim();
        var superPass = _superPass.Text;

        // When PostgreSQL is not yet installed, skip the connection test —
        // just validate the fields and save to state. The installer will run
        // during the Installing step using these credentials.
        if (_state.PgNeedsInstall)
        {
            if (string.IsNullOrWhiteSpace(superPass) || superPass.Length < 8)
            {
                _testStatus.Text      = "Password must be at least 8 characters.";
                _testStatus.ForeColor = Color.FromArgb(153, 27, 27);
                _testBtn.Enabled      = true;
                return;
            }

            _state.PgHost      = host;
            _state.PgPort      = port;
            _state.PgSuperUser = superUser;
            _state.PgSuperPass = superPass;
            _state.DbPass      = _dbPass.Text;

            _testStatus.Text      = "✓  Credentials saved — PostgreSQL will be installed during setup.";
            _testStatus.ForeColor = Color.FromArgb(21, 128, 61);
            _tested = true;
            _testBtn.Enabled = true;
            RaiseCanProceedChanged();
            return;
        }

        _testStatus.Text = "Testing connection…";
        try
        {
            await Task.Run(() => DbSetup.TestConnectionAsync(host, port, superUser, superPass));

            _state.PgHost      = host;
            _state.PgPort      = port;
            _state.PgSuperUser = superUser;
            _state.PgSuperPass = superPass;
            _state.DbPass      = _dbPass.Text;

            _testStatus.Text      = "✓  Connection successful — ready to proceed.";
            _testStatus.ForeColor = Color.FromArgb(21, 128, 61);
            _tested = true;
        }
        catch (Exception ex)
        {
            _testStatus.Text      = $"✗  {ex.Message}";
            _testStatus.ForeColor = Color.FromArgb(153, 27, 27);
            _tested = false;
        }
        finally
        {
            _testBtn.Enabled = true;
            RaiseCanProceedChanged();
        }
    }
}
