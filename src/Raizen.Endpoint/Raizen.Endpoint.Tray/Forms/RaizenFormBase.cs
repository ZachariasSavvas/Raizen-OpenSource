using System.DirectoryServices.AccountManagement;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// Base class for the modern borderless Raizen forms.
/// Provides DPI-aware auto-scaling, shared brand palette, header factory,
/// button factory, rounded corners, drag-to-move, and drop shadow.
///
/// Subclasses only need to build their body content; all chrome is handled here.
/// </summary>
public class RaizenFormBase : Form
{
    // ── Brand palette (shared across all forms) ──────────────────────────────
    protected static readonly Color NavyHeader  = Color.FromArgb(16,  22,  74);
    protected static readonly Color NavyDark    = Color.FromArgb(10,  14,  51);
    protected static readonly Color NavyChip    = Color.FromArgb(232, 234, 243);
    protected static readonly Color NavyChipFg  = Color.FromArgb(16,  22,  74);
    protected static readonly Color TextPrimary = Color.FromArgb(28,  25,  23);
    protected static readonly Color TextMuted   = Color.FromArgb(120, 113, 108);
    protected static readonly Color BgGray      = Color.FromArgb(249, 250, 251);
    protected static readonly Color BorderColor = Color.FromArgb(209, 213, 219);
    protected static readonly Color SepColor    = Color.FromArgb(229, 231, 235);
    protected static readonly Color Green       = Color.FromArgb(22,  101,  52);
    protected static readonly Color Red         = Color.FromArgb(185,  28,  28);
    protected static readonly Color SuccessGreen = Color.FromArgb(22, 163, 74);
    protected static readonly Color SuccessGreenHover = Color.FromArgb(21, 128, 61);

    // ── DPI ─────────────────────────────────────────────────────────────────
    protected float DpiScale => DeviceDpi / 96.0f;

    /// <summary>Scale a 96-DPI pixel value to the current monitor DPI.</summary>
    protected int S(int px) => (int)(px * DpiScale);

    // ── Drag support ────────────────────────────────────────────────────────
    private Point _dragOrigin;
    private bool  _dragging;

    // ── Header panel (exposed for subclass sizing) ──────────────────────────
    protected Panel? HeaderPanel { get; private set; }

    protected RaizenFormBase()
    {
        // Manual DPI scaling via S() — AutoScaleMode.None prevents double-scaling
        // with our explicit S() calls on borderless forms.
        AutoScaleMode = AutoScaleMode.None;

        FormBorderStyle = FormBorderStyle.None;
        BackColor       = Color.White;
        StartPosition   = FormStartPosition.CenterScreen;
        Font            = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview      = true;
        KeyDown        += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        var icon = BrandIcon.Get();
        if (icon is not null) Icon = icon;
    }

    // ── Header factory ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the standard 76px navy header with brand badge, title, subtitle,
    /// drag-to-move, and close button. Adds it to this form's Controls.
    /// </summary>
    protected Panel BuildHeader(string title, string subtitle)
    {
        var hdr = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = S(76),
            BackColor = NavyHeader,
        };

