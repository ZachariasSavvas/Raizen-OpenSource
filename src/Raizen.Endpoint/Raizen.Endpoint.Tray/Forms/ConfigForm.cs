using Raizen.Endpoint.Shared.Config;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// Allows the user (or administrator) to change the Raizen server URL and other
/// connection settings stored in raizen-config.json.
///
/// Note: The API key field is read-only here; it is set during installation.
/// Changing the server URL takes effect immediately — the service will pick it up
/// on the next poll cycle via the FileSystemWatcher in ConfigLoader.
/// </summary>
public sealed class ConfigForm : Form
{
    private readonly ConfigLoader _configLoader;

    private readonly TextBox _serverUrlBox;
    private readonly TextBox _machineIdBox;
    private readonly TextBox _tlsThumbprintBox;
    private readonly NumericUpDown _pollIntervalSpinner;
    private readonly NumericUpDown _heartbeatIntervalSpinner;
    private readonly TextBox _configPathBox;
    private readonly Label _statusLabel;
    private readonly Button _saveButton;

    // ── DPI helper for dynamically-sized controls ──────────────────────────────
    private int S(int px) => (int)(px * (DeviceDpi / 96.0f));

    public ConfigForm(ConfigLoader configLoader)
    {
        _configLoader = configLoader;
        var config = configLoader.Current;

        AutoScaleMode       = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Text = "Raizen configuration";
        TrayStyle.ApplyDialog(this, S(560), S(470));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(S(18)),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(178)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        void AddRow(string label, Control ctrl)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TrayStyle.Text,
                Font = new Font("Segoe UI", 9.1f, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, S(4), S(10), S(4)),
            });
            ctrl.Margin = new Padding(0, S(4), 0, S(4));
            layout.Controls.Add(ctrl);
        }

        var title = TrayStyle.Title("Configuration");
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(title);
        var intro = TrayStyle.HelpText("Connection settings used by the tray and endpoint service.");
        layout.SetColumnSpan(intro, 2);
        layout.Controls.Add(intro);

        _serverUrlBox = new TextBox { Text = config.ServerUrl, Dock = DockStyle.Fill };
        TrayStyle.StyleTextBox(_serverUrlBox);
        AddRow("Server URL *", _serverUrlBox);

        var urlHint = new Label
        {
            Text = "Change this value whenever the Raizen server is moved to a new hostname or IP.",
            ForeColor = TrayStyle.Muted, Dock = DockStyle.Fill, AutoSize = true,
            Font = new Font("Segoe UI", 8.6f, FontStyle.Regular, GraphicsUnit.Point),
        };
        layout.SetColumnSpan(urlHint, 2);
        layout.Controls.Add(urlHint);

        _tlsThumbprintBox = new TextBox { Text = config.TlsPinThumbprint, Dock = DockStyle.Fill };
        TrayStyle.StyleTextBox(_tlsThumbprintBox);
        AddRow("TLS Pin (SHA-256)", _tlsThumbprintBox);

        _machineIdBox = new TextBox { Text = config.MachineId, Dock = DockStyle.Fill, ReadOnly = true, BackColor = TrayStyle.SubtleSurface };
        TrayStyle.StyleTextBox(_machineIdBox);
        AddRow("Machine ID (read-only)", _machineIdBox);

        _pollIntervalSpinner = new NumericUpDown { Minimum = 5, Maximum = 3600, Value = config.PollIntervalSeconds, Dock = DockStyle.Fill };
        AddRow("Poll interval (seconds)", _pollIntervalSpinner);

        _heartbeatIntervalSpinner = new NumericUpDown { Minimum = 60, Maximum = 86400, Value = config.HeartbeatIntervalSeconds, Dock = DockStyle.Fill };
        AddRow("Heartbeat interval (sec)", _heartbeatIntervalSpinner);

        _configPathBox = new TextBox { Text = ConfigLoader.ResolveConfigPath(), ReadOnly = true, Dock = DockStyle.Fill, BackColor = TrayStyle.SubtleSurface };
        TrayStyle.StyleTextBox(_configPathBox);
        AddRow("Config file path", _configPathBox);

        _statusLabel = new Label { Text = "", ForeColor = TrayStyle.Danger, Dock = DockStyle.Fill, AutoSize = true };
        layout.SetColumnSpan(_statusLabel, 2);
        layout.Controls.Add(_statusLabel);

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, S(8), 0, 0) };
        layout.SetColumnSpan(buttonPanel, 2);
        var cancelBtn = TrayStyle.Button("Cancel", S(88), primary: false);
        cancelBtn.Click += (_, _) => Close();
        _saveButton = TrayStyle.Button("Save and apply", S(128), primary: true);
        _saveButton.Click += SaveButton_Click;
        buttonPanel.Controls.Add(cancelBtn);
        buttonPanel.Controls.Add(_saveButton);
        layout.Controls.Add(buttonPanel);
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        var url = _serverUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            _statusLabel.Text = "Server URL must be a valid absolute URL.";
            return;
        }

        var current = _configLoader.Current;
        var updated = new RaizenEndpointConfig
        {
            ServerUrl = url,
            TlsPinThumbprint = _tlsThumbprintBox.Text.Trim(),
            MachineId = current.MachineId,
            ApiKey = current.ApiKey,
            PollIntervalSeconds = (int)_pollIntervalSpinner.Value,
            HeartbeatIntervalSeconds = (int)_heartbeatIntervalSpinner.Value,
            HttpProxy = current.HttpProxy,
            HttpTimeoutSeconds = current.HttpTimeoutSeconds,
        };

        try
        {
            _configLoader.Save(updated);
            _statusLabel.ForeColor = Color.Green;
            _statusLabel.Text = "Configuration saved. Changes take effect on the next poll cycle.";
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Red;
            _statusLabel.Text = $"Save failed: {ex.Message}";
        }
    }
}
