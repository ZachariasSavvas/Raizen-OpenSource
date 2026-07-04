using System.Diagnostics;
using Raizen.Server.Setup.Installer;

namespace Raizen.Server.Setup.Pages;

public class InstallingPage : WizardPage
{
    private readonly WizardState _state;
    private bool _done;
    private Panel      _stepsPanel  = null!;
    private ProgressBar _bar        = null!;
    private readonly List<Label> _stepLabels = [];

    public override string Title    => "Installing";
    public override bool CanProceed => _done;

    private static readonly string[] StepNames =
    [
        "Prepare PostgreSQL",           //  0
        "Create database 'raizen'",    //  1
        "Create DB user 'raizen'",     //  2
        "Grant database privileges",   //  3
        "Generate RSA signing key",    //  4
        "Generate TLS certificate",    //  5
        "Copy API files",              //  6
        "Copy Web files",              //  7
        "Write API configuration",     //  8
        "Write Web configuration",     //  9
        "Install RaizenApi service",   // 10
        "Install RaizenWeb service",   // 11
        "Configure Windows Firewall",  // 12
        "Copy agent MSI",              // 13
        "Start services",              // 14
        "Verify services running"      // 15
    ];

    public InstallingPage(WizardState state)
    {
        _state = state;
        BuildUi();
    }

    private void BuildUi()
    {
        BackColor = Color.White;

        var header = new Label
        {
            Text      = "Installing Raizen Server...",
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 43, 75),
            AutoSize  = true,
            Location  = new Point(32, 18)
        };

        _bar = new ProgressBar
        {
            Location = new Point(32, 48),
            Size     = new Size(532, 14),
            Minimum  = 0,
            Maximum  = StepNames.Length,
            Value    = 0
        };

        // stepsPanel fills remaining space below the progress bar
        _stepsPanel = new Panel
        {
            Location   = new Point(32, 72),
            Size       = new Size(532, 400),
            AutoScroll = true,
            BackColor  = Color.White
        };

        int y = 2;
        foreach (var name in StepNames)
        {
            var lbl = new Label
            {
                Text      = $"◌  {name}",
                Font      = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(107, 114, 128),
                AutoSize  = true,
                Location  = new Point(4, y)
            };
            _stepsPanel.Controls.Add(lbl);
            _stepLabels.Add(lbl);
            y += 22;
        }

