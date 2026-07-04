using System.ServiceProcess;
using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// Lets a user request that the Raizen service (SYSTEM) starts, stops, or
/// restarts a Windows service on this machine.
/// </summary>
public sealed class ServicesForm : RaizenFormBase
{
    private readonly ServerClient _client;

    private ComboBox _serviceCombo = null!;
    private Label    _statusChip   = null!;
    private Button   _btnStart     = null!;
    private Button   _btnStop      = null!;
    private Button   _btnRestart   = null!;
    private TextBox  _justBox      = null!;
    private Label    _statusLabel  = null!;
    private Button   _submitBtn    = null!;
    private Button   _cancelBtn    = null!;

    private ServiceController[] _services = [];
    private ActionType _selectedAction = ActionType.RestartService;

    public ServicesForm(ServerClient client)
    {
        _client = client;

        Text       = "Raizen | Request Service Control";
        ClientSize = new Size(S(440), S(430));

        SuspendLayout();

        Controls.Add(BuildHeader("Request Service Control",
            "SYSTEM will start, stop, or restart the service when approved"));
        Controls.Add(BuildBody());
        ResumeLayout(true);
        ApplyRoundedCorners();

        SelectAction(_btnRestart, ActionType.RestartService);
        Load += async (_, _) => await LoadServicesAsync();
    }

    private Panel BuildBody()
    {
        const int L  = 24;
        const int W  = 392;
        const int FW = 440;

        var body = new Panel
        {
            Location  = new Point(0, S(76)),
            Size      = new Size(S(FW), S(354)),
            BackColor = Color.White,
        };

        int y = 20;

        // Service selector
        body.Controls.Add(BoldLabel("Windows Service *", L, y)); y += 22;

        _serviceCombo = new ComboBox
        {
            Location      = new Point(S(L), S(y)),
            Size          = new Size(S(W - 100), S(26)),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font          = Font,
        };
        _serviceCombo.SelectedIndexChanged += ServiceCombo_SelectedIndexChanged;
        body.Controls.Add(_serviceCombo);

        _statusChip = new Label
        {
            Location  = new Point(S(L + W - 94), S(y + 4)),
            Size      = new Size(S(94), S(20)),
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Text      = "",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        body.Controls.Add(_statusChip);
        y += 34;

        body.Controls.Add(Sep(L, y, W)); y += 14;

        // Action
        body.Controls.Add(BoldLabel("Action *", L, y)); y += 22;

        int btnW = W / 3;
        var actionPanel = new Panel
        {
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(36)),
            BackColor = Color.FromArgb(243, 244, 246),
        };
        actionPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(BorderColor);
            pe.Graphics.DrawRectangle(pen, 0, 0, actionPanel.Width - 1, actionPanel.Height - 1);
        };

        _btnStart   = MakeSegmentButton("Start",   btnW);
        _btnStart.Location = new Point(0, 0);
        _btnStop    = MakeSegmentButton("Stop",    btnW);
        _btnStop.Location = new Point(S(btnW), 0);
        _btnRestart = MakeSegmentButton("Restart", W - btnW * 2);
        _btnRestart.Location = new Point(S(btnW * 2), 0);

        _btnStart.Click   += (_, _) => SelectAction(_btnStart,   ActionType.StartService);
        _btnStop.Click    += (_, _) => SelectAction(_btnStop,    ActionType.StopService);
        _btnRestart.Click += (_, _) => SelectAction(_btnRestart, ActionType.RestartService);

        actionPanel.Controls.Add(_btnStart);
        actionPanel.Controls.Add(_btnStop);
        actionPanel.Controls.Add(_btnRestart);
        body.Controls.Add(actionPanel);
        y += 46;

        body.Controls.Add(Sep(L, y, W)); y += 14;

        // Justification
        body.Controls.Add(BoldLabel("Reason for this request *", L, y)); y += 22;

        _justBox = new TextBox
        {
            Multiline       = true,
            Location        = new Point(S(L), S(y)),
            Size            = new Size(S(W), S(68)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. Application deployment requires service restart",
            MaxLength       = 500,
            AcceptsReturn   = true,
            ScrollBars      = ScrollBars.Vertical,
        };
        _justBox.TextChanged += (_, _) => UpdateSubmitState();
        body.Controls.Add(_justBox);
        y += 78;

        // Status + Buttons
        _statusLabel = new Label
        {
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(18)),
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = TextMuted,
        };
        body.Controls.Add(_statusLabel);
        y += 28;

        _cancelBtn = MakeButton("Cancel", 82, primary: false);
        _cancelBtn.Location = new Point(S(FW - L - 82 - 8 - 156), S(y));
        _cancelBtn.Click   += (_, _) => Close();

        _submitBtn = MakeButton("Submit Request", 156, primary: true);
        _submitBtn.Location = new Point(S(FW - L - 156), S(y));
        _submitBtn.Enabled  = false;
        _submitBtn.Click   += SubmitBtn_Click;

        body.Controls.Add(_cancelBtn);
        body.Controls.Add(_submitBtn);

