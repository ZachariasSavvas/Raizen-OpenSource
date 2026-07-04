using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// WinForms tray form for submitting firewall rule elevation requests.
/// Collects rule name, action, direction, protocol, port, remote address,
/// and program path, then submits via the Raizen API.
/// </summary>
public sealed class FirewallForm : RaizenFormBase
{
    private readonly ServerClient _client;

    // Controls
    private readonly TextBox _ruleNameBox;
    private readonly Button _actionAllow, _actionBlock;
    private readonly Button _dirInbound, _dirOutbound;
    private readonly ComboBox _protocolBox;
    private readonly TextBox _localPortBox;
    private readonly TextBox _remoteAddrBox;
    private readonly TextBox _programBox;
    private readonly Button _browseBtn;
    private readonly CheckBox _enabledCheck;
    private readonly TextBox _justBox;
    private readonly Label _statusLabel;
    private readonly Button _submitBtn;
    private readonly Button _cancelBtn;

    // State
    private string _action = "Allow";
    private string _direction = "Inbound";

    public FirewallForm(ServerClient client)
    {
        _client = client;

        Text = "Manage Firewall Rule";
        ClientSize = new Size(S(420), S(620));
        MaximizeBox = false;
        MinimizeBox = false;

        var header = BuildHeader("Manage Firewall Rule", "Create or update a Windows Firewall rule");
        Controls.Add(header);

        int y = S(92);
        int leftPad = S(24);
        int fieldW = S(372);

        // ── Rule Name ────────────────────────────────────────────────────────
        Controls.Add(MakeLabel("Rule Name", leftPad, y));
        y += S(20);
        _ruleNameBox = MakeTextBox(leftPad, y, fieldW);
        Controls.Add(_ruleNameBox);
        y += S(38);

        // ── Action (Allow / Block) ──────────────────────────────────────────
        Controls.Add(MakeLabel("Action", leftPad, y));
        y += S(20);
        _actionAllow = MakeSegmentButton("Allow", 180);
        _actionAllow.Location = new Point(leftPad, y);
        _actionBlock = MakeSegmentButton("Block", 180);
        _actionBlock.Location = new Point(leftPad + S(186), y);
        SetSegmentActive(_actionAllow, _actionAllow, _actionBlock);
        _actionAllow.Click += (_, _) => { _action = "Allow"; SetSegmentActive(_actionAllow, _actionAllow, _actionBlock); };
        _actionBlock.Click += (_, _) => { _action = "Block"; SetSegmentActive(_actionBlock, _actionAllow, _actionBlock); };
        Controls.Add(_actionAllow);
        Controls.Add(_actionBlock);
        y += S(42);

        // ── Direction (Inbound / Outbound) ──────────────────────────────────
        Controls.Add(MakeLabel("Direction", leftPad, y));
        y += S(20);
        _dirInbound = MakeSegmentButton("Inbound", 180);
        _dirInbound.Location = new Point(leftPad, y);
        _dirOutbound = MakeSegmentButton("Outbound", 180);
        _dirOutbound.Location = new Point(leftPad + S(186), y);
        SetSegmentActive(_dirInbound, _dirInbound, _dirOutbound);
        _dirInbound.Click += (_, _) => { _direction = "Inbound"; SetSegmentActive(_dirInbound, _dirInbound, _dirOutbound); };
        _dirOutbound.Click += (_, _) => { _direction = "Outbound"; SetSegmentActive(_dirOutbound, _dirInbound, _dirOutbound); };
        Controls.Add(_dirInbound);
        Controls.Add(_dirOutbound);
        y += S(42);

        // ── Protocol ────────────────────────────────────────────────────────
        Controls.Add(MakeLabel("Protocol", leftPad, y));
        y += S(20);
        _protocolBox = new ComboBox
        {
            Location = new Point(leftPad, y),
            Size = new Size(S(140), S(28)),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Segoe UI", 9.5f),
        };
        _protocolBox.Items.AddRange(["TCP", "UDP", "Any"]);
        _protocolBox.SelectedIndex = 0;
        _protocolBox.SelectedIndexChanged += (_, _) => UpdatePortState();
        Controls.Add(_protocolBox);

        // Local Port (inline with protocol)
        var portLabel = MakeLabel("Local Port", leftPad + S(160), y - S(20));
        Controls.Add(portLabel);
        _localPortBox = MakeTextBox(leftPad + S(160), y, S(200));
        _localPortBox.PlaceholderText = "e.g. 1433, 8000-8100";
        Controls.Add(_localPortBox);
        y += S(38);

        // ── Remote Address ──────────────────────────────────────────────────
        Controls.Add(MakeLabel("Remote Address (optional)", leftPad, y));
        y += S(20);
        _remoteAddrBox = MakeTextBox(leftPad, y, fieldW);
        _remoteAddrBox.PlaceholderText = "e.g. 192.168.1.0/24  or  * for any";
        Controls.Add(_remoteAddrBox);
        y += S(38);

        // ── Program Path ────────────────────────────────────────────────────
        Controls.Add(MakeLabel("Program (optional)", leftPad, y));
        y += S(20);
        _programBox = MakeTextBox(leftPad, y, fieldW - S(80));
        _programBox.PlaceholderText = "e.g. C:\\Program Files\\App\\app.exe";
        Controls.Add(_programBox);
        _browseBtn = MakeNavyButton("Browse", 70, 30);
        _browseBtn.Location = new Point(leftPad + fieldW - S(72), y);
        _browseBtn.Click += OnBrowse;
        Controls.Add(_browseBtn);
        y += S(38);

        // ── Enabled checkbox ────────────────────────────────────────────────
        _enabledCheck = new CheckBox
        {
            Text = "Rule enabled",
            Checked = true,
            Location = new Point(leftPad, y),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f),
        };
        Controls.Add(_enabledCheck);
        y += S(30);

