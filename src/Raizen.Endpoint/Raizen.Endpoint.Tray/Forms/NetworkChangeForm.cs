using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// Lets a user request a network adapter configuration change (static IPv4 or DHCP revert).
/// Submitted as a brokered elevation request; an approver must review before the
/// endpoint service (SYSTEM) applies the change via WMI.
/// </summary>
public sealed class NetworkChangeForm : RaizenFormBase
{
    // ── Layout constants (design-time 96 DPI) ────────────────────────────────
    private const int HeaderH       = 76;
    private const int FormW         = 420;
    private const int BodyL         = 24;
    private const int BodyW         = 372;
    private const int StaticPanelTop = 182;
    private const int StaticPanelH  = 328;
    private const int LowerSectionH = 198;

    private readonly ServerClient _client;

    private record AdapterInfo(string Name, string Guid, string CurrentIp, string CurrentSubnet, string CurrentGateway);
    private List<AdapterInfo> _adapters = new();

    private Panel _body = null!;

    private ComboBox _adapterCombo   = null!;
    private Label    _currentInfo    = null!;
    private Button   _btnStatic      = null!;
    private Button   _btnDhcp        = null!;
    private Panel    _staticPanel    = null!;
    private TextBox  _ipBox          = null!;
    private TextBox  _subnetBox      = null!;
    private TextBox  _gatewayBox     = null!;
    private CheckBox _keepDnsCheck   = null!;
    private TextBox  _primaryDnsBox  = null!;
    private TextBox  _secondaryDnsBox = null!;
    private Panel    _lowerSep       = null!;
    private Label    _lowerJustLbl   = null!;
    private TextBox  _justBox        = null!;
    private Label    _statusLabel    = null!;
    private Button   _submitBtn      = null!;
    private Button   _cancelBtn      = null!;

    private bool _isStatic = true;

    public NetworkChangeForm(ServerClient client)
    {
        _client = client;

        Text       = "Raizen | Request Network Change";
        ClientSize = new Size(S(FormW), S(HeaderH + StaticPanelTop + StaticPanelH + LowerSectionH));
        ShowInTaskbar = true;

        SuspendLayout();

        Controls.Add(BuildHeader("Request Network Change",
            "Select an adapter and configure IPv4 settings"));
        Controls.Add(BuildBody());
        ResumeLayout(true);
        ApplyRoundedCorners();

        SelectMode(isStatic: true);
        Load += (_, _) => LoadAdapters();
    }

    private Panel BuildBody()
    {
        _body = new Panel
        {
            Location  = new Point(0, S(HeaderH)),
            Size      = new Size(S(FormW), S(StaticPanelTop + StaticPanelH + LowerSectionH)),
            BackColor = Color.White,
        };

        int y = 20;

        // Adapter selector
        _body.Controls.Add(BoldLabel("Network Adapter *", BodyL, y)); y += 22;

        _adapterCombo = new ComboBox
        {
            Location      = new Point(S(BodyL), S(y)),
            Size          = new Size(S(BodyW), S(26)),
            Font          = Font,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle     = FlatStyle.Flat,
        };
        _adapterCombo.Items.Add("Loading adapters\u2026");
        _adapterCombo.SelectedIndex = 0;
        _adapterCombo.SelectedIndexChanged += OnAdapterChanged;
        _body.Controls.Add(_adapterCombo);
        y += 32;

        _currentInfo = new Label
        {
            Location  = new Point(S(BodyL), S(y)),
            Size      = new Size(S(BodyW), S(18)),
            Font      = new Font("Segoe UI", 8f),
            ForeColor = TextMuted,
            Text      = "Select an adapter to see its current configuration",
        };
        _body.Controls.Add(_currentInfo);
        y += 26;

        _body.Controls.Add(Sep(BodyL, y, BodyW)); y += 14;

        // Configuration mode
        _body.Controls.Add(BoldLabel("Configuration Mode *", BodyL, y)); y += 22;

        int modeW = BodyW / 2;
        var modePanel = new Panel
        {
            Location  = new Point(S(BodyL), S(y)),
            Size      = new Size(S(BodyW), S(36)),
            BackColor = Color.FromArgb(243, 244, 246),
        };
        modePanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(BorderColor);
            pe.Graphics.DrawRectangle(pen, 0, 0, modePanel.Width - 1, modePanel.Height - 1);
        };

