using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// Lets a user request an elevated file copy or move (CopyFile action) from a right-click context menu.
/// Source is pre-filled from the selected file; user picks a destination folder and Copy vs Move.
/// </summary>
public sealed class FileTransferForm : RaizenFormBase
{
    private readonly ServerClient _client;
    private readonly string       _sourcePath;

    private readonly TextBox     _destBox;
    private readonly TextBox     _justBox;
    private readonly Button      _submitBtn;
    private readonly Button      _cancelBtn;
    private readonly Label       _statusLabel;
    private readonly RadioButton _copyRadio;
    private readonly RadioButton _moveRadio;

    public FileTransferForm(ServerClient client, string sourcePath)
    {
        _client     = client;
        _sourcePath = sourcePath;

        Text       = "Raizen | Request File Transfer";
        ClientSize = new Size(S(420), S(500));

        SuspendLayout();

        var header = BuildHeader("Request File Transfer",
                                 "Requires approver sign-off via the Raizen portal");
        var body = BuildBody(out _destBox, out _justBox, out _submitBtn, out _cancelBtn,
                             out _statusLabel, out _copyRadio, out _moveRadio);

        Controls.Add(header);
        Controls.Add(body);
        ResumeLayout(true);
        ApplyRoundedCorners();
    }

    private Panel BuildBody(out TextBox destBox, out TextBox justBox,
                            out Button submitBtn, out Button cancelBtn, out Label statusLabel,
                            out RadioButton copyRadio, out RadioButton moveRadio)
    {
        const int L  = 24;
        const int W  = 372;
        const int FW = 420;

        var body = new Panel { Location = new Point(0, S(76)), Size = new Size(S(FW), S(424)), BackColor = Color.White };
        int y = 20;

        // Source chip
        var sourceChipLabel = new Label
        {
            AutoSize  = true,
            Text      = $"{(Directory.Exists(_sourcePath) ? "\ud83d\udcc1" : "\ud83d\udcc4")}  {Path.GetFileName(_sourcePath)}",
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = NavyChipFg,
        };
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        var chipW = (int)g.MeasureString(sourceChipLabel.Text, sourceChipLabel.Font).Width + S(24);
        var sourceChip = new Panel { Location = new Point(S(L), S(y)), Size = new Size(Math.Min(chipW, S(W)), S(28)), BackColor = NavyChip };
        sourceChipLabel.Location = new Point(S(10), S(5));
        sourceChip.Controls.Add(sourceChipLabel);
        body.Controls.Add(sourceChip);
        y += 38;

        // Source full path
        body.Controls.Add(new Label
        {
            Text      = _sourcePath.Length > 58 ? "\u2026" + _sourcePath[^55..] : _sourcePath,
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(18)),
            Font      = new Font("Segoe UI", 8f),
            ForeColor = TextMuted,
        });
        y += 26;

        // Separator
        body.Controls.Add(new Panel { Location = new Point(S(L), S(y)), Size = new Size(S(W), S(1)), BackColor = SepColor });
        y += 14;

