using System.Diagnostics;
using Raizen.Server.Setup.Installer;

namespace Raizen.Server.Setup.Pages;

public class DonePage : WizardPage
{
    private readonly WizardState _state;

    public override string Title => "Done";

    public DonePage(WizardState state)
    {
        _state = state;
    }

    public override async Task OnActivatedAsync()
    {
        Controls.Clear();
        BuildUi();
        await Task.CompletedTask;
    }

    private void BuildUi()
    {
        BackColor = Color.White;

        if (_state.InstallSuccess)
        {
            var icon = new Label
            {
                Text      = "✓",
                Font      = new Font("Segoe UI", 48, FontStyle.Bold),
                ForeColor = Color.FromArgb(21, 128, 61),
                AutoSize  = true,
                Location  = new Point(32, 16)
            };

            var title = new Label
            {
                Text      = "Installation complete!",
                Font      = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(26, 43, 75),
                AutoSize  = true,
                Location  = new Point(100, 28)
            };

            var adminUrl = $"https://{_state.ServerHostname}:{_state.WebPort}";
            var apiUrl   = $"https://{_state.ServerHostname}:{_state.ApiPort}";

            var urls = new Label
            {
                Text      = $"Admin Portal:  {adminUrl}\n" +
                            $"API endpoint:  {apiUrl}\n\n" +
                            "Default login:  Admin  /  Admin\n" +
                            "(You will be prompted to change the password on first login.)",
                Font      = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(55, 65, 81),
                AutoSize  = false,
                Size      = new Size(532, 100),
                Location  = new Point(32, 90)
            };

            var certInfo = new Label
            {
                Text      = $"TLS cert thumbprint SHA-256 (embedded in agent deployment packages):\n" +
                            $"   {_state.CertThumbprintSha256}\n" +
                            $"   Certificate installed to LocalMachine\\My and \\Root stores.",
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(30, 64, 175),
                AutoSize  = false,
                Size      = new Size(532, 56),
                Location  = new Point(32, 206)
            };

            var keyReminder = new Label
            {
                Text      = $"Save this — losing it breaks the installation:\n" +
                            $"   Encryption key: {_state.EncryptionKey}",
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(146, 64, 14),
                AutoSize  = false,
                Size      = new Size(532, 40),
                Location  = new Point(32, 276)
            };

            var openBtn = new Button
            {
                Text      = "Open Admin Portal",
                Font      = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(79, 142, 247),
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(180, 36),
                Location  = new Point(32, 332),
                Cursor    = Cursors.Hand
            };
            openBtn.FlatAppearance.BorderSize = 0;
            openBtn.Click += (_, _) =>
                Process.Start(new ProcessStartInfo(adminUrl) { UseShellExecute = true });

            Controls.AddRange(new Control[] { icon, title, urls, certInfo, keyReminder, openBtn });
        }
        else
        {
            var icon = new Label
            {
                Text      = "✗",
                Font      = new Font("Segoe UI", 48, FontStyle.Bold),
                ForeColor = Color.FromArgb(153, 27, 27),
                AutoSize  = true,
                Location  = new Point(32, 16)
            };
            var title = new Label
            {
                Text      = "Installation failed",
                Font      = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(26, 43, 75),
                AutoSize  = true,
                Location  = new Point(100, 28)
            };
            var error = new Label
            {
                Text      = _state.ErrorMessage ?? "An unknown error occurred.",
                Font      = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(153, 27, 27),
                AutoSize  = false,
                Size      = new Size(480, 80),
                Location  = new Point(32, 90)
            };
            var logNote = new Label
            {
                Text      = $"Full error details saved to:\n{SetupLog.LogPath}",
                Font      = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(107, 114, 128),
                AutoSize  = false,
                Size      = new Size(532, 36),
                Location  = new Point(32, 180)
            };
            var openLogBtn = new Button
            {
                Text      = "Open Log File",
                Font      = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(71, 85, 105),
                FlatStyle = FlatStyle.Flat,
                Size      = new Size(130, 32),
                Location  = new Point(32, 228),
                Cursor    = Cursors.Hand
            };
            openLogBtn.FlatAppearance.BorderSize = 0;
            openLogBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(SetupLog.LogPath) { UseShellExecute = true }); }
                catch { /* log may not exist if error occurred before any logging */ }
            };
            Controls.AddRange(new Control[] { icon, title, error, logNote, openLogBtn });
        }
    }
}
