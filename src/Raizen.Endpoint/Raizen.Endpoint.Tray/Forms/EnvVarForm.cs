using System.Runtime.InteropServices;
using Microsoft.Win32;
using Raizen.Endpoint.Tray.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Tray.Forms;

/// <summary>
/// Lets a user request a system environment variable change.
/// PATH is handled specially: the form auto-detects it and shows
/// append / prepend / remove-entry / set-full operations with a
/// live list of current PATH entries.
/// All other variables get a simple Set / Delete UI.
/// </summary>
public sealed class EnvVarForm : RaizenFormBase
{
    // ── Layout constants (design-time 96 DPI) ────────────────────────────────
    private const int HeaderH      = 76;
    private const int FormW        = 420;
    private const int BodyL        = 24;
    private const int BodyW        = 372;
    private const int PanelTop     = 114;
    private const int PathPanelH   = 232;
    private const int GenericSetH  = 96;
    private const int GenericDelH  = 46;
    private const int LowerSectionH = 198;

    private const string EnvKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private readonly ServerClient _client;

    // Body panel (for dynamic resize)
    private Panel _body = null!;

    // Upper section
    private TextBox _nameBox          = null!;
    private Label   _currentValueLbl  = null!;

    // PATH panel
    private Panel   _pathPanel        = null!;
    private ListBox _pathListBox      = null!;
    private Button  _btnAppend        = null!;
    private Button  _btnPrepend       = null!;
    private Button  _btnRemoveEntry   = null!;
    private Button  _btnSetFull       = null!;
    private Label   _entryLabel       = null!;
    private TextBox _entryBox         = null!;

    // Generic panel
    private Panel   _genericPanel     = null!;
    private Button  _btnSet           = null!;
    private Button  _btnDeleteVar     = null!;
    private Label   _valueLbl         = null!;
    private TextBox _valueBox         = null!;

    // Lower section
    private Panel   _lowerSep         = null!;
    private Label   _lowerJustLbl     = null!;
    private TextBox _justBox          = null!;
    private Label   _statusLabel      = null!;
    private Button  _submitBtn        = null!;
    private Button  _cancelBtn        = null!;

    // State
    private bool   _isPathMode     = false;
    private string _pathOp         = "Append";
    private string _genericOp      = "Set";

    public EnvVarForm(ServerClient client)
    {
        _client = client;

        Text       = "Raizen | Request Environment Variable";
        ClientSize = new Size(S(FormW), S(HeaderH + PanelTop + GenericSetH + LowerSectionH));
        ShowInTaskbar = true;

        SuspendLayout();

        Controls.Add(BuildHeader("Request Environment Variable",
            "System environment variable, requires admin approval"));
        Controls.Add(BuildBody());
        ResumeLayout(true);
        ApplyRoundedCorners();

        SwitchToGenericMode(animate: false);
    }

