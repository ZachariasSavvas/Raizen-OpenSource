using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// Modern borderless popup form that lets a user request
/// to run a specific executable with administrator privileges via Raizen.
///
/// Launched from the Windows right-click context menu:
///   Raizen > Request to Run as Admin
///
/// Flow:
///   1. User enters justification and clicks Submit.
///   2. The tray app submits an elevation request to the Raizen API.
///   3. An approver reviews the request in the web portal.
///   4. The endpoint service (SYSTEM) executes the binary in the user's session.
/// </summary>
public sealed class RunAsAdminForm : RaizenFormBase
{
    private readonly ServerClient _client;
    private readonly string       _exePath;

    private readonly TextBox _justBox;
    private readonly Button  _submitBtn;
    private readonly Button  _cancelBtn;
    private readonly Label   _statusLabel;

    public RunAsAdminForm(ServerClient client, string exePath)
    {
        _client  = client;
        _exePath = exePath;

        Text       = "Raizen | Request Elevation";
        ClientSize = new Size(S(420), S(370));

        SuspendLayout();

        var header = BuildHeader("Request to Run as Administrator",
                                 "Requires approver sign-off via the Raizen portal");
        var body = BuildBody(out _justBox, out _submitBtn, out _cancelBtn, out _statusLabel);

        Controls.Add(header);
        Controls.Add(body);
        ResumeLayout(true);
        ApplyRoundedCorners();
    }

    // ── Body ─────────────────────────────────────────────────────────────────
    private Panel BuildBody(out TextBox justBox, out Button submitBtn,
                            out Button cancelBtn, out Label statusLabel)
    {
        const int L  = 24;
        const int W  = 372;
        const int FW = 420;

        var body = new Panel
        {
            Location  = new Point(0, S(76)),
            Size      = new Size(S(FW), S(294)),
            BackColor = Color.White,
        };

        int y = 20;

        // ── Program chip ─────────────────────────────────────────────────────
        var chipLabel = new Label
        {
            AutoSize  = true,
            Text      = $"\u26a1  {Path.GetFileName(_exePath)}",
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = NavyChipFg,
            Cursor    = Cursors.Default,
        };

        using var g = Graphics.FromHwnd(IntPtr.Zero);
        var textSize = g.MeasureString(chipLabel.Text, chipLabel.Font);
        var chipW    = (int)textSize.Width + S(24);

        var chip = new Panel
        {
            Location  = new Point(S(L), S(y)),
            Size      = new Size(Math.Min(chipW, S(W)), S(28)),
            BackColor = NavyChip,
        };
        chipLabel.Location = new Point(S(10), S(5));
        chip.Controls.Add(chipLabel);
        body.Controls.Add(chip);
        y += 40;

        // ── Full path (muted, truncated) ─────────────────────────────────────
        body.Controls.Add(new Label
        {
            Text      = _exePath.Length > 58 ? "\u2026" + _exePath[^55..] : _exePath,
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(18)),
            Font      = new Font("Segoe UI", 8f),
            ForeColor = TextMuted,
        });
        y += 28;

        // ── Separator ────────────────────────────────────────────────────────
        body.Controls.Add(new Panel
        {
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(1)),
            BackColor = SepColor,
        });
        y += 14;

        // ── Justification label ──────────────────────────────────────────────
        body.Controls.Add(new Label
        {
            Text      = "Reason for requesting elevated access *",
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(20)),
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = TextPrimary,
        });
        y += 24;

        // ── Justification textbox ────────────────────────────────────────────
        justBox = new TextBox
        {
            Multiline       = true,
            Location        = new Point(S(L), S(y)),
            Size            = new Size(S(W), S(80)),
            Font            = new Font("Segoe UI", 9.5f),
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. Troubleshooting network adapter, need to run diagnostics tool",
            MaxLength       = 500,
            AcceptsReturn   = true,
            ScrollBars      = ScrollBars.Vertical,
        };
        body.Controls.Add(justBox);
        y += 90;

        // ── Status label ─────────────────────────────────────────────────────
        statusLabel = new Label
        {
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(20)),
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = TextMuted,
            Text      = "",
        };
        body.Controls.Add(statusLabel);
        y += 28;

        // ── Buttons ──────────────────────────────────────────────────────────
        cancelBtn = MakeButton("Cancel", 82, primary: false);
        cancelBtn.Location = new Point(S(FW - L - 82 - 8 - 156), S(y));
        cancelBtn.Click   += (_, _) => Close();

        submitBtn = MakeButton("Submit Request", 156, primary: true);
        submitBtn.Location = new Point(S(FW - L - 156), S(y));
        submitBtn.Click   += SubmitBtn_Click;

        body.Controls.Add(cancelBtn);
        body.Controls.Add(submitBtn);

        return body;
    }

    // ── Submit handler ───────────────────────────────────────────────────────
    private async void SubmitBtn_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_justBox.Text))
        {
            SetStatus(_statusLabel, "Please enter a reason for this request.", error: true);
            return;
        }

        _submitBtn.Enabled = false;
        _cancelBtn.Enabled = false;
        _justBox.Enabled   = false;
        SetStatus(_statusLabel, "Submitting request\u2026");

        try
        {
            var actions = await _client.GetActionsAsync();
            var action  = actions.FirstOrDefault(a => a.ActionType == ActionType.RunAsAdmin);

            if (action is null)
            {
                SetStatus(_statusLabel, "No 'RunAsAdmin' action is configured in the catalog. Contact your Raizen admin.", error: true);
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
                    ["ExecutablePath"] = _exePath,
                },
            }, upn, display);

            var estimate = RequestForm.FormatApprovalEstimate(action);
            var statusMsg = $"Request submitted (ID: {result.Id.ToString()[..8]}\u2026). Waiting for approver.";
            if (!string.IsNullOrEmpty(estimate))
                statusMsg += $" {estimate}.";
            SetStatus(_statusLabel, statusMsg);
            _submitBtn.Text     = "\u2713  Request Sent";
            _submitBtn.Enabled  = true;
            _submitBtn.BackColor = SuccessGreen;
            _submitBtn.FlatAppearance.MouseOverBackColor = SuccessGreenHover;
            _submitBtn.Click -= SubmitBtn_Click;
            _submitBtn.Click += (_, _) => Close();
            _cancelBtn.Text  = "Close";
            _cancelBtn.Enabled = true;
        }
        catch (Exception ex)
        {
            SetStatus(_statusLabel, $"Error: {ex.Message}", error: true);
            ReEnableControls();
        }
    }

    private void ReEnableControls()
    {
        _submitBtn.Enabled = true;
        _cancelBtn.Enabled = true;
        _justBox.Enabled   = true;
    }
}
