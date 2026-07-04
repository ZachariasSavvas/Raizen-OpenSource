namespace Raizen.Server.Setup.Pages;

public class ServerConfigPage : WizardPage
{
    private readonly WizardState _state;

    private TextBox _hostname      = null!;
    private TextBox _apiPort       = null!;
    private TextBox _webPort       = null!;
    private TextBox _encryptionKey = null!;

    public override string Title => "Server Configuration";

    public ServerConfigPage(WizardState state)
    {
        _state = state;
        BuildUi();
    }

    private void BuildUi()
    {
        BackColor = Color.White;

        var header = new Label
        {
            Text      = "Ports and URLs",
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 43, 75),
            AutoSize  = true,
            Location  = new Point(32, 24)
        };

        int y = 60;
        _hostname     = AddField("Server hostname / IP", _state.ServerHostname,        ref y);
        _apiPort      = AddField("API port",             _state.ApiPort.ToString(),    ref y);
        _webPort      = AddField("Web portal port",      _state.WebPort.ToString(),    ref y);

        var urlHint = new Label
        {
            Text      = "A self-signed TLS certificate will be generated for this hostname.\n" +
                        "Use the server's FQDN or IP address that endpoints will connect to.",
            Font      = new Font("Segoe UI", 8),
            ForeColor = Color.FromArgb(107, 114, 128),
            AutoSize  = false,
            Size      = new Size(360, 32),
            Location  = new Point(175, y)
        };
        Controls.Add(urlHint);
        y += 38;

        // Encryption key with Regenerate button on same row
        var keyLabel = new Label
        {
            Text      = "Admin encryption key",
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(55, 65, 81),
            AutoSize  = true,
            Location  = new Point(32, y + 4)
        };
        _encryptionKey = new TextBox
        {
            Text      = _state.EncryptionKey,
            Font      = new Font("Segoe UI", 9),
            Location  = new Point(175, y),
            Size      = new Size(228, 26)
        };
        var regenBtn = new Button
        {
            Text      = "Regenerate",
            Font      = new Font("Segoe UI", 8),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(107, 114, 128),
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(88, 26),
            Location  = new Point(410, y),
            Cursor    = Cursors.Hand
        };
        regenBtn.FlatAppearance.BorderSize = 0;
        regenBtn.Click += (_, _) => _encryptionKey.Text = WizardState.GenerateBase64Key(32);
        y += 38;

        var keyWarn = new Label
        {
            Text      = "Save this key — losing it means admin passwords cannot be decrypted.",
            Font      = new Font("Segoe UI", 8, FontStyle.Bold),
            ForeColor = Color.FromArgb(146, 64, 14),
            AutoSize  = false,
            Size      = new Size(360, 32),
            Location  = new Point(175, y)
        };
        y += 40;

        var deployHint = new Label
        {
            Text      = "The hostname is used in the Public API URL shown in the admin portal\n" +
                        "when downloading endpoint agent config (https://<hostname>:5001).",
            Font      = new Font("Segoe UI", 8),
            ForeColor = Color.FromArgb(107, 114, 128),
            AutoSize  = false,
            Size      = new Size(532, 32),
            Location  = new Point(32, y)
        };

        Controls.AddRange(new Control[]
        {
            header, keyLabel, _encryptionKey, regenBtn, keyWarn, deployHint
        });
    }

    private TextBox AddField(string label, string value, ref int y)
    {
        var lbl = new Label
        {
            Text      = label,
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(55, 65, 81),
            AutoSize  = true,
            Location  = new Point(32, y + 4)
        };
        var box = new TextBox
        {
            Text     = value,
            Font     = new Font("Segoe UI", 10),
            Location = new Point(175, y),
            Size     = new Size(320, 26)
        };
        Controls.Add(lbl);
        Controls.Add(box);
        y += 38;
        return box;
    }

    public override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(_hostname.Text))
            return "Server hostname or IP address is required.";
        if (!int.TryParse(_apiPort.Text, out var api) || api < 1 || api > 65535)
            return "API port must be a number between 1 and 65535.";
        if (!int.TryParse(_webPort.Text, out var web) || web < 1 || web > 65535)
            return "Web portal port must be a number between 1 and 65535.";
        if (api == web)
            return "API port and web portal port must be different.";
        if (string.IsNullOrWhiteSpace(_encryptionKey.Text) || _encryptionKey.Text.Length < 16)
            return "Encryption key must be at least 16 characters.";
        return null;
    }

    public override Task OnActivatedAsync()
    {
        _hostname.Text      = _state.ServerHostname;
        _apiPort.Text       = _state.ApiPort.ToString();
        _webPort.Text       = _state.WebPort.ToString();
        _encryptionKey.Text = _state.EncryptionKey;
        return Task.CompletedTask;
    }

    public void CommitToState()
    {
        _state.ServerHostname = _hostname.Text.Trim();
        _state.ApiPort        = int.Parse(_apiPort.Text);
        _state.WebPort        = int.Parse(_webPort.Text);
        _state.PublicApiUrl   = $"https://{_state.ServerHostname}:{_state.ApiPort}";
        _state.EncryptionKey  = _encryptionKey.Text.Trim();
    }
}
