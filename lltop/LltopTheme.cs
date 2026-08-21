using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

internal sealed record LltopThemeDefinition(
    string Name,
    Color PanelBorder,
    Color Title,
    Color Hotkey,
    Color Success,
    Color Warning,
    Color Error,
    Color Highlight,
    Color SelectedText,
    Color SelectedBackground,
    Color AnalysisBackground,
    Color AnalysisText,
    Color MemoryFullyOnGpu,
    Color MemoryTight,
    Color MemoryPartialOffload);

internal static class LltopTheme
{
    // Add future themes here. Screens consume semantic tokens below, never a theme's
    // raw RGB values, so a new palette does not change the meaning of UI states.
    static readonly IReadOnlyDictionary<string, LltopThemeDefinition> Themes =
        new Dictionary<string, LltopThemeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["midnight"] = new(
                "Midnight",
                PanelBorder: new(95, 95, 255),
                Title: new(95, 255, 255),
                Hotkey: new(95, 255, 0),
                Success: new(0, 215, 135),
                Warning: new(255, 175, 0),
                Error: new(255, 0, 0),
                Highlight: new(95, 175, 255),
                SelectedText: new(255, 255, 175),
                SelectedBackground: new(95, 95, 255),
                AnalysisBackground: new(20, 31, 55),
                AnalysisText: new(225, 235, 255),
                MemoryFullyOnGpu: new(95, 255, 255),
                MemoryTight: new(255, 175, 0),
                MemoryPartialOffload: new(255, 95, 175)),
            ["nord"] = new(
                "Nord",
                PanelBorder: new(136, 192, 208),
                Title: new(136, 192, 208),
                Hotkey: new(163, 190, 140),
                Success: new(163, 190, 140),
                Warning: new(235, 203, 139),
                Error: new(191, 97, 106),
                Highlight: new(129, 161, 193),
                SelectedText: new(236, 239, 244),
                SelectedBackground: new(94, 129, 172),
                AnalysisBackground: new(46, 52, 64),
                AnalysisText: new(236, 239, 244),
                MemoryFullyOnGpu: new(136, 192, 208),
                MemoryTight: new(235, 203, 139),
                MemoryPartialOffload: new(180, 142, 173))
        };

    static LltopThemeDefinition current = Themes["midnight"];

    internal static IReadOnlyList<string> Names => Themes.Values.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
    internal static IReadOnlyList<string> Ids => Themes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
    internal static string CurrentName => current.Name;

    internal static bool Select(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && Themes.TryGetValue(name.Trim(), out var selected))
        {
            current = selected;
            return true;
        }
        current = Themes["midnight"];
        return false;
    }

    internal static Color PanelBorder => current.PanelBorder;
    internal static Color Title => current.Title;
    internal static Color Muted => current.Hotkey;
    internal static Color Success => current.Success;
    internal static Color Warning => current.Warning;
    internal static Color Error => current.Error;
    internal static Color Highlight => current.Highlight;
    internal static Color SelectedText => current.SelectedText;
    internal static Color SelectedBackground => current.SelectedBackground;
    internal static Color MemoryFullyOnGpu => current.MemoryFullyOnGpu;
    internal static Color MemoryTight => current.MemoryTight;
    internal static Color MemoryPartialOffload => current.MemoryPartialOffload;

    internal static void Apply(
        IEnumerable<FrameView> frames,
        Label banner,
        ListView profileList,
        LogTextView logView,
        Label status,
        Label help,
        Label logStatus)
    {
        var normal = profileList.GetScheme().Normal;

        foreach (var frame in frames)
            Override(frame, _ => new TuiAttribute(PanelBorder, normal.Background));

        Override(banner, _ => new TuiAttribute(Title, normal.Background, TextStyle.Bold));
        Override(help, _ => new TuiAttribute(Muted, normal.Background, TextStyle.Faint));
        Override(logStatus, _ => new TuiAttribute(Muted, normal.Background, TextStyle.Faint));

        Override(profileList, role => role is VisualRole.Focus or VisualRole.Active
            ? new TuiAttribute(SelectedText, SelectedBackground, TextStyle.Bold)
            : normal);

        Override(logView, _ => normal);
        logView.PanelAttribute = normal;
        Override(status, _ => normal);
    }

#pragma warning disable CS0618 // Terminal.Gui 2.4 ships TextView as its built-in read-only text control.
    internal static void ApplyAnalysis(TextView report)
    {
        var analysis = new TuiAttribute(current.AnalysisText, current.AnalysisBackground);
        Override(report, _ => analysis);
        if (report is LogTextView log) log.PanelAttribute = analysis;
    }
#pragma warning restore CS0618

    private static void Override(View view, Func<VisualRole, TuiAttribute> attributeForRole)
    {
        view.GettingAttributeForRole += (_, args) =>
        {
            args.Result = attributeForRole(args.Role);
            args.Handled = true;
        };
    }
}
