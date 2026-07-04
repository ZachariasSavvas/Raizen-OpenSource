namespace Raizen.Endpoint.Tray.Forms;

internal static class TrayStyle
{
    internal static readonly Color Surface = Color.FromArgb(255, 255, 255);
    internal static readonly Color SubtleSurface = Color.FromArgb(248, 250, 252);
    internal static readonly Color Border = Color.FromArgb(213, 218, 226);
    internal static readonly Color Text = Color.FromArgb(24, 24, 27);
    internal static readonly Color Muted = Color.FromArgb(82, 82, 91);
    internal static readonly Color Primary = Color.FromArgb(17, 24, 39);
    internal static readonly Color PrimaryHover = Color.FromArgb(39, 39, 42);
    internal static readonly Color Success = Color.FromArgb(22, 101, 52);
    internal static readonly Color Danger = Color.FromArgb(185, 28, 28);

    internal static void ApplyDialog(Form form, int width, int height)
    {
        form.Width = width;
        form.Height = height;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.BackColor = Surface;
        form.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

        var icon = BrandIcon.Get();
        if (icon is not null) form.Icon = icon;
    }

    internal static Label Title(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Text,
        Font = new Font("Segoe UI", 13.5f, FontStyle.Bold, GraphicsUnit.Point),
        Margin = new Padding(0, 0, 0, 2),
    };

    internal static Label HelpText(string text = "") => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Fill,
        ForeColor = Muted,
        Font = new Font("Segoe UI", 8.6f, FontStyle.Regular, GraphicsUnit.Point),
        Margin = new Padding(0, 0, 0, 8),
    };

    internal static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Text,
        Font = new Font("Segoe UI", 9.1f, FontStyle.Bold, GraphicsUnit.Point),
        Margin = new Padding(0, 8, 0, 3),
    };

    internal static void StyleTextBox(TextBox textBox)
    {
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.BackColor = Surface;
        textBox.ForeColor = Text;
        textBox.Margin = new Padding(0, 0, 0, 4);
    }

    internal static void StyleComboBox(ComboBox comboBox)
    {
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.BackColor = Surface;
        comboBox.ForeColor = Text;
        comboBox.Margin = new Padding(0, 0, 0, 4);
    }

    internal static Button Button(string text, int width, bool primary)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Primary : SubtleSurface,
            ForeColor = primary ? Color.White : Text,
            Font = new Font("Segoe UI", 9.2f, primary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point),
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0),
        };

        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = primary ? PrimaryHover : Color.FromArgb(241, 245, 249);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(63, 63, 70) : Color.FromArgb(226, 232, 240);
        return button;
    }
}