    private Panel BuildBody()
    {
        _body = new Panel
        {
            Location  = new Point(0, S(HeaderH)),
            Size      = new Size(S(FormW), S(PanelTop + GenericSetH + LowerSectionH)),
            BackColor = Color.White,
        };

        int y = 20;

        // Variable name row
        _body.Controls.Add(BoldLabel("Variable Name *", BodyL, y)); y += 22;

        int nameW = BodyW - 90;
        _nameBox = new TextBox
        {
            Location        = new Point(S(BodyL), S(y)),
            Size            = new Size(S(nameW), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. JAVA_HOME",
        };
        _nameBox.TextChanged += (_, _) => OnVariableNameChanged();
        _body.Controls.Add(_nameBox);

        var commonBtn = new Button
        {
            Text      = "\u25bc Existing",
            Location  = new Point(S(BodyL + nameW + 8), S(y)),
            Size      = new Size(S(82), S(26)),
            FlatStyle = FlatStyle.Flat,
            BackColor = BgGray,
            ForeColor = TextPrimary,
            Font      = new Font("Segoe UI", 8.5f),
            Cursor    = Cursors.Hand,
        };
        commonBtn.FlatAppearance.BorderSize  = 1;
        commonBtn.FlatAppearance.BorderColor = BorderColor;
        commonBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 244, 246);
        commonBtn.Click += ShowCommonVarsMenu;
        _body.Controls.Add(commonBtn);
        y += 32;

        _currentValueLbl = new Label
        {
            Location  = new Point(S(BodyL), S(y)),
            Size      = new Size(S(BodyW), S(18)),
            Font      = new Font("Segoe UI", 8f),
            ForeColor = TextMuted,
            Text      = "Type a variable name to see its current value",
        };
        _body.Controls.Add(_currentValueLbl);
        y += 22;

        _body.Controls.Add(Sep(BodyL, y, BodyW)); y += 14;

        // PATH panel
        _pathPanel = new Panel
        {
            Location  = new Point(0, S(y)),
            Size      = new Size(S(FormW), S(PathPanelH)),
            BackColor = Color.White,
            Visible   = false,
        };
        BuildPathPanel();
        _body.Controls.Add(_pathPanel);

        // Generic panel
        _genericPanel = new Panel
        {
            Location  = new Point(0, S(y)),
            Size      = new Size(S(FormW), S(GenericSetH)),
            BackColor = Color.White,
            Visible   = true,
        };
        BuildGenericPanel();
        _body.Controls.Add(_genericPanel);

        // Lower section
        int lowerY = y + GenericSetH;

        _lowerSep = Sep(BodyL, lowerY, BodyW);
        _body.Controls.Add(_lowerSep); lowerY += 14;

        _lowerJustLbl = BoldLabel("Reason for this request *", BodyL, lowerY);
        _body.Controls.Add(_lowerJustLbl); lowerY += 22;

        _justBox = new TextBox
        {
            Multiline       = true,
            Location        = new Point(S(BodyL), S(lowerY)),
            Size            = new Size(S(BodyW), S(68)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. Setting JAVA_HOME for the build pipeline",
            MaxLength       = 500,
            AcceptsReturn   = true,
            ScrollBars      = ScrollBars.Vertical,
        };
        _justBox.TextChanged += (_, _) => UpdateSubmitState();
        _body.Controls.Add(_justBox); lowerY += 78;

        _statusLabel = new Label
        {
            Location  = new Point(S(BodyL), S(lowerY)),
            Size      = new Size(S(BodyW), S(18)),
            Font      = new Font("Segoe UI", 8.5f),
            ForeColor = TextMuted,
        };
        _body.Controls.Add(_statusLabel); lowerY += 28;

        _cancelBtn = MakeButton("Cancel", 82, primary: false);
        _cancelBtn.Location = new Point(S(FormW - BodyL - 82 - 8 - 156), S(lowerY));
        _cancelBtn.Click   += (_, _) => Close();

        _submitBtn = MakeButton("Submit Request", 156, primary: true);
        _submitBtn.Location = new Point(S(FormW - BodyL - 156), S(lowerY));
        _submitBtn.Enabled  = false;
        _submitBtn.Click   += SubmitBtn_Click;

        _body.Controls.Add(_cancelBtn);
        _body.Controls.Add(_submitBtn);

        return _body;
    }

    private void BuildPathPanel()
    {
        int sy = 0;

        _pathPanel.Controls.Add(BoldLabel("Current PATH Entries", BodyL, sy)); sy += 22;

        _pathListBox = new ListBox
        {
            Location      = new Point(S(BodyL), S(sy)),
            Size          = new Size(S(BodyW), S(110)),
            Font          = new Font("Segoe UI", 8.5f),
            BorderStyle   = BorderStyle.FixedSingle,
            SelectionMode = SelectionMode.One,
        };
        _pathListBox.SelectedIndexChanged += (_, _) =>
        {
            if (_pathListBox.SelectedItem is string entry)
                _entryBox.Text = entry;
        };
        _pathPanel.Controls.Add(_pathListBox);
        sy += 114;

        int bw = BodyW / 4;
        var opPanel = new Panel
        {
            Location  = new Point(S(BodyL), S(sy)),
            Size      = new Size(S(BodyW), S(36)),
            BackColor = Color.FromArgb(243, 244, 246),
        };
        opPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(BorderColor);
            pe.Graphics.DrawRectangle(pen, 0, 0, opPanel.Width - 1, opPanel.Height - 1);
        };

        _btnAppend      = MakeSegmentButton("Append",       bw);
        _btnAppend.Location = new Point(0, 0);
        _btnPrepend     = MakeSegmentButton("Prepend",       bw);
        _btnPrepend.Location = new Point(S(bw), 0);
        _btnRemoveEntry = MakeSegmentButton("Remove",        bw);
        _btnRemoveEntry.Location = new Point(S(bw * 2), 0);
        _btnSetFull     = MakeSegmentButton("Set Full Path", BodyW - bw * 3);
        _btnSetFull.Location = new Point(S(bw * 3), 0);

        _btnAppend.Click      += (_, _) => SelectPathOp("Append");
        _btnPrepend.Click     += (_, _) => SelectPathOp("Prepend");
        _btnRemoveEntry.Click += (_, _) => SelectPathOp("RemoveEntry");
        _btnSetFull.Click     += (_, _) => SelectPathOp("SetFull");

        opPanel.Controls.AddRange([_btnAppend, _btnPrepend, _btnRemoveEntry, _btnSetFull]);
        _pathPanel.Controls.Add(opPanel);
        sy += 46;

        _entryLabel = BoldLabel("Entry to add *", BodyL, sy);
        _pathPanel.Controls.Add(_entryLabel); sy += 22;

        _entryBox = new TextBox
        {
            Location        = new Point(S(BodyL), S(sy)),
            Size            = new Size(S(BodyW), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. C:\\tools\\bin",
        };
        _entryBox.TextChanged += (_, _) => UpdateSubmitState();
        _pathPanel.Controls.Add(_entryBox);
    }

    private void BuildGenericPanel()
    {
        int sy = 0;

        int bw = BodyW / 2;
        var opPanel = new Panel
        {
            Location  = new Point(S(BodyL), S(sy)),
            Size      = new Size(S(BodyW), S(36)),
            BackColor = Color.FromArgb(243, 244, 246),
        };
        opPanel.Paint += (_, pe) =>
        {
            using var pen = new Pen(BorderColor);
            pe.Graphics.DrawRectangle(pen, 0, 0, opPanel.Width - 1, opPanel.Height - 1);
        };

        _btnSet       = MakeSegmentButton("Set / Create",    bw);
        _btnSet.Location = new Point(0, 0);
        _btnDeleteVar = MakeSegmentButton("Delete Variable", BodyW - bw);
        _btnDeleteVar.Location = new Point(S(bw), 0);

        _btnSet.Click       += (_, _) => SelectGenericOp("Set");
        _btnDeleteVar.Click += (_, _) => SelectGenericOp("Delete");

        opPanel.Controls.AddRange([_btnSet, _btnDeleteVar]);
        _genericPanel.Controls.Add(opPanel);
        sy += 46;

        _valueLbl = BoldLabel("New Value *", BodyL, sy);
        _genericPanel.Controls.Add(_valueLbl); sy += 22;

        _valueBox = new TextBox
        {
            Location        = new Point(S(BodyL), S(sy)),
            Size            = new Size(S(BodyW), S(26)),
            Font            = Font,
            BorderStyle     = BorderStyle.FixedSingle,
            PlaceholderText = "e.g. C:\\Java\\jdk-21",
        };
        _valueBox.TextChanged += (_, _) => UpdateSubmitState();
        _genericPanel.Controls.Add(_valueBox);
    }

    // ── Mode switching ──────────────────────────────────────────────────────

    private void OnVariableNameChanged()
    {
        var name = _nameBox.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            _currentValueLbl.Text      = "Type a variable name to see its current value";
            _currentValueLbl.ForeColor = TextMuted;
        }
        else
        {
            var current = ReadCurrentValue(name);
            if (current == null)
            {
                _currentValueLbl.Text      = "Current: (not set)";
                _currentValueLbl.ForeColor = TextMuted;
            }
            else
            {
                var display = current.Length > 90 ? current[..87] + "\u2026" : current;
                _currentValueLbl.Text      = $"Current: {display}";
                _currentValueLbl.ForeColor = TextPrimary;
            }
        }

        if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
            SwitchToPathMode(animate: true);
        else
            SwitchToGenericMode(animate: true);

        UpdateSubmitState();
    }

