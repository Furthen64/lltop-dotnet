using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

internal static class LltopTheme
{
    // RGB equivalents of the ANSI-256 colors used by go_source/internal/ui/styles.go.
    internal static readonly Color PanelBorder = new(95, 95, 255);
    internal static readonly Color Title = new(95, 255, 255);
    internal static readonly Color Muted = new(95, 255, 0);
    internal static readonly Color Success = new(0, 215, 135);
    internal static readonly Color Warning = new(255, 175, 0);
    internal static readonly Color Error = new(255, 0, 0);
    internal static readonly Color Info = new(95, 215, 255);
    internal static readonly Color Highlight = new(95, 175, 255);
    internal static readonly Color SelectedText = new(255, 255, 175);
    internal static readonly Color SelectedBackground = new(95, 95, 255);

    internal static void Apply(
        IEnumerable<FrameView> frames,
        Label banner,
        ListView profileList,
        LogTextView logView,
        Label help)
    {
        var normal = profileList.GetScheme().Normal;

        foreach (var frame in frames)
            Override(frame, _ => new TuiAttribute(PanelBorder, normal.Background));

        Override(banner, _ => new TuiAttribute(Title, normal.Background, TextStyle.Bold));
        Override(help, _ => new TuiAttribute(Muted, normal.Background, TextStyle.Faint));

        Override(profileList, role => role is VisualRole.Focus or VisualRole.Active
            ? new TuiAttribute(SelectedText, SelectedBackground, TextStyle.Bold)
            : normal);

        // TextView normally paints read-only focused content with its Focus role,
        // which is the contrasting olive background seen in the old log panel.
        Override(logView, _ => normal);
        logView.PanelAttribute = normal;
    }

    private static void Override(View view, Func<VisualRole, TuiAttribute> attributeForRole)
    {
        view.GettingAttributeForRole += (_, args) =>
        {
            args.Result = attributeForRole(args.Role);
            args.Handled = true;
        };
    }
}