        // Destination label
        body.Controls.Add(new Label
        {
            Text      = "Destination folder *",
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(20)),
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = TextPrimary,
        });
        y += 24;

        // Destination textbox + Browse button
        const int browseW = 72;
        const int gap     = 6;
        destBox = new TextBox
        {
            Location        = new Point(S(L), S(y)),
            Size            = new Size(S(W - browseW - gap), S(26)),
            Font            = new Font("Segoe UI", 9.5f),
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = @"e.g. C:\Temp or \\server\share",
        };
        body.Controls.Add(destBox);
        var destBoxCapture = destBox;

        var browseBtn = new Button
        {
            Text      = "Browse\u2026",
            Location  = new Point(S(L + W - browseW), S(y)),
            Size      = new Size(S(browseW), S(26)),
            FlatStyle = FlatStyle.Flat,
            BackColor = BgGray,
            ForeColor = TextPrimary,
            Font      = new Font("Segoe UI", 8.5f),
            Cursor    = Cursors.Hand,
        };
        browseBtn.FlatAppearance.BorderSize  = 1;
        browseBtn.FlatAppearance.BorderColor = BorderColor;
        browseBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 244, 246);
        browseBtn.Click += async (_, _) =>
        {
            var selected = await Task.Run(() =>
            {
                string? result = null;
                var t = new Thread(() =>
                {
                    using var dlg = new FolderBrowserDialog
                    {
                        Description            = "Select destination folder",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton    = true,
                        AutoUpgradeEnabled     = true,
                    };
                    if (dlg.ShowDialog() == DialogResult.OK)
                        result = dlg.SelectedPath;
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                return result;
            });

            if (selected != null)
                destBoxCapture.Text = selected;
        };
        body.Controls.Add(browseBtn);
        y += 36;

        // Operation selector: Copy / Move
        body.Controls.Add(new Label
        {
            Text      = "Operation *",
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(20)),
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = TextPrimary,
        });
        y += 24;

        var opPanel = new Panel
        {
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(34)),
            BackColor = Color.FromArgb(243, 244, 246),
        };
        opPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(BorderColor);
            pe.Graphics.DrawRectangle(pen, 0, 0, opPanel.Width - 1, opPanel.Height - 1);
        };

        copyRadio = MakeOpRadio("\ud83d\udccb  Copy  (keep original)",   checked_: true,  left: 0,        width: S(W / 2),     height: S(34));
        moveRadio = MakeOpRadio("\u2702\ufe0f  Move  (remove original)", checked_: false, left: S(W / 2), width: S(W - W / 2), height: S(34));

        var copyCapture = copyRadio;
        var moveCapture = moveRadio;

        void UpdateOpColors(object? s, EventArgs ev)
        {
            copyCapture.BackColor = copyCapture.Checked ? NavyHeader : Color.FromArgb(243, 244, 246);
            copyCapture.ForeColor = copyCapture.Checked ? Color.White : TextPrimary;
            moveCapture.BackColor = moveCapture.Checked ? NavyHeader : Color.FromArgb(243, 244, 246);
            moveCapture.ForeColor = moveCapture.Checked ? Color.White : TextPrimary;
        }
        copyCapture.CheckedChanged += UpdateOpColors;
        moveCapture.CheckedChanged += UpdateOpColors;
        UpdateOpColors(null, EventArgs.Empty);

        opPanel.Controls.Add(copyRadio);
        opPanel.Controls.Add(moveRadio);
        body.Controls.Add(opPanel);
        y += 44;

        // Separator
        body.Controls.Add(new Panel { Location = new Point(S(L), S(y)), Size = new Size(S(W), S(1)), BackColor = SepColor });
        y += 14;

        // Justification
        body.Controls.Add(new Label
        {
            Text      = "Reason for requesting this transfer *",
            Location  = new Point(S(L), S(y)),
            Size      = new Size(S(W), S(20)),
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = TextPrimary,
        });
        y += 24;

        justBox = new TextBox
        {
            Multiline       = true,
            Location        = new Point(S(L), S(y)),
            Size            = new Size(S(W), S(72)),
            Font            = new Font("Segoe UI", 9.5f),
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. Copying driver update to local machine for installation",
            MaxLength       = 500,
            AcceptsReturn   = true,
            ScrollBars      = ScrollBars.Vertical,
        };
        body.Controls.Add(justBox);
        y += 82;

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

    private static RadioButton MakeOpRadio(string text, bool checked_, int left, int width, int height) =>
        new RadioButton
        {
            Text      = text,
            Checked   = checked_,
            Location  = new Point(left, 0),
            Size      = new Size(width, height),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(243, 244, 246),
            ForeColor = Color.FromArgb(28, 25, 23),
            Font      = new Font("Segoe UI", 8.5f),
            Cursor    = Cursors.Hand,
            Appearance = Appearance.Button,
            TextAlign  = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
        };

    private async void SubmitBtn_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_destBox.Text))
        {
            SetStatus(_statusLabel, "Please enter or browse to a destination folder.", error: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_justBox.Text))
        {
            SetStatus(_statusLabel, "Please enter a reason for this request.", error: true);
            return;
        }

        _submitBtn.Enabled = false;
        _cancelBtn.Enabled = false;
        _destBox.Enabled   = false;
        _justBox.Enabled   = false;
        SetStatus(_statusLabel, "Submitting request\u2026");

        var operation = _moveRadio.Checked ? "Move" : "Copy";

        try
        {
            var actions = await _client.GetActionsAsync();
            var action  = actions.FirstOrDefault(a => a.ActionType == ActionType.CopyFile);

            if (action is null)
            {
                SetStatus(_statusLabel, "No 'Copy File' action is configured in the catalog. Contact your Raizen admin.", error: true);
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
                    ["SourcePath"]      = _sourcePath,
                    ["DestinationPath"] = _destBox.Text.Trim(),
                    ["Operation"]       = operation,
                },
            }, upn, display);

            var estimate = RequestForm.FormatApprovalEstimate(action);
            var statusMsg = $"Request submitted (ID: {result.Id.ToString()[..8]}\u2026). Waiting for approver.";
            if (!string.IsNullOrEmpty(estimate)) statusMsg += $" {estimate}.";
            SetStatus(_statusLabel, statusMsg);
            _submitBtn.Text      = "\u2713  Request Sent";
            _submitBtn.Enabled   = true;
            _submitBtn.BackColor = SuccessGreen;
            _submitBtn.FlatAppearance.MouseOverBackColor = SuccessGreenHover;
            _submitBtn.Click -= SubmitBtn_Click;
            _submitBtn.Click += (_, _) => Close();
            _cancelBtn.Text   = "Close";
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
        _destBox.Enabled   = true;
        _justBox.Enabled   = true;
    }
}
