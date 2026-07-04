using Raizen.Server.Setup.Pages;

namespace Raizen.Server.Setup;

public class WizardForm : Form
{
    // Palette
    private static readonly Color ColSidebarBg     = Color.FromArgb(17,  24,  39);
    private static readonly Color ColSidebarActive = Color.FromArgb(37,  99, 235);
    private static readonly Color ColSidebarDone   = Color.FromArgb(30,  64, 175);
    private static readonly Color ColSidebarText   = Color.FromArgb(148, 163, 184);
    private static readonly Color ColBtnPrimary    = Color.FromArgb(37,  99, 235);
    private static readonly Color ColBtnSecondary  = Color.FromArgb(71,  85, 105);
    private static readonly Color ColBtnCancel     = Color.FromArgb(100, 116, 139);
    private static readonly Color ColFooterBg      = Color.FromArgb(248, 250, 252);
    private static readonly Color ColDivider       = Color.FromArgb(226, 232, 240);

    // Layout constants — all math is done here, nowhere else
    private const int SidebarW  = 196;   // sidebar width
    private const int FooterH   = 64;    // footer height (incl. 1px divider)
    private const int BtnW      = 104;   // all buttons same width
    private const int BtnH      = 34;    // all buttons same height
    private const int BtnY      = 15;    // button top within footer
    private const int BtnGap    = 8;     // gap between adjacent buttons
    private const int PagePadX  = 32;    // left/right padding inside content area
    private const int StepRowH  = 40;    // sidebar step row height

    private readonly WizardState _state = new();
    private List<WizardPage> _pages = null!;
    private List<Panel>  _stepRows  = null!;
    private List<Label>  _stepTexts = null!;
    private int _current = -1;
    private ServerConfigPage _serverConfigPage = null!;

    private Panel  _content   = null!;
    private Button _btnBack   = null!;
    private Button _btnNext   = null!;
    private Button _btnCancel = null!;

    public WizardForm()
    {
        BuildShell();
        BuildPages();
        Load += (_, _) => Navigate(0);
    }

