// The starting point a run's implementation stage extends.
//
// Deliberately minimal: a health endpoint and nothing else. What a run adds here is the
// run's own output, so anything already present would muddy what the run can be said to
// have produced.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(HealthStatus.Live));

app.Run();

/// <summary>What the liveness endpoint reports.</summary>
/// <remarks>
/// A named type rather than an anonymous object so there is something the test project can
/// actually assert against. The template ships one real test rather than none: a tree whose
/// test run finds nothing cannot tell "the suite passed" apart from "the suite never ran".
/// </remarks>
public sealed record HealthStatus(string Status)
{
    /// <summary>The service is up.</summary>
    public static HealthStatus Live { get; } = new("live");
}
