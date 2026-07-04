using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// A compact, borderless toast widget that slides in from the bottom-right
/// when an admin posts a comment on a pending request. Lets the user reply inline.
/// </summary>
public sealed class CommentNotificationForm : Form
{
    // ── Shared stacking so multiple notifications don't overlap ──────────────
    private static int _openCount;

    private readonly ServerClient _serverClient;
    private readonly Guid _requestId;
    private readonly int _stackSlot;
    private int _targetTop;

    private readonly System.Windows.Forms.Timer _slideTimer;
    private readonly System.Windows.Forms.Timer _countdown;
    private int _countdownTicks;

    private readonly TextBox  _replyBox;
    private readonly Button   _sendBtn;
    private readonly Panel    _progressTrack;
    private readonly Panel    _progressFill;

    private const int W              = 370;
    private const int AutoCloseSecs  = 50;
    private const int TickMs         = 80;

    // ── Navy palette ─────────────────────────────────────────────────────────
    private static readonly Color Navy    = Color.FromArgb(15, 23, 42);
    private static readonly Color NavyMid = Color.FromArgb(30, 58, 138);
    private static readonly Color Accent  = Color.FromArgb(59, 130, 246);
    private static readonly Color Bg      = Color.FromArgb(248, 250, 252);
    private static readonly Color Border  = Color.FromArgb(203, 213, 225);
    private static readonly Color TextDark= Color.FromArgb(15, 23, 42);
    private static readonly Color TextMid = Color.FromArgb(71, 85, 105);

    // ── DPI helper (this form doesn't inherit RaizenFormBase) ────────────────
    private int S(int px) => (int)(px * (DeviceDpi / 96.0f));

    public CommentNotificationForm(
        ServerClient serverClient,
        Guid requestId,
        string requestName,
        string authorName,
        string commentBody)
    {
        _serverClient = serverClient;
        _requestId    = requestId;
        _stackSlot    = Interlocked.Increment(ref _openCount);

        // Manual DPI scaling via S() — AutoScaleMode.None prevents double-scaling
        AutoScaleMode = AutoScaleMode.None;

        // Form chrome
        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;
        ShowInTaskbar   = false;
        BackColor       = Bg;
        Width           = S(W);

        SuspendLayout();

        int y = 0;

        // ── Header ────────────────────────────────────────────────────────────
        var header = MakePanel(0, y, S(W), S(44), Navy);

        var icon = MakeLabel("\ud83d\udcac", S(10), S(11), S(22), S(22), header);
        icon.Font = new Font("Segoe UI Emoji", 12f);

        var title = MakeLabel(Truncate(requestName, 36), S(34), S(12), S(W - 72), S(20), header);
        title.Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        title.ForeColor = Color.White;

        var closeBtn = new Button
        {
            Text      = "\u00d7",
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 13f),
            ForeColor = Color.FromArgb(148, 163, 184),
            BackColor = Color.Transparent,
            Location  = new Point(S(W - 36), S(8)),
            Size      = new Size(S(30), S(28)),
            Cursor    = Cursors.Hand,
            TabStop   = false,
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 255, 255, 255);
        closeBtn.Click += (_, _) => AnimateClose();
        header.Controls.Add(closeBtn);

        Controls.Add(header);
        y += 44;

        // ── Author row ────────────────────────────────────────────────────────
        y += 10;