        // Drag-to-move
        hdr.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragging = true; _dragOrigin = e.Location; } };
        hdr.MouseMove += (_, e) =>
        {
            if (_dragging)
                Location = new Point(Location.X + e.X - _dragOrigin.X,
                                     Location.Y + e.Y - _dragOrigin.Y);
        };
        hdr.MouseUp += (_, _) => _dragging = false;

        // Paint badge + text
        hdr.Paint += (_, pe) =>
        {
            var g = pe.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var scale = DpiScale;
            BrandIcon.DrawHeaderBadge(g, (int)(14 * scale), (int)(14 * scale), scale);

            using var titleFont = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            g.DrawString(title, titleFont, Brushes.White, (int)(72 * scale), (int)(20 * scale));

            using var subFont  = new Font("Segoe UI", 8.5f);
            using var subBrush = new SolidBrush(Color.FromArgb(210, 255, 255, 255));
            g.DrawString(subtitle, subFont, subBrush, (int)(72 * scale), (int)(44 * scale));
        };

        // Close button (anchored to top-right so it stays put after scaling)
        var closeBtn = new Button
        {
            Text      = "\u2715",
            Size      = new Size(S(34), S(34)),
            Location  = new Point(ClientSize.Width - S(42), S(21)),
            Anchor    = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
            TabStop   = false,
        };
        closeBtn.FlatAppearance.BorderSize         = 0;
        closeBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 255, 255, 255);
        closeBtn.FlatAppearance.MouseDownBackColor = Color.FromArgb(80, 255, 255, 255);
        closeBtn.Click += (_, _) => Close();
        hdr.Controls.Add(closeBtn);

        HeaderPanel = hdr;
        return hdr;
    }

    // ── Button factory ──────────────────────────────────────────────────────

    protected Button MakeButton(string text, int width, bool primary)
    {
        var btn = new Button
        {
            Text      = text,
            Size      = new Size(S(width), S(36)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? NavyHeader : BgGray,
            ForeColor = primary ? Color.White : TextPrimary,
            Font      = new Font("Segoe UI", 9.5f, primary ? FontStyle.Bold : FontStyle.Regular),
            Cursor    = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize         = primary ? 0 : 1;
        btn.FlatAppearance.BorderColor        = BorderColor;
        btn.FlatAppearance.MouseOverBackColor = primary ? NavyDark : Color.FromArgb(243, 244, 246);
        btn.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(6, 9, 32) : Color.FromArgb(229, 231, 235);
        return btn;
    }

    /// <summary>Creates a small navy-coloured button (e.g. "Resolve", "Browse").</summary>
    protected Button MakeNavyButton(string text, int width, int height)
    {
        var btn = new Button
        {
            Text      = text,
            Size      = new Size(S(width), S(height)),
            FlatStyle = FlatStyle.Flat,
            BackColor = NavyHeader,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize         = 0;
        btn.FlatAppearance.MouseOverBackColor = NavyDark;
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(6, 9, 32);
        return btn;
    }

    /// <summary>Creates a segmented toggle button for action/mode selectors.</summary>
    protected Button MakeSegmentButton(string text, int width, int height = 36)
    {
        return new Button
        {
            Text      = text,
            Size      = new Size(S(width), S(height)),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(243, 244, 246),
            ForeColor = TextPrimary,
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false,
            FlatAppearance = { BorderSize = 0 },
        };
    }

    /// <summary>Highlights the active button in a segmented toggle group.</summary>
    protected static void SetSegmentActive(Button active, params Button[] all)
    {
        foreach (var btn in all)
        {
            bool isActive = btn == active;
            btn.BackColor = isActive ? NavyHeader : Color.FromArgb(243, 244, 246);
            btn.ForeColor = isActive ? Color.White : TextPrimary;
            btn.FlatAppearance.MouseOverBackColor = isActive ? NavyDark : Color.FromArgb(229, 231, 235);
        }
    }

    // ── Common helpers ──────────────────────────────────────────────────────

    protected static void SetStatus(Label label, string text, bool error = false)
    {
        label.ForeColor = error ? Red : Green;
        label.Text      = text;
    }

    protected static (string upn, string displayName) GetCurrentUserInfo()
    {
        try
        {
            using var ctx  = new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.Current;
            if (user != null)
                return (user.UserPrincipalName ?? Environment.UserName,
                        user.DisplayName        ?? Environment.UserName);
        }
        catch { }
        return (Environment.UserName, Environment.UserName);
    }

    // ── Rounded corners ─────────────────────────────────────────────────────

    protected void ApplyRoundedCorners()
    {
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch
        {
            // Windows 10 fallback
            int radius = S(12);
            var rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, radius, radius);
            if (rgn != IntPtr.Zero) Region = Region.FromHrgn(rgn);
        }
    }

    // ── Drop shadow ─────────────────────────────────────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    // ── Win32 imports ────────────────────────────────────────────────────────

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    [DllImport("Gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
}
