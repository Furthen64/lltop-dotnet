using System.Drawing;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

// A deliberately roomy treatment for the one phase where an LLM server can be
// silent for minutes: ingesting a large prompt.  It is separate from the normal
// metrics label so ordinary request statistics stay compact.
internal sealed class RequestMetricsView : View
{
    PromptReadingProgress? progress;

    internal RequestMetricsView()
    {
        CanFocus = false;
        Height = 5;
        Visible = false;
    }

    internal PromptReadingProgress? Progress
    {
        get => progress;
        set
        {
            progress = value;
            Visible = value is not null;
            SetNeedsDraw();
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetScheme().Normal;
        SetAttribute(normal);
        FillRect(new Rectangle(0, 0, Viewport.Width, Viewport.Height), new Rune(' '));
        if (progress is not { } value || Viewport.Width < 12) return true;

        var percent = Math.Clamp((int)Math.Round(value.Fraction * 100, MidpointRounding.AwayFromZero), 0, 100);
        var heading = $"INPUT READING  ·  {percent}%";
        var eta = Eta(value);
        Write(0, heading, new TuiAttribute(LltopTheme.Highlight, normal.Background, TextStyle.Bold));
        if (heading.Length + eta.Length < Viewport.Width)
            WriteRight(0, eta, new TuiAttribute(LltopTheme.Success, normal.Background, TextStyle.Bold));

        var barWidth = Math.Max(1, Viewport.Width - 2);
        var filled = Math.Clamp((int)Math.Round(value.Fraction * barWidth, MidpointRounding.AwayFromZero), 0, barWidth);
        Move(0, 1);
        SetAttribute(new TuiAttribute(LltopTheme.PanelBorder, normal.Background));
        AddRune('[');
        SetAttribute(new TuiAttribute(LltopTheme.Highlight, normal.Background, TextStyle.Bold));
        AddStr(new string('█', filled));
        SetAttribute(new TuiAttribute(LltopTheme.Muted, normal.Background, TextStyle.Faint));
        AddStr(new string('░', barWidth - filled));
        SetAttribute(new TuiAttribute(LltopTheme.PanelBorder, normal.Background));
        AddRune(']');

        var total = value.EstimatedTotalTokens > 0 ? $"~{value.EstimatedTotalTokens:N0}" : "estimating";
        Write(3, $"{value.ReadTokens:N0} / {total} tokens", normal);
        WriteRight(3, value.TokensPerSecond > 0 ? $"{value.TokensPerSecond:F1} tok/s" : "measuring rate…", normal);

        var window = value.UsesRollingWindow
            ? $"rolling {FormatDuration(value.SampleDuration)} sample"
            : "rolling 5m sample warming up";
        Write(4, $"{window}  ·  Output waiting for generation…", new TuiAttribute(LltopTheme.Muted, normal.Background));
        return true;
    }

    void Write(int y, string text, TuiAttribute attribute)
    {
        if (y >= Viewport.Height) return;
        Move(0, y);
        SetAttribute(attribute);
        AddStr(Clip(text, Viewport.Width));
    }

    void WriteRight(int y, string text, TuiAttribute attribute)
    {
        if (y >= Viewport.Height || text.Length >= Viewport.Width) return;
        Move(Viewport.Width - text.Length, y);
        SetAttribute(attribute);
        AddStr(text);
    }

    static string Eta(PromptReadingProgress value) => value.EstimatedRemaining is { } remaining
        ? $"~{FormatDuration(remaining)} left"
        : "estimating time…";

    static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:D2}";

    static string Clip(string value, int width) => value.Length <= width ? value : value[..Math.Max(0, width)];
}