    private void SwitchToPathMode(bool animate)
    {
        _isPathMode = true;

        var rawPath = ReadCurrentValue("PATH") ?? "";
        _pathListBox.Items.Clear();
        foreach (var entry in rawPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(e => e.Trim())
                                     .Where(e => !string.IsNullOrEmpty(e)))
            _pathListBox.Items.Add(entry);

        _pathPanel.Visible    = true;
        _genericPanel.Visible = false;

        SelectPathOp(_pathOp);
    }

    private void SwitchToGenericMode(bool animate)
    {
        _isPathMode = false;
        _pathPanel.Visible    = false;
        _genericPanel.Visible = true;

        SelectGenericOp(_genericOp);
    }

    private void SelectPathOp(string op)
    {
        _pathOp = op;
        SetSegmentActive(
            op == "Append" ? _btnAppend : op == "Prepend" ? _btnPrepend : op == "RemoveEntry" ? _btnRemoveEntry : _btnSetFull,
            _btnAppend, _btnPrepend, _btnRemoveEntry, _btnSetFull);

        _entryLabel.Text = op switch
        {
            "Append"      => "Entry to add *",
            "Prepend"     => "Entry to add *",
            "RemoveEntry" => "Entry to remove *",
            _             => "Full PATH value *",
        };

        _entryBox.PlaceholderText = op == "SetFull"
            ? "Full semicolon-delimited PATH string"
            : "e.g. C:\\tools\\bin";

        // Use the visible panel's actual bottom position (already DPI-scaled)
        MoveLowerSection(_pathPanel.Bottom);
        ResizeForm(_pathPanel.Bottom);
        UpdateSubmitState();
    }

