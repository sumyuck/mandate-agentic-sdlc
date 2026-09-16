// The starting point a run's implementation stage extends.
//
// Deliberately minimal: a health endpoint and nothing else. What a run adds here is the
// run's own output, so anything already present would muddy what the run can be said to
// have produced.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();