        Controls.AddRange(new Control[] { header, _bar, _stepsPanel });
    }

    public override async Task OnActivatedAsync()
    {
        if (_done) return;

        foreach (var lbl in _stepLabels)
        {
            lbl.Text      = $"◌  {StepNames[_stepLabels.IndexOf(lbl)]}";
            lbl.ForeColor = Color.FromArgb(107, 114, 128);
        }
        _bar.Value = 0;

        await Task.Run(async () =>
        {
            var baseDir   = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            var msiSource = Path.Combine(baseDir, "agent", "RaizenEndpoint.msi");

            SetupLog.Section("Installation started");
            SetupLog.Info($"Server: {_state.ServerHostname}  API:{_state.ApiPort}  Web:{_state.WebPort}");
            SetupLog.Info($"Log file: {SetupLog.LogPath}");

            try
            {
                // 0 — Install PostgreSQL (if bundled installer found) then connect
                MarkRunning(0);
                SetupLog.Info("Step 0: Prepare PostgreSQL");
                if (_state.PgNeedsInstall)
                {
                    SetupLog.Info($"  Installing PostgreSQL from: {_state.PgInstallerPath}");
                    await InstallPostgreSqlAsync(_state);
                }
                await DbSetup.TestConnectionAsync(
                    _state.PgHost, _state.PgPort, _state.PgSuperUser, _state.PgSuperPass);
                MarkDone(0);
                SetupLog.Info("Step 0: OK");

                // 1-3 — Database & user
                MarkRunning(1); MarkRunning(2); MarkRunning(3);
                SetupLog.Info("Steps 1-3: Create database, user, grants");
                await DbSetup.SetupAsync(_state, CancellationToken.None);
                MarkDone(1); MarkDone(2); MarkDone(3);
                SetupLog.Info("Steps 1-3: OK");

                // 4 — Generate RSA signing key pair
                MarkRunning(4);
                SetupLog.Info("Step 4: Generate RSA signing key pair");
                var (privatePem, publicPem) = CertGenerator.GenerateRsaKeyPair();
                _state.PollSigningPrivateKeyPem = privatePem;
                _state.PollSigningPublicKeyPem  = publicPem;
                MarkDone(4);
                SetupLog.Info("Step 4: OK");

                // 5 — Generate self-signed TLS certificate
                MarkRunning(5);
                SetupLog.Info($"Step 5: Generate TLS certificate for hostname '{_state.ServerHostname}'");
                var (pfxBytes, thumbprintSha256) = CertGenerator.GenerateSelfSignedCert(
                    _state.ServerHostname, _state.CertPassword);
                _state.CertThumbprintSha256 = thumbprintSha256;
                ConfigWriter.WriteCert(_state, pfxBytes);
                CertGenerator.InstallToMachineStore(pfxBytes, _state.CertPassword);
                MarkDone(5);
                SetupLog.Info($"Step 5: OK  thumbprint(SHA-256)={thumbprintSha256}");

                // 6 — Copy API files
                MarkRunning(6);
                SetupLog.Info($"Step 6: Copy API -> {_state.ApiInstallDir}");
                CopyDir(Path.Combine(baseDir, "api"), _state.ApiInstallDir);
                MarkDone(6);
                SetupLog.Info("Step 6: OK");

                // 7 — Copy Web files
                MarkRunning(7);
                SetupLog.Info($"Step 7: Copy Web -> {_state.WebInstallDir}");
                CopyDir(Path.Combine(baseDir, "web"), _state.WebInstallDir);
                MarkDone(7);
                SetupLog.Info("Step 7: OK");

                // 8 — Write API config
                MarkRunning(8);
                SetupLog.Info("Step 8: Write API configuration");
                ConfigWriter.WriteApiConfig(_state);
                MarkDone(8);
                SetupLog.Info("Step 8: OK");

                // 9 — Write Web config
                MarkRunning(9);
                SetupLog.Info("Step 9: Write Web configuration");
                ConfigWriter.WriteWebConfig(_state);
                MarkDone(9);
                SetupLog.Info("Step 9: OK");

                // 10-11 — Install services
                MarkRunning(10); MarkRunning(11);
                SetupLog.Info("Steps 10-11: Install Windows Services");
                Installer.ServiceInstaller.Install(_state);
                MarkDone(10); MarkDone(11);
                SetupLog.Info("Steps 10-11: OK");

                // 12 — Firewall
                MarkRunning(12);
                SetupLog.Info($"Step 12: Configure Windows Firewall  API:{_state.ApiPort}  Web:{_state.WebPort}");
                FirewallSetup.AddRules(_state.ApiPort, _state.WebPort);
                MarkDone(12);
                SetupLog.Info("Step 12: OK");

                // 13 — Agent MSI (optional)
                MarkRunning(13);
                if (File.Exists(msiSource))
                {
                    SetupLog.Info($"Step 13: Copy agent MSI -> {_state.PackagesInstallDir}");
                    Directory.CreateDirectory(_state.PackagesInstallDir);
                    var msiDest = Path.Combine(_state.PackagesInstallDir, "RaizenEndpoint.msi");
                    File.Copy(msiSource, msiDest, overwrite: true);
                    _state.AgentMsiInstallPath = msiDest;
                    ConfigWriter.WriteWebConfig(_state);
                    MarkDone(13);
                    SetupLog.Info("Step 13: OK");
                }
                else
                {
                    SetupLog.Info("Step 13: Skipped (agent MSI not present)");
                    MarkSkipped(13, "agent\\RaizenEndpoint.msi not found — configure later in portal settings");
                }

                // 14 — Start services
                MarkRunning(14);
                SetupLog.Info("Step 14: Start services");
                Installer.ServiceInstaller.StartServices();
                MarkDone(14);
                SetupLog.Info("Step 14: OK");

                // 15 — Verify running
                MarkRunning(15);
                SetupLog.Info("Step 15: Verify services running");
                await Task.Delay(4000);
                VerifyServicesRunning();
                MarkDone(15);
                SetupLog.Info("Step 15: OK");

                _state.InstallSuccess = true;
                SetupLog.Info("Installation completed successfully.");
            }
            catch (Exception ex)
            {
                _state.InstallSuccess = false;
                _state.ErrorMessage   = ex.Message;
                SetupLog.Error("Installation failed", ex);

                for (int i = 0; i < _stepLabels.Count; i++)
                    if (_stepLabels[i].ForeColor == Color.FromArgb(37, 99, 235))
                        MarkFailed(i);
            }

            _done = true;
            Invoke(RaiseCanProceedChanged);
        });
    }

    private void MarkRunning(int i) => Invoke(() =>
    {
        _stepLabels[i].Text      = $"⟳  {StepNames[i]}";
        _stepLabels[i].ForeColor = Color.FromArgb(37, 99, 235);
        _stepsPanel.ScrollControlIntoView(_stepLabels[i]);
    });

    private void MarkDone(int i) => Invoke(() =>
    {
        _stepLabels[i].Text      = $"✓  {StepNames[i]}";
        _stepLabels[i].ForeColor = Color.FromArgb(21, 128, 61);
        _bar.Value               = Math.Min(_bar.Value + 1, _bar.Maximum);
    });

    private void MarkFailed(int i) => Invoke(() =>
    {
        _stepLabels[i].Text      = $"✗  {StepNames[i]}";
        _stepLabels[i].ForeColor = Color.FromArgb(153, 27, 27);
    });

    private void MarkSkipped(int i, string reason) => Invoke(() =>
    {
        _stepLabels[i].Text      = $"⊘  {StepNames[i]}  ({reason})";
        _stepLabels[i].ForeColor = Color.FromArgb(107, 114, 128);
        _bar.Value               = Math.Min(_bar.Value + 1, _bar.Maximum);
    });

    private static async Task InstallPostgreSqlAsync(WizardState state)
    {
        var psi = new ProcessStartInfo
        {
            FileName        = state.PgInstallerPath,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("unattended");
        psi.ArgumentList.Add("--superpassword");
        psi.ArgumentList.Add(state.PgSuperPass);
        psi.ArgumentList.Add("--serverport");
        psi.ArgumentList.Add(state.PgPort.ToString());
        psi.ArgumentList.Add("--servicename");
        psi.ArgumentList.Add("postgresql");

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PostgreSQL installer.");
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"PostgreSQL installer exited with code {p.ExitCode}.");

        // Give the service time to register
        await Task.Delay(3000);
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel     = Path.GetRelativePath(src, file);
            var dstFile = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(file, dstFile, overwrite: true);
        }
    }

    private static void VerifyServicesRunning()
    {
        foreach (var name in new[] { Installer.ServiceInstaller.ApiServiceName,
                                     Installer.ServiceInstaller.WebServiceName })
        {
            using var svc = new System.ServiceProcess.ServiceController(name);
            if (svc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                throw new InvalidOperationException($"Service {name} is not running after startup.");
        }
    }
}