    private void SelectGenericOp(string op)
    {
        _genericOp = op;

        bool isDelete = op.Equals("Delete", StringComparison.OrdinalIgnoreCase);
        SetSegmentActive(isDelete ? _btnDeleteVar : _btnSet, _btnSet, _btnDeleteVar);

        _valueLbl.Visible = !isDelete;
        _valueBox.Visible = !isDelete;

        int panelH = S(isDelete ? GenericDelH : GenericSetH);
        _genericPanel.Height = panelH;

        MoveLowerSection(_genericPanel.Bottom);
        ResizeForm(_genericPanel.Bottom);
        UpdateSubmitState();
    }

    private void MoveLowerSection(int topY)
    {
        // topY is already in device pixels; spacing values also need device pixels
        int sp14 = S(14);
        int sp22 = S(22);
        int sp78 = S(78);
        int sp28 = S(28);

        int y = topY;
        _lowerSep.Location     = new Point(_lowerSep.Location.X, y);     y += sp14;
        _lowerJustLbl.Location = new Point(_lowerJustLbl.Location.X, y); y += sp22;
        _justBox.Location      = new Point(_justBox.Location.X, y);      y += sp78;
        _statusLabel.Location  = new Point(_statusLabel.Location.X, y);  y += sp28;
        _cancelBtn.Location    = new Point(_cancelBtn.Location.X, y);
        _submitBtn.Location    = new Point(_submitBtn.Location.X, y);
    }

    private void ResizeForm(int lowerTop)
    {
        int scaledLowerH = S(LowerSectionH);
        int bodyH = lowerTop + scaledLowerH;
        _body.Height = bodyH;
        int scaledHeaderH = S(HeaderH);
        ClientSize = new Size(ClientSize.Width, scaledHeaderH + bodyH);
        ApplyRoundedCorners();
    }

