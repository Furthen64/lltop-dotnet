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

    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (!ReferenceEquals(styledLine, line))
        {
            styledLine = line;
            styledForeground = LogLineStyle.ForegroundFor(Cell.ToString(line)) ?? PanelAttribute.Foreground;
        }

        SetAttribute(new TuiAttribute(styledForeground, PanelAttribute.Background));
    }
}
