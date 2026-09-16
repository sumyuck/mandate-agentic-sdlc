using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Mandate.Core.Runs;
using Mandate.Observability.Metrics;

namespace Mandate.Observability.Reporting;

/// <summary>
/// Draws the run's stages as a timeline, in plain SVG.
/// </summary>
/// <remarks>
/// Hand-drawn rather than delegated to a charting library so the report stays a single file
/// with no script and no network dependency. The shape of a run — what overlapped, what
/// waited, what was retried — is the thing a reviewer reads first, and it should not require
/// anything to be fetched.
/// </remarks>
internal static class TimelineSvg
{
    private const int RowHeight = 26;
    private const int LabelWidth = 170;
    private const int ChartWidth = 620;
    private const int Padding = 12;

    public static string Render(RunMetrics metrics)
    {
        ImmutableArray<StageTiming> stages = metrics.Stages;

        if (stages.IsEmpty)
        {
            return "<p class=\"muted\">No stage executed, so there is no timeline to draw.</p>";
        }

        // Scaled to the longest stage rather than to wall-clock time: a run parked overnight
        // waiting for a signature would otherwise compress every stage into one pixel.
        double longest = Math.Max(1, stages.Max(stage => stage.Duration.TotalMilliseconds));

        int height = (stages.Length * RowHeight) + (Padding * 2) + 18;
        int width = LabelWidth + ChartWidth + (Padding * 2);

        StringBuilder svg = new();
        svg.Append(CultureInfo.InvariantCulture,
            $"""<svg viewBox="0 0 {width} {height}" role="img" aria-label="Stage timeline" xmlns="http://www.w3.org/2000/svg">""");

        for (int index = 0; index < stages.Length; index++)
        {
            StageTiming stage = stages[index];
            int y = Padding + (index * RowHeight);

            double fraction = stage.Duration.TotalMilliseconds / longest;
            int barWidth = Math.Max(3, (int)(fraction * ChartWidth));

            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{Padding}" y="{y + 15}" font-size="12" fill="#1a1d21" font-family="ui-monospace, Menlo, monospace">{Escape(Truncate(stage.NodeId.Value))}</text>""");

            svg.Append(CultureInfo.InvariantCulture,
                $"""<rect x="{LabelWidth}" y="{y + 5}" width="{ChartWidth}" height="14" fill="#f2f4f6" rx="3"/>""");

            svg.Append(CultureInfo.InvariantCulture,
                $"""<rect x="{LabelWidth}" y="{y + 5}" width="{barWidth}" height="14" fill="{Colour(stage.State)}" rx="3"><title>{Escape(stage.NodeId.Value)}: {Describe(stage)}</title></rect>""");

            string annotation = stage.Attempts > 1
                ? $"{Format(stage.Duration)}  ({stage.Attempts} attempts)"
                : Format(stage.Duration);

            svg.Append(CultureInfo.InvariantCulture,
                $"""<text x="{LabelWidth + barWidth + 6}" y="{y + 16}" font-size="11" fill="#5b6570">{Escape(annotation)}</text>""");
        }

        svg.Append(CultureInfo.InvariantCulture,
            $"""<text x="{Padding}" y="{height - 4}" font-size="11" fill="#5b6570">Bars are scaled to the longest stage, not to wall-clock time.</text>""");

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string Colour(NodeState state) => state switch
    {
        NodeState.Succeeded => "#2e9e52",
        NodeState.Failed => "#b3261e",
        NodeState.RolledBack => "#d06a5e",
        NodeState.AwaitingApproval => "#e0a23c",
        NodeState.Blocked => "#e0a23c",
        NodeState.Skipped => "#c3c9d0",
        _ => "#8a9199",
    };

    private static string Describe(StageTiming stage) =>
        $"{stage.State}, {Format(stage.Duration)}, {stage.Attempts} attempt(s)";

    private static string Format(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s"
            : $"{duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms";

    private static string Truncate(string value) =>
        value.Length <= 22 ? value : value[..19] + "...";

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
