using System.Drawing;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

internal sealed class ResourceStripView : View
{
    private SystemResourceSnapshot snapshot = new();

    internal ResourceStripView()
    {
        CanFocus = false;
        Height = 1;
    }

    internal SystemResourceSnapshot Snapshot
    {
        get => snapshot;
        set
        {
            snapshot = value;
            SetNeedsDraw();
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetScheme().Normal;
        SetAttribute(normal);
        FillRect(new Rectangle(0, 0, Viewport.Width, 1), new Rune(' '));
        Move(0, 0);
        foreach (var segment in ResourceStripFormatter.Format(snapshot, Viewport.Width).Segments)
        {
            SetAttribute(segment.Threshold switch
            {
                ResourceThreshold.Warning => new TuiAttribute(LltopTheme.Warning, normal.Background, TextStyle.Bold),
                ResourceThreshold.Critical => new TuiAttribute(LltopTheme.Error, normal.Background, TextStyle.Bold),
                _ => normal
            });
            AddStr(segment.Text);
        }
        return true;
    }
}
