using System.Security.Principal;
using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;
using System.DirectoryServices.AccountManagement;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// Lets a user request that the Raizen service (SYSTEM) applies a specific
/// NTFS permission change to a file or folder.
///
/// Launched from the Windows right-click context menu; the target path is
/// passed in via the initialPath parameter and shown read-only.
/// </summary>
public sealed class FilePermissionsForm : RaizenFormBase
{
    private readonly ServerClient _client;
    private readonly string       _targetPath;

    private TextBox _accountBox    = null!;
    private Button  _resolveBtn    = null!;
    private Label   _resolvedLabel = null!;
    private string? _canonicalAccount;
    private string  _permissionLevel = "FullControl";

    private Button _btnRead   = null!;
    private Button _btnReadEx = null!;
    private Button _btnFull   = null!;

    private TextBox _justBox     = null!;
    private Label   _statusLabel = null!;
    private Button  _submitBtn   = null!;
    private Button  _cancelBtn   = null!;

    public FilePermissionsForm(ServerClient client, string? initialPath = null)
    {
        _client     = client;
        _targetPath = initialPath ?? string.Empty;

        Text       = "Raizen | Set File / Folder Permissions";
        ClientSize = new Size(S(420), S(536));

        SuspendLayout();

        Controls.Add(BuildHeader("Request File / Folder Permissions",
            "SYSTEM will apply the permission change when approved"));
        Controls.Add(BuildBody());
        ResumeLayout(true);
        ApplyRoundedCorners();

        SelectPermission(_btnFull, "FullControl");
    }

    private Panel BuildBody()
    {
        const int L  = 24;
        const int W  = 372;
        const int FW = 420;

        var body = new Panel
        {
            Location  = new Point(0, S(76)),
            Size      = new Size(S(FW), S(460)),
            BackColor = Color.White,
        };

        int y = 20;

        // Target path chip
        var chipName  = string.IsNullOrEmpty(_targetPath) ? "(no target selected)" : Path.GetFileName(_targetPath);
        var chipLabel = new Label
        {
            AutoSize  = true,
            Text      = $"\ud83d\udd12  {chipName}",
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = NavyChipFg,
        };
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        var chipW = (int)g.MeasureString(chipLabel.Text, chipLabel.Font).Width + S(24);
        var chip  = new Panel { Location = new Point(S(L), S(y)), Size = new Size(Math.Min(chipW, S(W)), S(28)), BackColor = NavyChip };
        chipLabel.Location = new Point(S(10), S(5));
        chip.Controls.Add(chipLabel);
        body.Controls.Add(chip);
        y += 36;

        // Full path
        body.Controls.Add(new Label
        {
            Text      = _targetPath.Length > 58 ? "\u2026" + _targetPath[^55..] : _targetPath,
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(18)),
            Font      = new Font("Segoe UI", 8f),
            ForeColor = TextMuted,
        });
        y += 26;

        body.Controls.Add(Sep(L, y, W)); y += 14;

        // Account
        body.Controls.Add(BoldLabel("Account *", L, y)); y += 22;