        // ── Separator ───────────────────────────────────────────────────────
        var sep = new Panel { Location = new Point(leftPad, y), Size = new Size(fieldW, 1), BackColor = SepColor };
        Controls.Add(sep);
        y += S(12);

        // ── Justification ───────────────────────────────────────────────────
        Controls.Add(MakeLabel("Justification", leftPad, y));
        y += S(20);
        _justBox = new TextBox
        {
            Location = new Point(leftPad, y),
            Size = new Size(fieldW, S(50)),
            Multiline = true,
            MaxLength = 500,
            Font = new Font("Segoe UI", 9.5f),
            BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(_justBox);
        y += S(58);

        // ── Status label ────────────────────────────────────────────────────
        _statusLabel = new Label
        {
            Location = new Point(leftPad, y),
            Size = new Size(fieldW, S(20)),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = TextMuted,
        };
        Controls.Add(_statusLabel);
        y += S(24);

        // ── Buttons ─────────────────────────────────────────────────────────
        _submitBtn = MakeButton("Submit Request", 160, true);
        _submitBtn.Location = new Point(leftPad, y);
        _submitBtn.Click += SubmitBtn_Click;
        Controls.Add(_submitBtn);

        _cancelBtn = MakeButton("Cancel", 100, false);
        _cancelBtn.Location = new Point(leftPad + S(170), y);
        _cancelBtn.Click += (_, _) => Close();
        Controls.Add(_cancelBtn);

        // Adjust form height
        ClientSize = new Size(ClientSize.Width, y + S(52));

        Load += (_, _) => ApplyRoundedCorners();
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
            Title = "Select Program",
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            _programBox.Text = dlg.FileName;
    }

    private void UpdatePortState()
    {
        var isAny = _protocolBox.SelectedItem?.ToString() == "Any";
        _localPortBox.Enabled = !isAny;
        if (isAny) _localPortBox.Text = "";
    }

    private async void SubmitBtn_Click(object? sender, EventArgs e)
    {
        // Validate
        if (string.IsNullOrWhiteSpace(_ruleNameBox.Text))
        {
            SetStatus(_statusLabel, "Rule name is required.", error: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_justBox.Text))
        {
            SetStatus(_statusLabel, "Justification is required.", error: true);
            return;
        }

        var protocol = _protocolBox.SelectedItem?.ToString() ?? "Any";
        if (!string.IsNullOrWhiteSpace(_localPortBox.Text) && protocol == "Any")
        {
            SetStatus(_statusLabel, "Port requires TCP or UDP protocol.", error: true);
            return;
        }

        _submitBtn.Enabled = false;
        SetStatus(_statusLabel, "Submitting...");

        try
        {
            var actions = await _client.GetActionsAsync();
            var action = actions.FirstOrDefault(a => a.ActionType == ActionType.SetFirewallRule);

            if (action is null)
            {
                SetStatus(_statusLabel, "No firewall action configured on server.", error: true);
                _submitBtn.Enabled = true;
                return;
            }

            var parameters = new Dictionary<string, string>
            {
                ["RuleName"] = _ruleNameBox.Text.Trim(),
                ["Action"] = _action,
                ["Direction"] = _direction,
                ["Protocol"] = protocol,
                ["Enabled"] = _enabledCheck.Checked.ToString().ToLowerInvariant(),
            };

            if (!string.IsNullOrWhiteSpace(_localPortBox.Text))
                parameters["LocalPort"] = _localPortBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(_remoteAddrBox.Text))
                parameters["RemoteAddress"] = _remoteAddrBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(_programBox.Text))
                parameters["Program"] = _programBox.Text.Trim();

            var (upn, display) = GetCurrentUserInfo();
            var result = await _client.SubmitRequestAsync(new SubmitElevationRequestDto
            {
                ActionDefinitionId = action.Id,
                Justification = _justBox.Text.Trim(),
                Parameters = parameters,
            }, upn, display);

            SetStatus(_statusLabel, $"Request submitted (ID: {result.Id.ToString()[..8]}...)");
            _submitBtn.BackColor = SuccessGreen;
        }
        catch (Exception ex)
        {
            SetStatus(_statusLabel, $"Error: {ex.Message}", error: true);
            _submitBtn.Enabled = true;
        }
    }

    // ── Helper factories ─────────────────────────────────────────────────────

    private Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        ForeColor = TextPrimary,
    };

    private TextBox MakeTextBox(int x, int y, int width) => new()
    {
        Location = new Point(x, y),
        Size = new Size(width, S(28)),
        Font = new Font("Segoe UI", 9.5f),
        BorderStyle = BorderStyle.FixedSingle,
    };
}
