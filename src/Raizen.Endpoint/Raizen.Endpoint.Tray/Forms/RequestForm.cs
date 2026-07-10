using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;
using System.DirectoryServices.AccountManagement;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// The primary user-facing form for submitting an elevation request.
/// The user selects an action from the catalog, fills in parameters,
/// provides a justification and optional ticket reference, then submits.
/// </summary>
public sealed class RequestForm : Form
{
    private readonly ServerClient _client;
    private readonly string? _contextPath;
    private List<ActionDefinitionDto> _actions = [];
    private ActionDefinitionDto? _selectedAction;
    private readonly Dictionary<string, Control> _parameterControls = [];

    // ── DPI helper for dynamically-created controls ──────────────────────────────
    private int S(int px) => (int)(px * (DeviceDpi / 96.0f));

    // ── Controls ───────────────────────────────────────────────────────────────
    private readonly ComboBox _actionCombo;
    private readonly Label _actionDescription;
    private readonly Panel _parametersPanel;
    private readonly TextBox _justificationBox;
    private readonly TextBox _ticketBox;
    private readonly Button _submitButton;
    private readonly Button _cancelButton;
    private readonly Label _estimatedWaitLabel;
    private readonly Label _statusLabel;

    public RequestForm(ServerClient client, string? contextPath = null)
    {
        _client = client;
        _contextPath = contextPath;

        AutoScaleMode       = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Text = "Request elevation";
        TrayStyle.ApplyDialog(this, S(600), S(620));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(S(18)),
            AutoScroll = true,
        };
        Controls.Add(layout);

        layout.Controls.Add(TrayStyle.Title("Request elevation"));
        layout.Controls.Add(TrayStyle.HelpText("Choose the action you need, add the required context, and submit it for approval."));

        // Action selector
        layout.Controls.Add(TrayStyle.FieldLabel("Action"));
        _actionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        TrayStyle.StyleComboBox(_actionCombo);
        _actionCombo.SelectedIndexChanged += ActionCombo_SelectedIndexChanged;
        layout.Controls.Add(_actionCombo);

