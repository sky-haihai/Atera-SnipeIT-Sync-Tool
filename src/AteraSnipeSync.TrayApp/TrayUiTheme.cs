namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Defines the shared Ant Design-inspired light palette and control styling used by the Tray windows.
/// </summary>
internal static class TrayUiTheme
{
    public const int WindowRadius = 10;
    public const int WindowShadow = 10;
    public const int SurfaceRadius = 10;
    public const int NestedSurfaceRadius = 8;
    public const int ControlRadius = 6;

    public static readonly Size DashboardActionButtonSize = new(156, 40);
    public static readonly Color Canvas = Color.FromArgb(241, 243, 246);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(217, 222, 231);
    public static readonly Color SurfaceHover = Color.FromArgb(246, 248, 250);
    public static readonly Color SurfaceActive = Color.FromArgb(238, 242, 246);
    public static readonly Color SurfaceShadow = Color.FromArgb(52, 64, 84);
    public static readonly Color WindowShadowColor = Color.FromArgb(100, SurfaceShadow);
    public static readonly Color Primary = Color.FromArgb(22, 119, 255);
    public static readonly Color PrimaryHover = Color.FromArgb(64, 150, 255);
    public static readonly Color Text = Color.FromArgb(38, 38, 38);
    public static readonly Color MutedText = Color.FromArgb(140, 140, 140);
    public static readonly Color Success = Color.FromArgb(22, 132, 87);
    public static readonly Color Warning = Color.FromArgb(180, 96, 12);
    public static readonly Color Danger = Color.FromArgb(190, 54, 54);

    /// <summary>
    /// Applies the shared typography, background, scaling, and initial positioning to a top-level window.
    /// </summary>
    public static void ApplyWindow(AntdUI.BorderlessForm form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = Canvas;
        form.BorderColor = Border;
        form.BorderWidth = 1;
        form.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        form.ForeColor = Text;
        form.Radius = WindowRadius;
        form.Shadow = WindowShadow;
        form.ShadowColor = WindowShadowColor;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.UseDwm = false;
    }

    /// <summary>
    /// Styles a button according to its action emphasis while preserving the standard WinForms accessibility behavior.
    /// </summary>
    public static void StyleButton(AntdUI.Button button, ButtonKind kind = ButtonKind.Secondary)
    {
        button.AutoSize = true;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        button.Margin = new Padding(0, 0, 10, 8);
        button.MinimumSize = new Size(116, 38);
        button.Padding = new Padding(14, 0, 14, 0);
        button.Radius = ControlRadius;

        switch (kind)
        {
            case ButtonKind.Primary:
                button.Type = AntdUI.TTypeMini.Primary;
                break;
            case ButtonKind.Danger:
                button.Type = AntdUI.TTypeMini.Error;
                button.Ghost = true;
                break;
            default:
                button.Type = AntdUI.TTypeMini.Default;
                button.BackActive = SurfaceActive;
                button.BackHover = SurfaceHover;
                button.BorderWidth = 1;
                button.DefaultBack = Surface;
                button.DefaultBorderColor = Border;
                break;
        }
    }

    /// <summary>
    /// Styles one Dashboard run action at a fixed logical size that WinForms scales for DPI without consuming extra table-cell space.
    /// </summary>
    public static void StyleDashboardActionButton(
        AntdUI.Button button,
        ButtonKind kind = ButtonKind.Secondary)
    {
        StyleButton(button, kind);
        button.Anchor = AnchorStyles.None;
        button.AutoSize = false;
        button.Dock = DockStyle.None;
        button.Margin = Padding.Empty;
        button.MaximumSize = DashboardActionButtonSize;
        button.MinimumSize = DashboardActionButtonSize;
        button.Size = DashboardActionButtonSize;
    }

    /// <summary>
    /// Creates a bordered titleless surface around caller-provided content with explicit inner spacing.
    /// </summary>
    public static AntdUI.Panel CreateSurface(Control content, Padding padding)
    {
        var surface = new AntdUI.Panel
        {
            Back = Surface,
            BorderColor = Border,
            BorderWidth = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(5),
            Padding = padding,
            Radius = SurfaceRadius,
            Shadow = 6,
            ShadowColor = SurfaceShadow,
            ShadowOffsetY = 2,
            ShadowOpacity = 0.12F
        };
        content.Dock = DockStyle.Fill;
        surface.Controls.Add(content);
        return surface;
    }

    /// <summary>
    /// Creates a bordered surface with a consistent title area and a caller-provided content control.
    /// </summary>
    public static AntdUI.Panel CreateCard(string title, Control content)
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Surface,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new AntdUI.Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Text,
            Text = title
        }, 0, 0);
        content.Dock = DockStyle.Fill;
        layout.Controls.Add(content, 0, 1);
        return CreateSurface(layout, new Padding(14, 10, 14, 12));
    }

    internal enum ButtonKind
    {
        Secondary,
        Primary,
        Danger
    }
}
