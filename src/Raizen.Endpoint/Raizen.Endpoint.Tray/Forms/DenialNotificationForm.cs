using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>Shows a denied request with the approver's reason and a reply path.</summary>
public sealed class DenialNotificationForm : Form
{
    private const int MaxCommentLength = 4000;

    private readonly ServerClient _client;
    private readonly ElevationRequestDto _request;
    private readonly TextBox _replyBox;
    private readonly Label _statusLabel;
    private readonly Button _sendButton;
    private bool _sending;

    private int S(int px) => (int)(px * (DeviceDpi / 96.0f));

    public DenialNotificationForm(ServerClient client, ElevationRequestDto request)
    {
        _client = client;
        _request = request;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = "Request denied";
        TopMost = true;
        TrayStyle.ApplyDialog(this, S(460), S(430));
        MinimumSize = new Size(S(420), S(390));
        AccessibleName = "Request denied";
        AccessibleDescription = "Shows the denial reason and lets the requester reply.";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(S(18)),
            RowCount = 6,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(82)));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        layout.Controls.Add(BuildHeader(request));
        layout.Controls.Add(BuildReasonBox(request));

        var replyLabel = TrayStyle.FieldLabel("Reply to approver");
        replyLabel.Margin = new Padding(0, S(12), 0, S(4));
        layout.Controls.Add(replyLabel);

        _replyBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            PlaceholderText = "Ask a follow-up or add the missing details...",
            MaxLength = MaxCommentLength,
            AccessibleName = "Reply comment",
        };
        TrayStyle.StyleTextBox(_replyBox);
        _replyBox.Margin = new Padding(0, 0, 0, S(8));
        _replyBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.SuppressKeyPress = true;
                await SendReplyAsync();
            }
        };
        layout.Controls.Add(_replyBox);

        _statusLabel = new Label
        {
            Text = "",
            AutoSize = true,
            ForeColor = TrayStyle.Muted,
            Margin = new Padding(0, 0, 0, S(10)),
            AccessibleName = "Reply status",
        };
        layout.Controls.Add(_statusLabel);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0),
        };

        var viewButton = TrayStyle.Button("Open Request", S(122), primary: false);
        viewButton.AccessibleName = "Open request";
        viewButton.Click += (_, _) => OpenRequest();

        _sendButton = TrayStyle.Button("Send Reply", S(112), primary: true);
        _sendButton.AccessibleName = "Send reply";
        _sendButton.Click += async (_, _) => await SendReplyAsync();

        buttons.Controls.Add(_sendButton);
        buttons.Controls.Add(viewButton);
        layout.Controls.Add(buttons);

        Shown += (_, _) =>
        {
            Activate();
            _replyBox.Focus();
        };
    }

    private Control BuildHeader(ElevationRequestDto request)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, S(14)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(44)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var mark = new Panel
        {
            Width = S(32),
            Height = S(32),
            BackColor = Color.FromArgb(255, 247, 247),
            Margin = new Padding(0, 0, S(12), 0),
        };
        mark.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(239, 179, 179));
            e.Graphics.DrawRectangle(pen, 0, 0, mark.Width - 1, mark.Height - 1);
        };

        var markText = new Label
        {
            Text = "!",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TrayStyle.Danger,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
        };
        mark.Controls.Add(markText);

        var copy = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(0),
        };
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = TrayStyle.Title("Request denied");
        title.Margin = new Padding(0);

        var subtitle = TrayStyle.HelpText(BuildSubtitle(request));
        subtitle.Margin = new Padding(0, S(4), 0, 0);
        subtitle.MaximumSize = new Size(S(360), 0);
        subtitle.AccessibleName = "Denial summary";

        copy.Controls.Add(title);
        copy.Controls.Add(subtitle);

        header.Controls.Add(mark, 0, 0);
        header.Controls.Add(copy, 1, 0);
        return header;
    }

    private Control BuildReasonBox(ElevationRequestDto request)
    {
        var reasonPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(S(12)),
            BackColor = Color.FromArgb(255, 247, 247),
            Margin = new Padding(0, 0, 0, S(2)),
            AccessibleName = "Denial reason",
        };
        reasonPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reasonPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reasonPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(239, 179, 179));
            e.Graphics.DrawRectangle(pen, 0, 0, reasonPanel.Width - 1, reasonPanel.Height - 1);
        };

        var label = new Label
        {
            Text = "Why this was denied",
            AutoSize = true,
            ForeColor = TrayStyle.Danger,
            Font = new Font(Font.FontFamily, 8.9f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, S(6)),
        };

        var reason = new Label
        {
            Text = DenialReason(request),
            AutoSize = true,
            MaximumSize = new Size(S(392), 0),
            ForeColor = TrayStyle.Text,
            Margin = new Padding(0),
            AccessibleName = "Denial reason text",
        };

        reasonPanel.Controls.Add(label);
        reasonPanel.Controls.Add(reason);
        return reasonPanel;
    }

    private void OpenRequest()
    {
        var form = new RequestStatusForm(_client, _request.Id, _request.ActionDisplayName);
        form.Show();
        form.Activate();
    }

    private async Task SendReplyAsync()
    {
        if (_sending) return;

        var body = _replyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            SetStatus("Enter a reply before sending.", isError: true);
            return;
        }
        if (body.Length > MaxCommentLength)
        {
            SetStatus($"Replies must be {MaxCommentLength} characters or fewer.", isError: true);
            return;
        }

        _sending = true;
        _sendButton.Enabled = false;
        _replyBox.Enabled = false;
        SetStatus("Sending reply...", isError: false);

        try
        {
            var posted = await _client.AddCommentAsync(_request.Id, body);
            if (posted is null)
            {
                SetStatus("Reply could not be sent. Check your connection and try again.", isError: true);
                return;
            }

            _replyBox.Text = "";
            SetStatus("Reply sent. Open the request to continue the conversation.", isError: false);
        }
        catch
        {
            SetStatus("Reply could not be sent. Check your connection and try again.", isError: true);
        }
        finally
        {
            _sending = false;
            _sendButton.Enabled = true;
            _replyBox.Enabled = true;
            _replyBox.Focus();
        }
    }

    private void SetStatus(string text, bool isError)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = isError ? TrayStyle.Danger : TrayStyle.Muted;
    }

    private static string BuildSubtitle(ElevationRequestDto request)
    {
        var reviewer = string.IsNullOrWhiteSpace(request.ReviewerUpn) ? "an approver" : request.ReviewerUpn;
        var reviewedAt = request.ReviewedAt?.ToLocalTime().ToString("g");
        var reviewedText = string.IsNullOrWhiteSpace(reviewedAt)
            ? $"reviewed by {reviewer}"
            : $"reviewed by {reviewer} on {reviewedAt}";

        return $"{request.ActionDisplayName} was {reviewedText}.";
    }

    private static string DenialReason(ElevationRequestDto request) =>
        string.IsNullOrWhiteSpace(request.ReviewerNote)
            ? "No denial reason was provided."
            : request.ReviewerNote;
}