        _actionDescription = new Label
        {
            Text = "",
            ForeColor = TrayStyle.Muted,
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        layout.Controls.Add(_actionDescription);

        _estimatedWaitLabel = new Label
        {
            Text = "",
            ForeColor = TrayStyle.Success,
            Font = new Font("Segoe UI", 8.6f, FontStyle.Regular),
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        layout.Controls.Add(_estimatedWaitLabel);

        // Dynamic parameters panel
        layout.Controls.Add(TrayStyle.FieldLabel("Parameters"));
        _parametersPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = S(170),
            AutoScroll = true,
            BackColor = TrayStyle.SubtleSurface,
            BorderStyle = BorderStyle.FixedSingle,
        };
        layout.Controls.Add(_parametersPanel);

        // Justification
        layout.Controls.Add(TrayStyle.FieldLabel("Justification"));
        _justificationBox = new TextBox { Multiline = true, Height = S(74), Dock = DockStyle.Fill };
        TrayStyle.StyleTextBox(_justificationBox);
        layout.Controls.Add(_justificationBox);

        // Ticket reference
        layout.Controls.Add(TrayStyle.FieldLabel("Ticket or change reference"));
        _ticketBox = new TextBox { Dock = DockStyle.Fill };
        TrayStyle.StyleTextBox(_ticketBox);
        layout.Controls.Add(_ticketBox);

        // Status label
        _statusLabel = new Label { Text = "", ForeColor = TrayStyle.Danger, Dock = DockStyle.Fill, AutoSize = true };
        layout.Controls.Add(_statusLabel);

        // Buttons
        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, S(8), 0, 0) };
        _cancelButton = TrayStyle.Button("Cancel", S(88), primary: false);
        _cancelButton.Click += (_, _) => Close();
        _submitButton = TrayStyle.Button("Submit request", S(140), primary: true);
        _submitButton.Enabled = false;
        _submitButton.Click += SubmitButton_Click;
        buttonPanel.Controls.Add(_cancelButton);
        buttonPanel.Controls.Add(_submitButton);
        layout.Controls.Add(buttonPanel);

        Load += async (_, _) => await LoadActionsAsync();
    }

    private async Task LoadActionsAsync()
    {
        _statusLabel.Text = "Loading actions from server...";
        _statusLabel.ForeColor = TrayStyle.Muted;
        try
        {
            _actions = await _client.GetActionsAsync();
            _actionCombo.Items.Clear();
            foreach (var a in _actions)
                _actionCombo.Items.Add(a.DisplayName);

            if (_actionCombo.Items.Count > 0)
                _actionCombo.SelectedIndex = 0;

            ApplyContextPath();
            _statusLabel.Text = "";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Could not load actions: {FriendlyError(ex, "Check your server connection and try again.")}";
            _statusLabel.ForeColor = TrayStyle.Danger;
        }
    }

    private void ActionCombo_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_actionCombo.SelectedIndex < 0 || _actionCombo.SelectedIndex >= _actions.Count) return;

        _selectedAction = _actions[_actionCombo.SelectedIndex];
        _actionDescription.Text = _selectedAction.Description;
        _estimatedWaitLabel.Text = FormatApprovalEstimate(_selectedAction);
        BuildParameterControls(_selectedAction);
        _submitButton.Enabled = true;
    }

    private void BuildParameterControls(ActionDefinitionDto action)
    {
        _parametersPanel.Controls.Clear();
        _parameterControls.Clear();

        var y = 0;
        foreach (var param in action.Parameters)
        {
            var label = new Label
            {
                Text = param.DisplayName + (param.Required ? " *" : ""),
                Left = S(10), Top = y + S(4), Width = S(190), Height = S(22),
                ForeColor = TrayStyle.Text,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Bold, GraphicsUnit.Point),
            };
            var tooltip = new ToolTip();
            tooltip.SetToolTip(label, param.Description);

            Control input;
            if (param.Type == ParameterType.Boolean)
            {
                var check = new CheckBox
                {
                    Left = S(210),
                    Top = y + S(2),
                    Width = S(320),
                    Text = param.Description,
                    ForeColor = TrayStyle.Text,
                };
                input = check;
            }
            else
            {
                var box = new TextBox { Left = S(210), Top = y, Width = S(320), Text = param.DefaultValue ?? "" };
                TrayStyle.StyleTextBox(box);
                input = box;
            }

            _parametersPanel.Controls.Add(label);
            _parametersPanel.Controls.Add(input);
            _parameterControls[param.Key] = input;
            y += S(34);
        }

        _parametersPanel.Height = Math.Min(y + S(14), S(210));
    }

    // Pre-fill path parameters when launched from Windows right-click context menu
    private void ApplyContextPath()
    {
        if (string.IsNullOrEmpty(_contextPath)) return;

        // Fill the first FilePath-type parameter (or any key containing "Path" or "Source")
        foreach (var (key, ctrl) in _parameterControls)
        {
            if (ctrl is TextBox tb && (
                key.Contains("Source", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("Path", StringComparison.OrdinalIgnoreCase)))
            {
                tb.Text = _contextPath;
                break;
            }
        }

        // Also set justification hint
        if (string.IsNullOrWhiteSpace(_justificationBox.Text))
            _justificationBox.Text = $"Elevation requested via context menu for: {_contextPath}";
    }

    private async void SubmitButton_Click(object? sender, EventArgs e)
    {
        _statusLabel.ForeColor = TrayStyle.Danger;

        if (string.IsNullOrWhiteSpace(_justificationBox.Text))
        {
            _statusLabel.Text = "Justification is required.";
            return;
        }

        if (_selectedAction is null)
        {
            _statusLabel.Text = "Please select an action.";
            return;
        }

        _submitButton.Enabled = false;
        _statusLabel.Text = "Submitting...";
        _statusLabel.ForeColor = TrayStyle.Muted;

        var parameters = new Dictionary<string, string>();
        foreach (var (key, ctrl) in _parameterControls)
        {
            parameters[key] = ctrl is CheckBox cb
                ? cb.Checked.ToString().ToLowerInvariant()
                : ((TextBox)ctrl).Text.Trim();
        }

        var dto = new SubmitElevationRequestDto
        {
            ActionDefinitionId = _selectedAction.Id,
            Justification = _justificationBox.Text.Trim(),
            TicketReference = _ticketBox.Text.Trim().Length > 0 ? _ticketBox.Text.Trim() : null,
            Parameters = parameters,
        };

        try
        {
            var (upn, displayName) = GetCurrentUserInfo();
            var result = await _client.SubmitRequestAsync(dto, upn, displayName);

            var estimate = FormatApprovalEstimate(_selectedAction);
            var msg = $"Request submitted successfully.\n\nID: {result.Id}\nStatus: {result.Status}";
            if (!string.IsNullOrEmpty(estimate))
                msg += $"\n{estimate}";
            msg += "\n\nYour approver will be notified. You can track the status in the Raizen tray icon.";

            MessageBox.Show(this, msg, "Request Submitted",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Submission failed: {FriendlyError(ex, "Check your server connection and try again.")}";
            _statusLabel.ForeColor = TrayStyle.Danger;
            _submitButton.Enabled = true;
        }
    }

    private static string FriendlyError(Exception ex, string fallback) => ex switch
    {
        ArgumentException or InvalidOperationException or UnauthorizedAccessException => ex.Message,
        _ => fallback
    };

    internal static string FormatApprovalEstimate(ActionDefinitionDto action)
    {
        if (action.AutoApprove) return "This action is auto-approved";
        if (!action.AverageApprovalSeconds.HasValue) return "";
        var seconds = action.AverageApprovalSeconds.Value;
        if (seconds > 3600) return "Avg. approval time: > 1 hour";
        var minutes = (int)Math.Round(seconds / 60.0);
        return minutes < 1
            ? "Avg. approval time: < 1 min"
            : $"Avg. approval time: ~{minutes} min";
    }

    private static (string upn, string displayName) GetCurrentUserInfo()
    {
        try
        {
            using var ctx = new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.Current;
            if (user != null)
                return (user.UserPrincipalName ?? Environment.UserName,
                        user.DisplayName ?? Environment.UserName);
        }
        catch { }

        return (Environment.UserName, Environment.UserName);
    }
}