    // ── Common vars dropdown ────────────────────────────────────────────────

    private void ShowCommonVarsMenu(object? sender, EventArgs e)
    {
        string[] names;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EnvKeyPath);
            names = key?.GetValueNames() ?? [];
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        }
        catch { names = []; }

        var menu = new ContextMenuStrip();

        if (names.Length == 0)
        {
            menu.Items.Add("(could not read registry)").Enabled = false;
        }
        else
        {
            foreach (var v in names)
            {
                var varName = v;
                menu.Items.Add(varName, null, (_, _) =>
                {
                    _nameBox.Text = varName;
                    OnVariableNameChanged();
                });
            }
        }

        if (sender is Control c)
            menu.Show(c, new Point(0, c.Height));
    }

    private static string? ReadCurrentValue(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EnvKeyPath);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch { return null; }
    }

    // ── Validation ──────────────────────────────────────────────────────────

    private void UpdateSubmitState()
    {
        if (_submitBtn is null) return;

        bool nameOk = !string.IsNullOrWhiteSpace(_nameBox?.Text);
        bool justOk = !string.IsNullOrWhiteSpace(_justBox?.Text);

        bool valueOk;
        if (_isPathMode)
        {
            bool needsEntry = _pathOp != "Delete";
            valueOk = !needsEntry || !string.IsNullOrWhiteSpace(_entryBox?.Text);
        }
        else
        {
            bool isDelete = _genericOp.Equals("Delete", StringComparison.OrdinalIgnoreCase);
            valueOk = isDelete || !string.IsNullOrWhiteSpace(_valueBox?.Text);
        }

        _submitBtn.Enabled = nameOk && justOk && valueOk;
    }

    // ── Submit ──────────────────────────────────────────────────────────────

    private async void SubmitBtn_Click(object? sender, EventArgs e)
    {
        var varName = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(varName))
        { SetStatus(_statusLabel, "Please enter a variable name.", error: true); return; }

        if (string.IsNullOrWhiteSpace(_justBox.Text))
        { SetStatus(_statusLabel, "Please enter a reason for this request.", error: true); return; }

        _submitBtn.Enabled = false;
        _cancelBtn.Enabled = false;
        _justBox.Enabled   = false;
        SetStatus(_statusLabel, "Submitting request\u2026");

        try
        {
            var actions = await _client.GetActionsAsync();
            var action  = actions.FirstOrDefault(a => a.ActionType == ActionType.SetEnvironmentVariable);

            if (action is null)
            {
                SetStatus(_statusLabel, "No 'Environment Variable' action is configured in the catalog. Contact your Raizen admin.", error: true);
                ReEnableControls();
                return;
            }

            string op, value;

            if (_isPathMode)
            {
                op    = _pathOp;
                value = _entryBox.Text.Trim();
            }
            else
            {
                op    = _genericOp;
                value = _genericOp.Equals("Delete", StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : _valueBox.Text.Trim();
            }

            if (value.Length > 2048)
            { SetStatus(_statusLabel, "Value is too long (max 2048 characters).", error: true); ReEnableControls(); return; }

            var parameters = new Dictionary<string, string>
            {
                ["VariableName"] = varName,
                ["Operation"]    = op,
            };
            if (!string.IsNullOrEmpty(value))
                parameters["Value"] = value;

            var (upn, display) = GetCurrentUserInfo();
            var result = await _client.SubmitRequestAsync(new SubmitElevationRequestDto
            {
                ActionDefinitionId = action.Id,
                Justification      = _justBox.Text.Trim(),
                Parameters         = parameters,
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

    private void ReEnableControls()
    {
        _submitBtn.Enabled = true;
        _cancelBtn.Enabled = true;
        _justBox.Enabled   = true;
    }

    // ── OnShown ─────────────────────────────────────────────────────────────

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        SetForegroundWindow(Handle);
    }

    // ── Control factories ───────────────────────────────────────────────────

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