        // Avatar circle
        var avatar = new Panel
        {
            Location  = new Point(S(12), S(y)),
            Size      = new Size(S(28), S(28)),
            BackColor = NavyMid,
        };
        avatar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(NavyMid);
            e.Graphics.FillEllipse(b, 0, 0, avatar.Width - 1, avatar.Height - 1);
            var initials = GetInitials(authorName);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var f = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            using var fb = new SolidBrush(Color.White);
            e.Graphics.DrawString(initials, f, fb, new RectangleF(0, 0, avatar.Width, avatar.Height), sf);
        };
        Controls.Add(avatar);

        var authorLbl = MakeLabel(authorName, S(46), S(y + 2), S(W - 58), S(16), null);
        authorLbl.Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        authorLbl.ForeColor = TextDark;
        Controls.Add(authorLbl);

        var timeLbl = MakeLabel("Just now", S(46), S(y + 16), S(W - 58), S(12), null);
        timeLbl.Font      = new Font("Segoe UI", 7f);
        timeLbl.ForeColor = TextMid;
        Controls.Add(timeLbl);

        y += 36;

        // ── Comment body ──────────────────────────────────────────────────────
        y += 6;
        var bodyLines = WrapText(commentBody, 52);
        var bodyText  = string.Join("\n", bodyLines.Take(4));
        if (bodyLines.Count > 4) bodyText += "\u2026";

        var body = new Label
        {
            Text      = bodyText,
            Font      = new Font("Segoe UI", 9f),
            ForeColor = TextDark,
            Location  = new Point(S(12), S(y)),
            Size      = new Size(S(W - 24), Math.Min(bodyLines.Take(4).Count(), 4) * S(18) + S(4)),
            AutoSize  = false,
        };
        Controls.Add(body);
        y += (Math.Min(bodyLines.Take(4).Count(), 4) * 18 + 4) + 10;

        // ── Divider ───────────────────────────────────────────────────────────
        var divider = MakePanel(S(12), S(y), S(W - 24), S(1), Border);
        Controls.Add(divider);
        y += 9;

        // ── Reply row ─────────────────────────────────────────────────────────
        _replyBox = new TextBox
        {
            PlaceholderText = "Reply to admin\u2026",
            Font            = new Font("Segoe UI", 9f),
            Location        = new Point(S(12), S(y)),
            Size            = new Size(S(W - 90), S(28)),
            BorderStyle     = BorderStyle.FixedSingle,
            BackColor       = Color.White,
            ForeColor       = TextDark,
        };
        _replyBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            { e.SuppressKeyPress = true; _ = SendAsync(); }
        };
        Controls.Add(_replyBox);

        _sendBtn = new Button
        {
            Text      = "Send",
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Navy,
            FlatStyle = FlatStyle.Flat,
            Location  = new Point(S(W - 74), S(y) - 1),
            Size      = new Size(S(62), S(30)),
            Cursor    = Cursors.Hand,
            TabStop   = false,
        };
        _sendBtn.FlatAppearance.BorderSize = 0;
        _sendBtn.FlatAppearance.MouseOverBackColor = NavyMid;
        _sendBtn.Click += async (_, _) => await SendAsync();
        Controls.Add(_sendBtn);

        y += 36;

        // ── Progress bar (auto-close countdown) ───────────────────────────────
        _progressTrack = MakePanel(0, S(y), S(W), S(3), Color.FromArgb(226, 232, 240));
        Controls.Add(_progressTrack);

        _progressFill = MakePanel(0, 0, S(W), S(3), Accent);
        _progressTrack.Controls.Add(_progressFill);

        y += 3;
        Height = S(y);

        ResumeLayout();

        // Drop shadow via border paint
        Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };

        // ── Timers ────────────────────────────────────────────────────────────
        _slideTimer  = new System.Windows.Forms.Timer { Interval = 10 };
        _slideTimer.Tick += OnSlide;

        _countdownTicks = (AutoCloseSecs * 1000) / TickMs;
        _countdown = new System.Windows.Forms.Timer { Interval = TickMs };
        _countdown.Tick += OnCountdown;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        var screen = Screen.FromPoint(Cursor.Position);
        var work   = screen.WorkingArea;
        int margin = S(12);
        int gap    = S(8);
        _targetTop = work.Bottom - Height - margin - (_stackSlot - 1) * (Height + gap);
        Location   = new Point(work.Right - Width - margin, work.Bottom + S(10));

        _slideTimer.Start();
        _countdown.Start();
        _replyBox.Focus();
    }

    private void OnSlide(object? sender, EventArgs e)
    {
        int step = Math.Max(3, (Top - _targetTop) / 3);
        Top = Math.Max(_targetTop, Top - step);
        if (Top <= _targetTop) _slideTimer.Stop();
    }

    private void OnCountdown(object? sender, EventArgs e)
    {
        _countdownTicks--;
        int total = (AutoCloseSecs * 1000) / TickMs;
        int w     = (int)(_progressFill.Parent!.Width * (_countdownTicks / (double)total));
        _progressFill.Width = Math.Max(0, w);
        if (_countdownTicks <= 0) AnimateClose();
    }

    private async Task SendAsync()
    {
        var body = _replyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        _sendBtn.Enabled = false;
        _sendBtn.Text    = "\u2026";
        _replyBox.Enabled = false;

        try
        {
            await _serverClient.AddCommentAsync(_requestId, body);
            AnimateClose();
        }
        catch
        {
            _sendBtn.Enabled  = true;
            _sendBtn.Text     = "Send";
            _replyBox.Enabled = true;
            _replyBox.Focus();
        }
    }

    private void AnimateClose()
    {
        _countdown.Stop();
        _slideTimer.Stop();
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Interlocked.Decrement(ref _openCount);
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _slideTimer.Dispose(); _countdown.Dispose(); }
        base.Dispose(disposing);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Panel MakePanel(int x, int y, int w, int h, Color bg)
        => new() { Location = new Point(x, y), Size = new Size(w, h), BackColor = bg };

    private static Label MakeLabel(string text, int x, int y, int w, int h, Control? parent)
    {
        var lbl = new Label
        {
            Text     = text,
            Location = new Point(x, y),
            Size     = new Size(w, h),
            AutoSize = false,
        };
        parent?.Controls.Add(lbl);
        return lbl;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "\u2026";

    private static string GetInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpper(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpper(),
        };
    }

    private static List<string> WrapText(string text, int charsPerLine)
    {
        var lines  = new List<string>();
        var words  = text.Split(' ');
        var current = "";
        foreach (var word in words)
        {
            if (current.Length + word.Length + 1 > charsPerLine && current.Length > 0)
            { lines.Add(current); current = word; }
            else
            { current = current.Length == 0 ? word : current + " " + word; }
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }
}
