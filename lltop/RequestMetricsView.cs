using System.Drawing;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

// A deliberately roomy, server-side view of the current request. llama-server
// cannot see client reasoning or tool execution, but it can reliably expose the
// request boundaries, prompt ingestion, generation, and release phases.
internal sealed class RequestMetricsView : View
{
    RequestActivity? activity;
    PromptReadingProgress? progress;

    internal RequestMetricsView()
    {
        CanFocus = false;
        Height = 5;
        Visible = false;
    }

    internal RequestActivity? Activity
    {
        get => activity;
        set
        {
            activity = value;
            Visible = value is not null;
            SetNeedsDraw();
        }
    }

    internal PromptReadingProgress? Progress
    {
        get => progress;
        set
        {
            progress = value;
            SetNeedsDraw();
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetScheme().Normal;
        SetAttribute(normal);
        FillRect(new Rectangle(0, 0, Viewport.Width, Viewport.Height), new Rune(' '));
        if (activity is not { } request || Viewport.Width < 12) return true;

        var heading = Heading(request);
        var elapsed = request.Phase == RequestPhase.Ready ? "" : FormatDuration(request.Elapsed);
        Write(0, heading, new TuiAttribute(LltopTheme.Highlight, normal.Background, TextStyle.Bold));
        if (heading.Length + elapsed.Length < Viewport.Width)
            WriteRight(0, elapsed, new TuiAttribute(LltopTheme.Success, normal.Background, TextStyle.Bold));

        Write(1, Timeline(request), normal);

        if (progress is { } reading && request.Phase == RequestPhase.Ingesting)
        {
            var total = reading.EstimatedTotalTokens > 0 ? $"~{reading.EstimatedTotalTokens:N0}" : "estimating";
            DrawProgressBar(reading.Fraction, normal);
            Write(3, $"Input  {reading.ReadTokens:N0} / {total} tokens", normal);
            WriteRight(3, reading.TokensPerSecond > 0 ? $"{reading.TokensPerSecond:F1} tok/s" : "measuring rate…", normal);
            var eta = Eta(reading);
            var window = reading.UsesRollingWindow ? $"rolling {FormatDuration(reading.SampleDuration)} sample" : "rolling sample warming up";
            Write(4, $"{window}  ·  {eta}", new TuiAttribute(LltopTheme.Muted, normal.Background));
        }
        else
        {
            Write(2, Detail(request), new TuiAttribute(LltopTheme.Muted, normal.Background));
            Write(4, "Server phases only · client thought and tools happen between requests", new TuiAttribute(LltopTheme.Muted, normal.Background));
        }
        return true;
    }

    static string Heading(RequestActivity request) => request.Phase switch
    {
        RequestPhase.Ready => "SERVER READY  ·  waiting for a client request",
        RequestPhase.Accepted => $"REQUEST {request.Number}  ·  accepted",
        RequestPhase.Ingesting => "REQUEST " + request.Number + "  ·  ingesting input",
        RequestPhase.Generating => "REQUEST " + request.Number + "  ·  generating output",
        RequestPhase.Completed => "REQUEST " + request.Number + "  ·  completed",
        _ => "REQUEST TIMELINE"
    };

    static string Timeline(RequestActivity request) => string.Join("─", [
        Step("Start", request.Phase != RequestPhase.Ready && request.Phase != RequestPhase.Accepted, request.Phase == RequestPhase.Accepted),
        Step("Ingest", request.SawIngest && request.Phase != RequestPhase.Ingesting, request.Phase == RequestPhase.Ingesting),
        Step("Output", request.SawOutput && request.Phase != RequestPhase.Generating, request.Phase == RequestPhase.Generating),
        Step("Done", request.Phase == RequestPhase.Completed, request.Phase == RequestPhase.Completed)
    ]);

    static string Step(string text, bool done, bool current) => done ? $"[✓ {text}]" : current ? $"[● {text}]" : $"[○ {text}]";

    static string Detail(RequestActivity request)
    {
        if (request.Phase == RequestPhase.Ready) return "No server request has been observed yet.";
        if (request.Phase == RequestPhase.Completed) return $"Last request completed {FormatDuration(request.SinceLastActivity)} ago.";
        return request.TelemetryQuiet
            ? $"Request remains active · no server telemetry for {FormatDuration(request.SinceLastActivity)}."
            : $"Request remains active · last server telemetry {FormatDuration(request.SinceLastActivity)} ago.";
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

    void DrawProgressBar(double fraction, TuiAttribute normal)
    {
        if (Viewport.Width < 3 || Viewport.Height < 3) return;
        var width = Viewport.Width - 2;
        var filled = Math.Clamp((int)Math.Round(Math.Clamp(fraction, 0, 1) * width, MidpointRounding.AwayFromZero), 0, width);
        Move(0, 2);
        SetAttribute(new TuiAttribute(LltopTheme.PanelBorder, normal.Background));
        AddRune('[');
        SetAttribute(new TuiAttribute(LltopTheme.Highlight, normal.Background, TextStyle.Bold));
        AddStr(new string('#', filled));
        SetAttribute(new TuiAttribute(LltopTheme.Muted, normal.Background, TextStyle.Faint));
        AddStr(new string('.', width - filled));
        SetAttribute(new TuiAttribute(LltopTheme.PanelBorder, normal.Background));
        AddRune(']');
    }

    static string Eta(PromptReadingProgress value) => value.EstimatedRemaining is { } remaining
        ? $"~{FormatDuration(remaining)} left"
        : "estimating time…";

    static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:D2}";

    static string Clip(string value, int width) => value.Length <= width ? value : value[..Math.Max(0, width)];
}
