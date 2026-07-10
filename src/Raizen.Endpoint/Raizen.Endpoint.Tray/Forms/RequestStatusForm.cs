using System.Text;
using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>Shows current status of a previously submitted request and refreshes on a timer.</summary>
public sealed class RequestStatusForm : Form
{
    private const int MaxCommentLength = 4000;

    private readonly ServerClient _client;
    private readonly Guid _requestId;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly ProgressBar _progress;
    private readonly TextBox _commentsBox;
    private readonly TextBox _replyBox;
    private readonly Label _commentStatusLabel;
    private readonly Button _sendCommentButton;
    private readonly Button _closeButton;
    private bool _refreshing;
    private bool _postingComment;

    public RequestStatusForm(ServerClient client, Guid requestId, string actionName)
    {
        _client = client;
        _requestId = requestId;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Text = $"Request status | {actionName}";
        TrayStyle.ApplyDialog(this, 560, 540);
        MinimumSize = new Size(520, 480);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            RowCount = 11,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        layout.Controls.Add(TrayStyle.Title("Request status"));
        layout.Controls.Add(TrayStyle.HelpText(actionName));

        var idLabel = new Label
        {
            Text = $"Request ID: {requestId}",
            AutoSize = true,
            ForeColor = TrayStyle.Muted,
            AccessibleName = "Request ID",
        };
        layout.Controls.Add(idLabel);

        _statusLabel = new Label
        {
            Text = "Checking...",
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            AutoSize = true,
            ForeColor = TrayStyle.Text,
            AccessibleName = "Request status",
        };
        layout.Controls.Add(_statusLabel);

        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Dock = DockStyle.Fill,
            Height = 10,
            Margin = new Padding(0, 4, 0, 10),
            AccessibleName = "Request progress",
        };
        layout.Controls.Add(_progress);

