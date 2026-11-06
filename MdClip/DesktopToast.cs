using System.Drawing.Drawing2D;

namespace MdClip;

// One thing a desktop notice can do: what it is called in its menu, and running it.
sealed record ToastChoice(string Title, string Sub, Action Run);

// A notice at the bottom right of the desktop that looks like the page's own (app.css .toast) in the current
// theme: for a clipboard find while MdClip is minimized, behind other windows or in the tray. Shown without
// taking the focus or a taskbar button; it goes after 15 seconds, or later while the pointer is on it.
// Without a text it is only the action's button, beside the pointer for a quick click, gone after 5 seconds.
// With one choice the button runs it; with several the button is a menu that opens under the pointer.
sealed class DesktopToast : Form
{
    const int RowHeight = 30, MaxRows = 10, MaxRowWidth = 460;

    readonly System.Windows.Forms.Timer life = new();
    readonly bool besidePointer;
    readonly FlowLayoutPanel? menu;
    Point pointer;

    public DesktopToast(Theme theme, string? text, string action, IReadOnlyList<ToastChoice> choices)
    {
        besidePointer = text is null;
        life.Interval = besidePointer ? 5000 : 15000;
        // As .toast: the theme's text colour as the background, its background colour as the text.
        Color back = theme.Color("fg"), fore = theme.Color("bg"), accent = theme.Color("accent");
        var hover = Blend(back, fore, 0.18);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = back;
        Font = new Font("Microsoft JhengHei UI", 10f);

        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty,
            Padding = besidePointer ? new Padding(4) : new Padding(14, 8, 8, 8), BackColor = back,
        };
        var act = new Button
        {
            Text = choices.Count > 1 ? action + " ▾" : action, AutoSize = true, MinimumSize = new Size(0, RowHeight), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = accent, ForeColor = fore, Font = new Font(Font, FontStyle.Bold), Margin = besidePointer ? Padding.Empty : new Padding(0, 0, 6, 0),
        };
        act.FlatAppearance.BorderSize = 0;
        if (besidePointer) row.Controls.Add(act);
        else
        {
            var message = new Label { Text = text, AutoSize = true, MaximumSize = new Size(MaxRowWidth, 0), ForeColor = fore, Margin = new Padding(0, 6, 10, 6) };
            var close = new Button
            {
                Text = "✕", Size = new Size(RowHeight, RowHeight), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = back, ForeColor = fore, Margin = Padding.Empty,
            };
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = hover;
            close.Click += (_, _) => Close();
            row.Controls.AddRange([message, act, close]);
        }

        var column = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Margin = Padding.Empty, BackColor = back,
        };
        if (choices.Count > 1)
        {
            menu = Menu(choices, back, fore, hover);
            act.MouseEnter += (_, _) => OpenMenu();
            act.Click += (_, _) => OpenMenu();
        }
        else act.Click += (_, _) => { Close(); choices[0].Run(); };
        // The menu opens away from the screen's edge: above the notice in the corner, below the button by the pointer.
        if (menu is not null && !besidePointer) column.Controls.Add(menu);
        column.Controls.Add(row);
        if (menu is not null && besidePointer) column.Controls.Add(menu);
        Controls.Add(column);

        life.Tick += (_, _) => { if (!Bounds.Contains(Cursor.Position)) Close(); };
        Disposed += (_, _) => life.Dispose();
    }

    FlowLayoutPanel Menu(IReadOnlyList<ToastChoice> choices, Color back, Color fore, Color hover)
    {
        var labels = choices.Select(c => c.Sub.Length > 0 ? $"{c.Title}  ·  {c.Sub}" : c.Title).ToList();
        var width = Math.Min(MaxRowWidth, labels.Max(l => TextRenderer.MeasureText(l, Font).Width) + 28);
        var list = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false, Margin = Padding.Empty, Padding = new Padding(4), BackColor = back,
        };
        // A long list scrolls rather than leaving the screen.
        var many = choices.Count > MaxRows;
        list.AutoScroll = many;
        list.AutoSize = !many;
        list.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        if (many) list.Size = new Size(width + SystemInformation.VerticalScrollBarWidth + 8, MaxRows * RowHeight + 8);
        for (int i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var item = new Button
            {
                Text = labels[i], Size = new Size(width, RowHeight), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = back, ForeColor = fore, Margin = Padding.Empty,
            };
            item.FlatAppearance.BorderSize = 0;
            item.FlatAppearance.MouseOverBackColor = hover;
            item.Click += (_, _) => { Close(); choice.Run(); };
            list.Controls.Add(item);
        }
        return list;
    }

    // Open, the one beside the pointer goes soon after the pointer leaves it: it covers more of the screen now.
    void OpenMenu()
    {
        if (menu is null || menu.Visible) return;
        menu.Visible = true;
        if (!besidePointer) return;
        life.Stop();
        life.Interval = 1500;
        life.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        pointer = Cursor.Position;
        Fit();
        life.Start();
    }

    // The menu opening changes the size.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (life.Enabled) Fit();
    }

    // Where the page's toast sits in its window (right 20, bottom 18), on the desktop; or below and to the right of the
    // pointer, clear of it and kept on its screen. Rounded like the page's.
    void Fit()
    {
        if (besidePointer)
        {
            var screen = Screen.FromPoint(pointer).WorkingArea;
            Location = new Point(Math.Clamp(pointer.X + 12, screen.Left, screen.Right - Width), Math.Clamp(pointer.Y + 16, screen.Top, screen.Bottom - Height));
        }
        else
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Bottom - Height - 18);
        }
        using var path = new GraphicsPath();
        var d = 16;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(Width - d, 0, d, d, 270, 90);
        path.AddArc(Width - d, Height - d, d, d, 0, 90);
        path.AddArc(0, Height - d, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