    private void BuildShell()
    {
        // Outer: 800 x 600.
        // Client on Windows 10 FixedDialog: approx 792 x 562 (4px borders, 34px title bar).
        // Sidebar=196 → content panel width ~596.
        // Content usable width = 596 - 32*2 = 532px.
        // Footer=64 → content height ~498px.
        Text            = "Raizen Server Setup";
        Size            = new Size(800, 600);
        MinimumSize     = Size;
        MaximumSize     = Size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.White;

        // ── Sidebar ──────────────────────────────────────────────────────────
        var sidebar = new Panel
        {
            BackColor = ColSidebarBg,
            Dock      = DockStyle.Left,
            Width     = SidebarW
        };

        // Logo block
        var logoTitle = new Label
        {
            Text      = "Raizen",
            Font      = new Font("Segoe UI", 17, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize  = true,
            Location  = new Point(20, 24)
        };
        var logoSub = new Label
        {
            Text      = "Security",
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(96, 165, 250),
            AutoSize  = true,
            Location  = new Point(22, 54)
        };
        var logoDivider = new Panel
        {
            BackColor = Color.FromArgb(55, 65, 81),
            Location  = new Point(0, 84),
            Size      = new Size(SidebarW, 1)
        };
        sidebar.Controls.AddRange(new Control[] { logoTitle, logoSub, logoDivider });

        // Step rows are added in BuildPages (we need page titles first)
        _stepRows  = new List<Panel>();
        _stepTexts = new List<Label>();

        // Version label at bottom of sidebar
        var versionLbl = new Label
        {
            Text      = "v1.0.0",
            Font      = new Font("Segoe UI", 7.5f),
            ForeColor = Color.FromArgb(75, 85, 99),
            AutoSize  = true,
            Location  = new Point(20, 560)
        };
        sidebar.Controls.Add(versionLbl);

        // ── Right panel (content + footer) ───────────────────────────────────
        var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

        // Footer
        var footer = new Panel
        {
            BackColor = ColFooterBg,
            Dock      = DockStyle.Bottom,
            Height    = FooterH
        };
        footer.Controls.Add(new Panel
        {
            BackColor = ColDivider,
            Dock      = DockStyle.Top,
            Height    = 1
        });

        // Buttons — all same size, right-aligned.
        // Right panel client ~596px. Right margin 16px → right edge at ~580.
        // Next:   starts at 580 - BtnW = 476
        // Back:   starts at 476 - BtnGap - BtnW = 364
        // Cancel: fixed at x=16 (left-anchored, separated from nav buttons)
        _btnNext   = MakeBtn("Next  >",  ColBtnPrimary);
        _btnBack   = MakeBtn("<  Back",  ColBtnSecondary);
        _btnCancel = MakeBtn("Cancel",   ColBtnCancel);

        _btnNext.Location   = new Point(476, BtnY);
        _btnBack.Location   = new Point(364, BtnY);
        _btnCancel.Location = new Point(16,  BtnY);

        _btnNext.Click   += (_, _) => NavigateNext();
        _btnBack.Click   += (_, _) => NavigateBack();
        _btnCancel.Click += (_, _) => Close();

        footer.Controls.AddRange(new Control[] { _btnCancel, _btnBack, _btnNext });

        // Content
        _content = new Panel { BackColor = Color.White, Dock = DockStyle.Fill };

        // DockStyle order: Bottom gets allocated first, Fill gets the rest
        rightPanel.Controls.Add(_content);
        rightPanel.Controls.Add(footer);

        // Sidebar Left first, then Fill takes the rest
        Controls.Add(rightPanel);
        Controls.Add(sidebar);
    }

    private static Button MakeBtn(string text, Color back) => new()
    {
        Text      = text,
        Font      = new Font("Segoe UI", 9, FontStyle.Bold),
        ForeColor = Color.White,
        BackColor = back,
        FlatStyle = FlatStyle.Flat,
        Size      = new Size(BtnW, BtnH),
        Cursor    = Cursors.Hand,
        FlatAppearance = { BorderSize = 0 }
    };

    private void BuildPages()
    {
        _serverConfigPage = new ServerConfigPage(_state);
        _pages =
        [
            new WelcomePage(_state),
            new TermsPage(),
            new PrerequisitesPage(_state),
            new DatabasePage(_state),
            _serverConfigPage,
            new InstallingPage(_state),
            new DonePage(_state)
        ];

        // Build sidebar step rows now that we have page titles
        var sidebar = Controls.OfType<Panel>().First(p => p.BackColor == ColSidebarBg);
        int y = 92;
        for (int i = 0; i < _pages.Count; i++)
        {
            var row = new Panel
            {
                BackColor = ColSidebarBg,
                Location  = new Point(0, y),
                Size      = new Size(SidebarW, StepRowH)
            };

            var num = new Label
            {
                Text      = $"{i + 1}",
                Font      = new Font("Segoe UI", 8),
                ForeColor = ColSidebarText,
                AutoSize  = false,
                Size      = new Size(22, StepRowH),
                Location  = new Point(20, 0),
                TextAlign = ContentAlignment.MiddleRight
            };
            var name = new Label
            {
                Text      = _pages[i].Title,
                Font      = new Font("Segoe UI", 9),
                ForeColor = ColSidebarText,
                AutoSize  = false,
                Size      = new Size(SidebarW - 52, StepRowH),
                Location  = new Point(46, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };

            row.Controls.Add(num);
            row.Controls.Add(name);
            sidebar.Controls.Add(row);
            _stepRows.Add(row);
            _stepTexts.Add(name);
            y += StepRowH;
        }

        // Wire up pages into content panel
        foreach (var page in _pages)
        {
            page.Dock    = DockStyle.Fill;
            page.Visible = false;
            _content.Controls.Add(page);
            page.CanProceedChanged += (_, _) => UpdateButtons();
        }
    }

    private async void Navigate(int index)
    {
        // Hide previous page and reset its sidebar row
        if (_current >= 0 && _current < _pages.Count)
        {
            _pages[_current].Visible = false;
            SetRowStyle(_current, done: _current < index);
        }

        _current = index;
        SetRowStyle(_current, active: true);
        _pages[_current].Visible = true;

        _btnNext.Enabled = false;
        _btnBack.Enabled = false;
        await _pages[_current].OnActivatedAsync();
        UpdateButtons();
    }

    private void SetRowStyle(int i, bool active = false, bool done = false)
    {
        var row  = _stepRows[i];
        var name = _stepTexts[i];
        var num  = (Label)row.Controls[0];

        if (active)
        {
            row.BackColor  = ColSidebarActive;
            name.ForeColor = Color.White;
            name.Font      = new Font("Segoe UI", 9, FontStyle.Bold);
            num.ForeColor  = Color.White;
        }
        else if (done)
        {
            row.BackColor  = ColSidebarDone;
            name.ForeColor = Color.FromArgb(147, 197, 253);
            name.Font      = new Font("Segoe UI", 9);
            num.ForeColor  = Color.FromArgb(147, 197, 253);
        }
        else
        {
            row.BackColor  = ColSidebarBg;
            name.ForeColor = ColSidebarText;
            name.Font      = new Font("Segoe UI", 9);
            num.ForeColor  = ColSidebarText;
        }
    }

    private void UpdateButtons()
    {
        if (InvokeRequired) { Invoke(UpdateButtons); return; }

        bool isLast    = _current == _pages.Count - 1;
        bool isInstall = _current == _pages.Count - 2;

        _btnBack.Enabled   = _current > 0 && !isInstall && !isLast;
        _btnNext.Enabled   = _pages[_current].CanProceed;
        _btnNext.Text      = isInstall ? "  Install" : isLast ? "  Close " : "Next  >";
        _btnCancel.Visible = !isLast;
    }

    private async void NavigateNext()
    {
        var page = _pages[_current];

        var error = page.Validate();
        if (error != null)
        {
            MessageBox.Show(error, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (page == _serverConfigPage)
            _serverConfigPage.CommitToState();

        if (_current == _pages.Count - 1) { Close(); return; }

        Navigate(_current + 1);
        await Task.CompletedTask;
    }

    private void NavigateBack()
    {
        if (_current > 0) Navigate(_current - 1);
    }
}
