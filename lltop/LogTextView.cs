using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

#pragma warning disable CS0618 // Terminal.Gui 2.4 ships TextView as its built-in scrollable read-only text control.
internal sealed class LogTextView : TextView
#pragma warning restore CS0618
{
    private List<Cell>? styledLine;
    private Color styledForeground;

    internal TuiAttribute PanelAttribute { get; set; } = TuiAttribute.Default;
    // Benchmark result tables use this to keep their columns calm while making
    // only operational severity words stand out.
    internal bool HighlightSeverityMarkersOnly { get; set; }

    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (!ReferenceEquals(styledLine, line))
        {
            styledLine = line;
            styledForeground = LogLineStyle.ForegroundFor(Cell.ToString(line)) ?? PanelAttribute.Foreground;
        }

        var foreground = HighlightSeverityMarkersOnly
            ? LogLineStyle.InlineSeverityColor(Cell.ToString(line), idxCol) ?? PanelAttribute.Foreground
            : styledForeground;
        SetAttribute(new TuiAttribute(foreground, PanelAttribute.Background));
    }
}
