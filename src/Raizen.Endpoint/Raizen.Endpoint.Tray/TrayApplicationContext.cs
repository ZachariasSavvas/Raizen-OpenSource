using System.Security.Principal;
using Raizen.Endpoint.Tray.Forms;
using Raizen.Endpoint.Tray.Services;
using Raizen.Endpoint.Shared.Config;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray;

/// <summary>
/// WinForms ApplicationContext that owns the system tray icon.
/// The tray icon gives users quick access to submit requests and check status.
///
/// The application runs as the LOGGED-IN USER (non-elevated).
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ConfigLoader _configLoader;
    private readonly ServerClient _serverClient;
    private readonly System.Windows.Forms.Timer _statusTimer;

    // Track which request IDs and comment IDs we've already notified about
    private readonly HashSet<Guid> _notifiedRequests = [];
    private readonly HashSet<Guid> _notifiedComments = [];
    private bool _checking;

    public TrayApplicationContext(ConfigLoader configLoader)
    {
        _configLoader = configLoader;
        _serverClient = new ServerClient(configLoader);

        var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("My Requests...", null, OnMyRequests);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Manage Firewall Rule...", null, OnFirewallRule);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, OnExit);

        var trayIcon = BrandIcon.Get() ?? SystemIcons.Shield;

        _trayIcon = new NotifyIcon
        {
            Icon = trayIcon,
            ContextMenuStrip = contextMenu,
            Text = "Raizen Security Agent",
            Visible = true,
        };

        _trayIcon.DoubleClick += OnMyRequests;

        // Periodically check for status updates and show balloon notifications.
        // Also run an initial check shortly after startup so the user doesn't
        // have to wait 30 s for the first notification.
        _statusTimer = new System.Windows.Forms.Timer { Interval = 30_000 }; // 30 seconds
        _statusTimer.Tick += async (_, _) => await CheckPendingNotificationsAsync();
        _statusTimer.Start();

        // Initial check after 5 seconds so new approvals are caught quickly on restart
        var startupTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        startupTimer.Tick += async (sender, _) =>
        {
            var t = (System.Windows.Forms.Timer)sender!;
            t.Stop();
            t.Dispose();
            await CheckPendingNotificationsAsync();
        };
        startupTimer.Start();
    }

    private void OnRequestElevation(object? sender, EventArgs e)
    {
        var form = new RequestForm(_serverClient);
        form.Show();
    }

    private void OnFilePermissions(object? sender, EventArgs e)
    {
        var form = new FilePermissionsForm(_serverClient);
        form.Show();
    }

    private void OnNetworkChange(object? sender, EventArgs e)
    {
        var form = new NetworkChangeForm(_serverClient);
        form.Show();
    }

    private void OnEnvVar(object? sender, EventArgs e)
    {
        var form = new EnvVarForm(_serverClient);
        form.Show();
    }

    private void OnFirewallRule(object? sender, EventArgs e)
    {
        var form = new FirewallForm(_serverClient);
        form.Show();
    }

    private void OnManageServices(object? sender, EventArgs e)
    {
        var form = new ServicesForm(_serverClient);
        form.Show();
    }

    private async void OnMyRequests(object? sender, EventArgs e)
    {
        try
        {
            var result = await _serverClient.GetMyRequestsAsync();
            if (result.Items.Count == 0)
            {
                MessageBox.Show("No requests found for this machine.", "My Requests",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Show a simple list; user can double-click to open status form
            using var listForm = new Form
            {
                Text = "My Elevation Requests",
                Width = 700,
                Height = 400,
                StartPosition = FormStartPosition.CenterScreen,
                AutoScaleMode = AutoScaleMode.Dpi,
                AutoScaleDimensions = new SizeF(96F, 96F),
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToAddRows = false,
            };

            grid.Columns.Add("Action", "Action");
            grid.Columns.Add("Status", "Status");
            grid.Columns.Add("Submitted", "Submitted");
            grid.Columns.Add("Expires", "Expires");
            grid.Columns["Submitted"].FillWeight = 30;
            grid.Columns["Expires"].FillWeight = 30;

            foreach (var r in result.Items)
                grid.Rows.Add(r.ActionDisplayName, r.Status, r.SubmittedAt.ToLocalTime().ToString("g"), r.ExpiresAt.ToLocalTime().ToString("g"));

            grid.CellDoubleClick += (_, args) =>
            {
                if (args.RowIndex < 0 || args.RowIndex >= result.Items.Count) return;
                var req = result.Items[args.RowIndex];
                new RequestStatusForm(_serverClient, req.Id, req.ActionDisplayName).Show();
            };

            listForm.Controls.Add(grid);
            listForm.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load requests: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _statusTimer.Stop();
        Application.Exit();
    }

    private async Task CheckPendingNotificationsAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            // Prevent unbounded growth for long-running tray sessions
            if (_notifiedRequests.Count > 500) _notifiedRequests.Clear();
            if (_notifiedComments.Count > 1000) _notifiedComments.Clear();

            var result = await _serverClient.GetMyRequestsAsync(pageSize: 20);
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);

            foreach (var r in result.Items)
            {
                // ── Status change notifications (one per request, deduped) ────────
                if (!_notifiedRequests.Contains(r.Id))
                {
                    bool shouldNotify = false;
                    string title = "", text = "";
                    ToolTipIcon icon = ToolTipIcon.Info;

                    if (r.Status == RequestStatus.Approved && r.ReviewedAt > cutoff)
                    {
                        title = "Elevation Approved";
                        text  = $"'{r.ActionDisplayName}' was approved.";
                        icon  = ToolTipIcon.Info;
                        shouldNotify = true;
                    }
                    else if (r.Status == RequestStatus.Denied && r.ReviewedAt > cutoff)
                    {
                        title = "Elevation Denied";
                        text  = $"'{r.ActionDisplayName}' was denied.";
                        if (!string.IsNullOrEmpty(r.ReviewerNote))
                            text += $" Reason: {r.ReviewerNote}";
                        icon  = ToolTipIcon.Warning;
                        shouldNotify = true;
                    }
                    else if (r.Status == RequestStatus.Succeeded && r.ExecutedAt > cutoff)
                    {
                        title = "Elevation Succeeded";
                        text  = $"'{r.ActionDisplayName}' completed successfully.";
                        icon  = ToolTipIcon.Info;
                        shouldNotify = true;
                    }
                    else if (r.Status == RequestStatus.Failed && r.ExecutedAt > cutoff)
                    {
                        title = "Elevation Failed";
                        text  = $"'{r.ActionDisplayName}' failed: {r.ExecutionError}";
                        icon  = ToolTipIcon.Error;
                        shouldNotify = true;
                    }

                    if (shouldNotify)
                    {
                        _notifiedRequests.Add(r.Id);
                        _trayIcon.ShowBalloonTip(10000, title, text, icon);
                        if (r.Status == RequestStatus.Denied)
                            new DenialNotificationForm(_serverClient, r).Show();
                    }
                    else if (r.Status is RequestStatus.Succeeded or RequestStatus.Failed
                                      or RequestStatus.Denied or RequestStatus.Cancelled
                                      or RequestStatus.Expired)
                    {
                        // Terminal state — mark as seen so we don't re-check it
                        _notifiedRequests.Add(r.Id);
                    }
                }

                // ── Comment notifications (always check, deduped by comment ID) ──
                // Keep denied requests commentable so the requester can ask for clarification.
                if (r.Status is RequestStatus.Pending or RequestStatus.Approved or RequestStatus.Executing
                    or RequestStatus.Denied)
                {
                    try
                    {
                        var comments = await _serverClient.GetCommentsAsync(r.Id);
                        foreach (var c in comments)
                        {
                            if (_notifiedComments.Contains(c.Id)) continue;
                            _notifiedComments.Add(c.Id);

                            if (c.IsAdmin)
                            {
                                var author = string.IsNullOrEmpty(c.AuthorDisplayName)
                                    ? c.AuthorUpn : c.AuthorDisplayName;
                                var popup = new CommentNotificationForm(
                                    _serverClient, r.Id, r.ActionDisplayName, author, c.Body);
                                popup.Show();
                            }
                        }
                    }
                    catch { /* silently ignore per-request comment errors */ }
                }
            }
        }
        catch { /* Background check — silently ignore network errors */ }
        finally { _checking = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _statusTimer.Dispose();
            _serverClient.Dispose();
        }
        base.Dispose(disposing);
    }
}
