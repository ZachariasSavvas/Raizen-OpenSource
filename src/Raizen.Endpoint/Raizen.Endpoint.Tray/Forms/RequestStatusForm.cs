using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>Shows current status of a previously submitted request and refreshes on a timer.</summary>
public sealed class RequestStatusForm : Form
{
    private readonly ServerClient _client;
    private readonly Guid _requestId;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly ProgressBar _progress;
    private readonly Button _closeButton;

    public RequestStatusForm(ServerClient client, Guid requestId, string actionName)
    {
        _client = client;
        _requestId = requestId;

        AutoScaleMode       = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Text = $"Request status | {actionName}";
        TrayStyle.ApplyDialog(this, 450, 280);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            RowCount = 6,
            ColumnCount = 1,
        };
        Controls.Add(layout);

        layout.Controls.Add(TrayStyle.Title("Request status"));
        layout.Controls.Add(TrayStyle.HelpText(actionName));
        layout.Controls.Add(new Label { Text = $"Request ID: {requestId}", AutoSize = true, ForeColor = TrayStyle.Muted });

        _statusLabel = new Label { Text = "Checking...", Font = new Font(Font.FontFamily, 14, FontStyle.Bold), AutoSize = true, ForeColor = TrayStyle.Text };
        layout.Controls.Add(_statusLabel);

        _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Fill, Height = 10, Margin = new Padding(0, 4, 0, 10) };
        layout.Controls.Add(_progress);

        _detailLabel = new Label { Text = "", Dock = DockStyle.Fill, AutoSize = true, ForeColor = TrayStyle.Muted };
        layout.Controls.Add(_detailLabel);

        _closeButton = TrayStyle.Button("Close", 88, primary: true);
        _closeButton.Anchor = AnchorStyles.Right;
        _closeButton.Click += (_, _) => Close();
        layout.Controls.Add(_closeButton);

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
        try
        {
            var r = await _client.GetRequestStatusAsync(_requestId);
            if (r is null)
            {
                _statusLabel.Text = "Not found";
                _timer.Stop();
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

            _detailLabel.Text = r.Status switch
            {
                RequestStatus.Pending => "Waiting for approver review...",
                RequestStatus.Approved => "Approved — waiting for endpoint to execute...",
                RequestStatus.Executing => "Executing on endpoint...",
                RequestStatus.Succeeded => r.ExecutionResult ?? "Completed successfully.",
                RequestStatus.Failed => r.ExecutionError ?? "Execution failed.",
                RequestStatus.Denied => $"Denied by {r.ReviewerUpn}: {r.ReviewerNote}",
                RequestStatus.Expired => "Request expired without being reviewed.",
                _ => ""
            };

            _progress.Visible = r.Status is RequestStatus.Pending or RequestStatus.Approved or RequestStatus.Executing;

            // Stop polling once in a terminal state
            if (r.Status is RequestStatus.Succeeded or RequestStatus.Failed or
                RequestStatus.Denied or RequestStatus.Cancelled or RequestStatus.Expired)
            {
                _timer.Stop();
            }
        }
        catch (Exception ex)
        {
            _detailLabel.Text = $"Error: {ex.Message}";
        }
    }
}