        return body;
    }

    // ── Service loading ──────────────────────────────────────────────────────

    private async Task LoadServicesAsync()
    {
        SetStatus(_statusLabel, "Loading services\u2026");
        try
        {
            _services = await Task.Run(() =>
                ServiceController.GetServices()
                    .OrderBy(s => s.DisplayName)
                    .ToArray());

            _serviceCombo.BeginUpdate();
            _serviceCombo.Items.Clear();
            foreach (var svc in _services)
                _serviceCombo.Items.Add($"{svc.DisplayName}  ({svc.ServiceName})");
            _serviceCombo.EndUpdate();

            SetStatus(_statusLabel, _services.Length > 0 ? "" : "No services found.");
        }
        catch (Exception ex)
        {
            SetStatus(_statusLabel, $"Could not load services: {ex.Message}", error: true);
        }
    }

    private void ServiceCombo_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateStatusChip();
        UpdateSubmitState();
    }

    private void UpdateStatusChip()
    {
        int idx = _serviceCombo.SelectedIndex;
        if (idx < 0 || idx >= _services.Length) { _statusChip.Text = ""; return; }

        try
        {
            var status = _services[idx].Status;
            bool running = status == ServiceControllerStatus.Running;
            _statusChip.Text      = running ? "\u25cf Running" : "\u25cf Stopped";
            _statusChip.ForeColor = running ? Green : Red;
        }
        catch
        {
            _statusChip.Text      = "\u25cf Unknown";
            _statusChip.ForeColor = TextMuted;
        }
    }

    // ── Action selector ──────────────────────────────────────────────────────

    private void SelectAction(Button selected, ActionType action)
    {
        _selectedAction = action;
        SetSegmentActive(selected, _btnStart, _btnStop, _btnRestart);
    }

    // ── Submit ───────────────────────────────────────────────────────────────

    private async void SubmitBtn_Click(object? sender, EventArgs e)
    {
        if (_serviceCombo.SelectedIndex < 0)
        {
            SetStatus(_statusLabel, "Please select a service.", error: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_justBox.Text))
        {
            SetStatus(_statusLabel, "Please enter a reason for this request.", error: true);
            return;
        }

        _submitBtn.Enabled = false;
        _cancelBtn.Enabled = false;
        _justBox.Enabled   = false;
        SetStatus(_statusLabel, "Submitting request\u2026");

        var svc = _services[_serviceCombo.SelectedIndex];

        try
        {
            var actions = await _client.GetActionsAsync();
            var action  = actions.FirstOrDefault(a => a.ActionType == _selectedAction);

            if (action is null)
            {
                var label = _selectedAction switch
                {
                    ActionType.StartService => "Start Service",
                    ActionType.StopService  => "Stop Service",
                    _                       => "Restart Service",
                };
                SetStatus(_statusLabel, $"No '{label}' action is configured in the catalog. Contact your Raizen admin.", error: true);
                ReEnableControls();
                return;
            }

            var (upn, display) = GetCurrentUserInfo();
            var result = await _client.SubmitRequestAsync(new SubmitElevationRequestDto
            {
                ActionDefinitionId = action.Id,
                Justification      = _justBox.Text.Trim(),
                Parameters         = new Dictionary<string, string>
                {
                    ["ServiceName"] = svc.ServiceName,
                },
            }, upn, display);

            var estimate = RequestForm.FormatApprovalEstimate(action);
            var statusMsg = $"Request submitted (ID: {result.Id.ToString()[..8]}\u2026). Awaiting approval.";
            if (!string.IsNullOrEmpty(estimate)) statusMsg += $" {estimate}.";
            SetStatus(_statusLabel, statusMsg);
            _submitBtn.Text      = "\u2713  Request Sent";
            _submitBtn.BackColor = SuccessGreen;
            _submitBtn.FlatAppearance.MouseOverBackColor = SuccessGreenHover;
            _submitBtn.Enabled   = true;
            _submitBtn.Click    -= SubmitBtn_Click;
            _submitBtn.Click    += (_, _) => Close();
            _cancelBtn.Text     = "Close";
            _cancelBtn.Enabled  = true;
        }
        catch (Exception ex)
        {
            SetStatus(_statusLabel, $"Error: {ex.Message}", error: true);
            ReEnableControls();
        }
    }

    private void UpdateSubmitState()
    {
        _submitBtn.Enabled = _serviceCombo.SelectedIndex >= 0
                          && !string.IsNullOrWhiteSpace(_justBox.Text);
    }

    private void ReEnableControls()
    {
        _submitBtn.Enabled = true;
        _cancelBtn.Enabled = true;
        _justBox.Enabled   = true;
    }

    // ── Control factories ────────────────────────────────────────────────────

    private Label BoldLabel(string text, int x, int y) => new Label
    {
        Text      = text,
        Location  = new Point(S(x), S(y + 1)),
        AutoSize  = true,
        Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
        ForeColor = TextPrimary,
    };

    private Panel Sep(int x, int y, int width) => new Panel
    {
        Location  = new Point(S(x), S(y)),
        Size      = new Size(S(width), S(1)),
        BackColor = SepColor,
    };
}