        _accountBox = new TextBox
        {
            Location        = new Point(S(L), S(y)),
            Size            = new Size(S(W - 78), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "Type a username, e.g. john.smith",
        };
        _accountBox.TextChanged += (_, _) =>
        {
            _canonicalAccount = null;
            _resolvedLabel.Text = "";
            UpdateSubmitState();
        };
        _accountBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = ResolveAccountAsync(); }
        };

        _resolveBtn = MakeNavyButton("Resolve", 74, 26);
        _resolveBtn.Location = new Point(S(L + W - 74), S(y));
        _resolveBtn.Click += async (_, _) => await ResolveAccountAsync();

        body.Controls.Add(_accountBox);
        body.Controls.Add(_resolveBtn);
        y += 32;

        _resolvedLabel = new Label
        {
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(18)),
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = TextMuted,
        };
        body.Controls.Add(_resolvedLabel);
        y += 26;

        body.Controls.Add(Sep(L, y, W)); y += 14;

        // Access level
        body.Controls.Add(BoldLabel("Access Level *", L, y)); y += 22;

        int btnW = W / 3;
        var permPanel = new Panel
        {
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(36)),
            BackColor = Color.FromArgb(243, 244, 246),
        };
        permPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(BorderColor);
            pe.Graphics.DrawRectangle(pen, 0, 0, permPanel.Width - 1, permPanel.Height - 1);
        };

        _btnRead   = MakeSegmentButton("Read",            btnW);
        _btnRead.Location = new Point(0, 0);
        _btnReadEx = MakeSegmentButton("Read && Execute", btnW);
        _btnReadEx.Location = new Point(S(btnW), 0);
        _btnFull   = MakeSegmentButton("Full Access",     W - btnW * 2);
        _btnFull.Location = new Point(S(btnW * 2), 0);

        _btnRead.Click   += (_, _) => SelectPermission(_btnRead,   "Read");
        _btnReadEx.Click += (_, _) => SelectPermission(_btnReadEx, "ReadAndExecute");
        _btnFull.Click   += (_, _) => SelectPermission(_btnFull,   "FullControl");

        permPanel.Controls.Add(_btnRead);
        permPanel.Controls.Add(_btnReadEx);
        permPanel.Controls.Add(_btnFull);
        body.Controls.Add(permPanel);
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
            PlaceholderText = "e.g. Grant service account access to application folder",
            MaxLength       = 500,
            AcceptsReturn   = true,
            ScrollBars      = ScrollBars.Vertical,
        };
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

    // ── Permission selector ──────────────────────────────────────────────────

    private void SelectPermission(Button selected, string level)
    {
        _permissionLevel = level;
        SetSegmentActive(selected, _btnRead, _btnReadEx, _btnFull);
    }

    // ── Account resolution ───────────────────────────────────────────────────

    private async Task ResolveAccountAsync()
    {
        var input = _accountBox.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        _resolveBtn.Enabled      = false;
        _resolvedLabel.ForeColor = TextMuted;
        _resolvedLabel.Text      = "Resolving\u2026";
        _canonicalAccount        = null;
        UpdateSubmitState();

        var (canonical, displayName, error) = await Task.Run(() => ResolveAccountSync(input));

        _resolveBtn.Enabled = true;

        if (error != null)
        {
            _resolvedLabel.ForeColor = Red;
            _resolvedLabel.Text      = $"\u2717  {error}";
            _canonicalAccount        = null;
        }
        else
        {
            _resolvedLabel.ForeColor = Green;
            _resolvedLabel.Text = string.IsNullOrEmpty(displayName)
                ? $"\u2713  {canonical}"
                : $"\u2713  {canonical}  ({displayName})";
            _canonicalAccount = canonical;
        }

        UpdateSubmitState();
    }

    private static (string canonical, string? displayName, string? error) ResolveAccountSync(string input)
    {
        string sam = input.Contains('\\') ? input.Split('\\', 2)[1]
                   : input.Contains('@')  ? input.Split('@')[0]
                   : input;

        try
        {
            using var ctx  = new PrincipalContext(ContextType.Machine);
            using var user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, sam);
            if (user != null)
            {
                var ntAccount = (NTAccount)user.Sid.Translate(typeof(NTAccount));
                return (ntAccount.Value, user.DisplayName, null);
            }
        }
        catch { }

        try
        {
            using var ctx  = new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, sam);
            if (user != null)
            {
                var ntAccount = (NTAccount)user.Sid.Translate(typeof(NTAccount));
                return (ntAccount.Value, user.DisplayName, null);
            }
        }
        catch { }

        return ("", null, $"Account '{input}' not found locally or in the domain.");
    }

    // ── Submit ───────────────────────────────────────────────────────────────

    private async void SubmitBtn_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_targetPath))        { SetStatus(_statusLabel, "No target path; launch via right-click on a file or folder.", error: true); return; }
        if (_canonicalAccount is null)                { SetStatus(_statusLabel, "Resolve the account before submitting.", error: true); return; }
        if (string.IsNullOrWhiteSpace(_justBox.Text)) { SetStatus(_statusLabel, "Please enter a reason for this request.", error: true); return; }

        _submitBtn.Enabled = false;
        _cancelBtn.Enabled = false;
        _justBox.Enabled   = false;
        SetStatus(_statusLabel, "Submitting request\u2026");

        try
        {
            var actions = await _client.GetActionsAsync();
            var action  = actions.FirstOrDefault(a => a.ActionType == ActionType.OpenFileProperties);

            if (action is null)
            {
                SetStatus(_statusLabel, "No 'File Permissions' action is configured in the catalog. Contact your Raizen admin.", error: true);
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
                    ["FilePath"]        = _targetPath,
                    ["Account"]         = _canonicalAccount,
                    ["PermissionLevel"] = _permissionLevel,
                    ["ApplyTo"]         = "FolderSubfoldersAndFiles",
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
        _submitBtn.Enabled = _canonicalAccount is not null
                          && !string.IsNullOrEmpty(_targetPath);
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