        _btnStatic = MakeSegmentButton("Set Static IP", modeW);
        _btnStatic.Location = new Point(0, 0);
        _btnDhcp   = MakeSegmentButton("Use DHCP", BodyW - modeW);
        _btnDhcp.Location = new Point(S(modeW), 0);

        _btnStatic.Click += (_, _) => SelectMode(isStatic: true);
        _btnDhcp.Click   += (_, _) => SelectMode(isStatic: false);

        modePanel.Controls.Add(_btnStatic);
        modePanel.Controls.Add(_btnDhcp);
        _body.Controls.Add(modePanel);
        y += 46;

        // Static IP fields + DNS
        _staticPanel = new Panel
        {
            Location  = new Point(0, S(y)),
            Size      = new Size(S(FormW), S(StaticPanelH)),
            BackColor = Color.White,
        };

        int sy = 0;

        _staticPanel.Controls.Add(BoldLabel("IP Address *", BodyL, sy)); sy += 22;
        _ipBox = new TextBox
        {
            Location        = new Point(S(BodyL), S(sy)),
            Size            = new Size(S(BodyW), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. 192.168.1.100",
        };
        _ipBox.TextChanged += (_, _) => UpdateSubmitState();
        _staticPanel.Controls.Add(_ipBox);
        sy += 32;

        _staticPanel.Controls.Add(BoldLabel("Subnet Mask *", BodyL, sy)); sy += 22;
        _subnetBox = new TextBox
        {
            Location        = new Point(S(BodyL), S(sy)),
            Size            = new Size(S(BodyW), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. 255.255.255.0",
        };
        _subnetBox.TextChanged += (_, _) => UpdateSubmitState();
        _staticPanel.Controls.Add(_subnetBox);
        sy += 32;

        _staticPanel.Controls.Add(BoldLabel("Default Gateway (optional)", BodyL, sy)); sy += 22;
        _gatewayBox = new TextBox
        {
            Location        = new Point(S(BodyL), S(sy)),
            Size            = new Size(S(BodyW), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. 192.168.1.1",
        };
        _gatewayBox.TextChanged += (_, _) => UpdateSubmitState();
        _staticPanel.Controls.Add(_gatewayBox);
        sy += 32;

        // DNS section
        _staticPanel.Controls.Add(Sep(BodyL, sy, BodyW)); sy += 14;
        _staticPanel.Controls.Add(BoldLabel("DNS Servers", BodyL, sy)); sy += 22;

        _keepDnsCheck = new CheckBox
        {
            Text      = "Use current DNS automatically",
            Location  = new Point(S(BodyL), S(sy)),
            Size      = new Size(S(BodyW), S(22)),
            Font      = Font,
            Checked   = true,
            ForeColor = TextPrimary,
        };
        _keepDnsCheck.CheckedChanged += OnKeepDnsChanged;
        _staticPanel.Controls.Add(_keepDnsCheck);
        sy += 28;

        _staticPanel.Controls.Add(BoldLabel("Primary DNS *", BodyL, sy)); sy += 22;
        _primaryDnsBox = new TextBox
        {
            Location        = new Point(S(BodyL), S(sy)),
            Size            = new Size(S(BodyW), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. 8.8.8.8",
            Enabled         = false,
        };
        _primaryDnsBox.TextChanged += (_, _) => UpdateSubmitState();
        _staticPanel.Controls.Add(_primaryDnsBox);
        sy += 32;

        _staticPanel.Controls.Add(BoldLabel("Secondary DNS (optional)", BodyL, sy)); sy += 22;
        _secondaryDnsBox = new TextBox
        {
            Location        = new Point(S(BodyL), S(sy)),
            Size            = new Size(S(BodyW), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. 8.8.4.4",
            Enabled         = false,
        };
        _secondaryDnsBox.TextChanged += (_, _) => UpdateSubmitState();
        _staticPanel.Controls.Add(_secondaryDnsBox);

        _body.Controls.Add(_staticPanel);
        y += StaticPanelH;

        // Lower section
        _lowerSep = Sep(BodyL, y, BodyW);
        _body.Controls.Add(_lowerSep); y += 14;

        _lowerJustLbl = BoldLabel("Reason for this request *", BodyL, y);
        _body.Controls.Add(_lowerJustLbl); y += 22;

        _justBox = new TextBox
        {
            Multiline       = true,
            Location        = new Point(S(BodyL), S(y)),
            Size            = new Size(S(BodyW), S(68)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. Need a static IP for a development server",
            MaxLength       = 500,
            AcceptsReturn   = true,
            ScrollBars      = ScrollBars.Vertical,
        };
        _justBox.TextChanged += (_, _) => UpdateSubmitState();
        _body.Controls.Add(_justBox);
        y += 78;

        _statusLabel = new Label
        {
            Location  = new Point(S(BodyL), S(y)),
            Size      = new Size(S(BodyW), S(18)),
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = TextMuted,
        };
        _body.Controls.Add(_statusLabel);
        y += 28;

        _cancelBtn = MakeButton("Cancel", 82, primary: false);
        _cancelBtn.Location = new Point(S(FormW - BodyL - 82 - 8 - 156), S(y));
        _cancelBtn.Click   += (_, _) => Close();

        _submitBtn = MakeButton("Submit Request", 156, primary: true);
        _submitBtn.Location = new Point(S(FormW - BodyL - 156), S(y));
        _submitBtn.Enabled  = false;
        _submitBtn.Click   += SubmitBtn_Click;

        _body.Controls.Add(_cancelBtn);
        _body.Controls.Add(_submitBtn);

        return _body;
    }

    // ── Adapter loading ─────────────────────────────────────────────────────

    private void LoadAdapters()
    {
        _adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni =>
                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                !ni.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) &&
                !ni.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            .Select(ni =>
            {
                var props = ni.GetIPProperties();
                var ipv4  = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                var gw    = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                return new AdapterInfo(
                    Name:           ni.Name,
                    Guid:           ni.Id,
                    CurrentIp:      ipv4?.Address.ToString() ?? "",
                    CurrentSubnet:  ipv4?.IPv4Mask.ToString() ?? "",
                    CurrentGateway: gw?.Address.ToString() ?? "");
            })
            .ToList();

        _adapterCombo.SelectedIndexChanged -= OnAdapterChanged;
        _adapterCombo.Items.Clear();

        if (_adapters.Count == 0)
        {
            _adapterCombo.Items.Add("No eligible adapters found");
            _adapterCombo.SelectedIndex = 0;
            _currentInfo.Text = "No non-Bluetooth network adapters detected.";
        }
        else
        {
            foreach (var a in _adapters)
            {
                var ip = string.IsNullOrEmpty(a.CurrentIp) ? "No IP" : a.CurrentIp;
                _adapterCombo.Items.Add($"{a.Name}  |  {ip}");
            }
            _adapterCombo.SelectedIndex = 0;
            _adapterCombo.SelectedIndexChanged += OnAdapterChanged;
            OnAdapterChanged(null, EventArgs.Empty);
        }

        UpdateSubmitState();
    }

    private void OnAdapterChanged(object? sender, EventArgs e)
    {
        if (_adapterCombo.SelectedIndex < 0 || _adapterCombo.SelectedIndex >= _adapters.Count) return;

        var a = _adapters[_adapterCombo.SelectedIndex];

        if (string.IsNullOrEmpty(a.CurrentIp))
        {
            _currentInfo.Text = "Current: No IP configured";
        }
        else
        {
            var info = $"Current: {a.CurrentIp} / {a.CurrentSubnet}";
            if (!string.IsNullOrEmpty(a.CurrentGateway))
                info += $"  (Gateway: {a.CurrentGateway})";
            _currentInfo.Text = info;

            if (_isStatic && string.IsNullOrEmpty(_ipBox.Text))
            {
                _ipBox.Text      = a.CurrentIp;
                _subnetBox.Text  = a.CurrentSubnet;
                _gatewayBox.Text = a.CurrentGateway;
            }
        }

        UpdateSubmitState();
    }

    // ── Mode selector ───────────────────────────────────────────────────────

    private void SelectMode(bool isStatic)
    {
        _isStatic = isStatic;

        SetSegmentActive(isStatic ? _btnStatic : _btnDhcp, _btnStatic, _btnDhcp);

        if (_staticPanel != null)
            _staticPanel.Visible = isStatic;

        if (_lowerSep != null)
        {
            int scaledStaticPanelTop = _staticPanel?.Location.Y ?? S(StaticPanelTop);
            int lowerTop = isStatic ? _staticPanel!.Bottom : scaledStaticPanelTop;
            MoveLowerSection(lowerTop);
            ResizeForm(lowerTop);
        }

        UpdateSubmitState();
    }

    private void MoveLowerSection(int topY)
    {
        // topY is already in device pixels; spacing values also need device pixels
        int sp14 = S(14);
        int sp22 = S(22);
        int sp78 = S(78);
        int sp28 = S(28);

        int y = topY;
        _lowerSep.Location     = new Point(_lowerSep.Location.X, y);     y += sp14;
        _lowerJustLbl.Location = new Point(_lowerJustLbl.Location.X, y); y += sp22;
        _justBox.Location      = new Point(_justBox.Location.X, y);      y += sp78;
        _statusLabel.Location  = new Point(_statusLabel.Location.X, y);  y += sp28;
        _cancelBtn.Location    = new Point(_cancelBtn.Location.X, y);
        _submitBtn.Location    = new Point(_submitBtn.Location.X, y);
    }

    private void ResizeForm(int lowerTop)
    {
        int scaledLowerH  = S(LowerSectionH);
        int scaledHeaderH = S(HeaderH);
        int bodyH = lowerTop + scaledLowerH;
        _body.Height = bodyH;
        ClientSize   = new Size(ClientSize.Width, scaledHeaderH + bodyH);
        ApplyRoundedCorners();
    }

    // ── DNS toggle ──────────────────────────────────────────────────────────

    private void OnKeepDnsChanged(object? sender, EventArgs e)
    {
        bool custom = !_keepDnsCheck.Checked;
        _primaryDnsBox.Enabled   = custom;
        _secondaryDnsBox.Enabled = custom;
        if (!custom)
        {
            _primaryDnsBox.Text   = "";
            _secondaryDnsBox.Text = "";
        }
        UpdateSubmitState();
    }

    // ── Validation ──────────────────────────────────────────────────────────

    private static bool IsValidIpv4(string value) =>
        IPAddress.TryParse(value.Trim(), out var addr) &&
        addr.AddressFamily == AddressFamily.InterNetwork;

    private static bool IsValidSubnetMask(string value)
    {
        if (!IPAddress.TryParse(value.Trim(), out var addr) ||
            addr.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = addr.GetAddressBytes();
        uint val  = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        uint inv  = ~val;
        return (inv & (inv + 1)) == 0;
    }

    // ── Submit ──────────────────────────────────────────────────────────────

    private async void SubmitBtn_Click(object? sender, EventArgs e)
    {
        if (_adapters.Count == 0 || _adapterCombo.SelectedIndex < 0)
        { SetStatus(_statusLabel, "Please select a network adapter.", error: true); return; }

        if (_isStatic)
        {
            if (!IsValidIpv4(_ipBox.Text))
            { SetStatus(_statusLabel, "Enter a valid IPv4 address.", error: true); return; }
            if (!IsValidSubnetMask(_subnetBox.Text))
            { SetStatus(_statusLabel, "Enter a valid subnet mask (e.g. 255.255.255.0).", error: true); return; }
            if (!string.IsNullOrEmpty(_gatewayBox.Text.Trim()) && !IsValidIpv4(_gatewayBox.Text))
            { SetStatus(_statusLabel, "Default gateway must be a valid IPv4 address or left empty.", error: true); return; }
            if (!_keepDnsCheck.Checked && !IsValidIpv4(_primaryDnsBox.Text))
            { SetStatus(_statusLabel, "Primary DNS must be a valid IPv4 address.", error: true); return; }
            if (!_keepDnsCheck.Checked && !string.IsNullOrEmpty(_secondaryDnsBox.Text.Trim()) && !IsValidIpv4(_secondaryDnsBox.Text))
            { SetStatus(_statusLabel, "Secondary DNS must be a valid IPv4 address or left empty.", error: true); return; }
        }

        if (string.IsNullOrWhiteSpace(_justBox.Text))
        { SetStatus(_statusLabel, "Please enter a reason for this request.", error: true); return; }

        _submitBtn.Enabled = false;
        _cancelBtn.Enabled = false;
        _justBox.Enabled   = false;
        SetStatus(_statusLabel, "Submitting request\u2026");

        try
        {
            var actions = await _client.GetActionsAsync();
            var action  = actions.FirstOrDefault(a => a.ActionType == ActionType.SetNetworkConfiguration);

            if (action is null)
            {
                SetStatus(_statusLabel, "No 'Network Change' action is configured in the catalog. Contact your Raizen admin.", error: true);
                ReEnableControls();
                return;
            }

            var adapter = _adapters[_adapterCombo.SelectedIndex];
            var mode    = _isStatic ? "Static" : "DHCP";

            var parameters = new Dictionary<string, string>
            {
                ["AdapterName"] = adapter.Name,
                ["AdapterGuid"] = adapter.Guid,
                ["Mode"]        = mode,
            };

            if (_isStatic)
            {
                parameters["IpAddress"]  = _ipBox.Text.Trim();
                parameters["SubnetMask"] = _subnetBox.Text.Trim();

                var gw = _gatewayBox.Text.Trim();
                if (!string.IsNullOrEmpty(gw))
                    parameters["DefaultGateway"] = gw;

                if (_keepDnsCheck.Checked)
                {
                    parameters["DnsMode"] = "Auto";
                }
                else
                {
                    parameters["DnsMode"]    = "Custom";
                    parameters["PrimaryDns"] = _primaryDnsBox.Text.Trim();

                    var sec = _secondaryDnsBox.Text.Trim();
                    if (!string.IsNullOrEmpty(sec))
                        parameters["SecondaryDns"] = sec;
                }
            }

            var (upn, display) = GetCurrentUserInfo();
            var result = await _client.SubmitRequestAsync(new SubmitElevationRequestDto
            {
                ActionDefinitionId = action.Id,
                Justification      = _justBox.Text.Trim(),
                Parameters         = parameters,
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
        if (_submitBtn is null) return;

        bool adapterOk = _adapters.Count > 0 && _adapterCombo.SelectedIndex >= 0 && _adapterCombo.SelectedIndex < _adapters.Count;
        bool justOk    = !string.IsNullOrWhiteSpace(_justBox?.Text);
        bool ipOk      = !_isStatic || (IsValidIpv4(_ipBox?.Text ?? "") && IsValidSubnetMask(_subnetBox?.Text ?? ""));
        bool dnsOk     = !_isStatic || (_keepDnsCheck?.Checked ?? true) || IsValidIpv4(_primaryDnsBox?.Text ?? "");

        _submitBtn.Enabled = adapterOk && justOk && ipOk && dnsOk;
    }

    private void ReEnableControls()
    {
        _submitBtn.Enabled = true;
        _cancelBtn.Enabled = true;
        _justBox.Enabled   = true;
    }

    // ── OnShown ─────────────────────────────────────────────────────────────

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        SetForegroundWindow(Handle);
    }

    // ── Control factories ───────────────────────────────────────────────────

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