        _detailLabel = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = TrayStyle.Muted,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = "Request detail",
        };
        layout.Controls.Add(_detailLabel);

        layout.Controls.Add(TrayStyle.FieldLabel("Comments"));

        _commentsBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = TrayStyle.SubtleSurface,
            ForeColor = TrayStyle.Text,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "Request comments",
            AccessibleDescription = "Conversation between the requester and approvers.",
        };
        layout.Controls.Add(_commentsBox);

        layout.Controls.Add(TrayStyle.FieldLabel("Reply"));

        var replyRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6),
        };
        replyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        replyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _replyBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Add a comment for the approver...",
            MaxLength = MaxCommentLength,
            AccessibleName = "Reply comment",
        };
        TrayStyle.StyleTextBox(_replyBox);
        _replyBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.SuppressKeyPress = true;
                await SendCommentAsync();
            }
        };

        _sendCommentButton = TrayStyle.Button("Send", 88, primary: true);
        _sendCommentButton.AccessibleName = "Send comment";
        _sendCommentButton.Click += async (_, _) => await SendCommentAsync();

        replyRow.Controls.Add(_replyBox, 0, 0);
        replyRow.Controls.Add(_sendCommentButton, 1, 0);
        layout.Controls.Add(replyRow);

        _commentStatusLabel = new Label
        {
            Text = "",
            AutoSize = true,
            ForeColor = TrayStyle.Muted,
            AccessibleName = "Comment status",
        };
        layout.Controls.Add(_commentStatusLabel);

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        _closeButton = TrayStyle.Button("Close", 88, primary: false);
        _closeButton.Click += (_, _) => Close();
        buttonRow.Controls.Add(_closeButton);
        layout.Controls.Add(buttonRow);

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) => await RefreshAsync();

        Load += async (_, _) =>
        {
            await RefreshAsync();
            _timer.Start();
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
        };
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var r = await _client.GetRequestStatusAsync(_requestId);
            if (r is null)
            {
                _statusLabel.Text = "Not found";
                _detailLabel.Text = "This request could not be found on the server.";
                await LoadCommentsAsync();
                return;
            }

            _statusLabel.Text = r.Status.ToString();
            _statusLabel.ForeColor = r.Status switch
            {
                RequestStatus.Succeeded => TrayStyle.Success,
                RequestStatus.Denied or RequestStatus.Failed or RequestStatus.Expired => TrayStyle.Danger,
                RequestStatus.Approved or RequestStatus.Executing => TrayStyle.Primary,
                _ => TrayStyle.Text
            };

            _detailLabel.Text = FormatStatusDetail(r);
            _progress.Visible = r.Status is RequestStatus.Pending or RequestStatus.Approved or RequestStatus.Executing;
            await LoadCommentsAsync();
        }
        catch
        {
            _detailLabel.Text = "Could not refresh this request. Check your server connection and try again.";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task LoadCommentsAsync()
    {
        var comments = await _client.GetCommentsAsync(_requestId);
        if (comments.Count == 0)
        {
            _commentsBox.Text = "No comments yet.";
            return;
        }

        var text = new StringBuilder();
        foreach (var c in comments.OrderBy(c => c.CreatedAt))
        {
            var author = string.IsNullOrWhiteSpace(c.AuthorDisplayName) ? c.AuthorUpn : c.AuthorDisplayName;
            var role = c.IsAdmin ? "Approver" : "Requester";
            text.AppendLine($"{c.CreatedAt.ToLocalTime():g} - {role} - {author}");
            text.AppendLine(c.Body);
            text.AppendLine();
        }
        _commentsBox.Text = text.ToString().TrimEnd();
    }

    private async Task SendCommentAsync()
    {
        if (_postingComment) return;

        var body = _replyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            SetCommentStatus("Enter a comment before sending.", isError: true);
            return;
        }
        if (body.Length > MaxCommentLength)
        {
            SetCommentStatus($"Comments must be {MaxCommentLength} characters or fewer.", isError: true);
            return;
        }

        _postingComment = true;
        _sendCommentButton.Enabled = false;
        _replyBox.Enabled = false;
        SetCommentStatus("Sending comment...", isError: false);

        try
        {
            var posted = await _client.AddCommentAsync(_requestId, body);
            if (posted is null)
            {
                SetCommentStatus("Comment could not be sent. Check your connection and try again.", isError: true);
                return;
            }

            _replyBox.Text = "";
            SetCommentStatus("Comment sent.", isError: false);
            await LoadCommentsAsync();
        }
        catch
        {
            SetCommentStatus("Comment could not be sent. Check your connection and try again.", isError: true);
        }
        finally
        {
            _postingComment = false;
            _sendCommentButton.Enabled = true;
            _replyBox.Enabled = true;
            _replyBox.Focus();
        }
    }

    private void SetCommentStatus(string text, bool isError)
    {
        _commentStatusLabel.Text = text;
        _commentStatusLabel.ForeColor = isError ? TrayStyle.Danger : TrayStyle.Muted;
    }

    private static string FormatStatusDetail(ElevationRequestDto r) => r.Status switch
    {
        RequestStatus.Pending => "Waiting for approver review.",
        RequestStatus.Approved => "Approved. Waiting for the endpoint service to execute it.",
        RequestStatus.Executing => "Executing on the endpoint.",
        RequestStatus.Succeeded => string.IsNullOrWhiteSpace(r.ExecutionResult)
            ? "Completed successfully."
            : r.ExecutionResult,
        RequestStatus.Failed => string.IsNullOrWhiteSpace(r.ExecutionError)
            ? "Execution failed. Check endpoint logs for details."
            : r.ExecutionError,
        RequestStatus.Denied => $"Denied by {ReviewerName(r)}.{Environment.NewLine}Reason: {DenialReason(r)}",
        RequestStatus.Expired => "Request expired without being reviewed.",
        RequestStatus.Cancelled => "Request was cancelled.",
        _ => ""
    };

    private static string ReviewerName(ElevationRequestDto r) =>
        string.IsNullOrWhiteSpace(r.ReviewerUpn) ? "an approver" : r.ReviewerUpn;

    private static string DenialReason(ElevationRequestDto r) =>
        string.IsNullOrWhiteSpace(r.ReviewerNote) ? "No denial reason was provided." : r.ReviewerNote;
}
