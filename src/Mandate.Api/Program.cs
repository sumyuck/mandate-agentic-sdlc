using Mandate.Core.Diagnostics;

namespace Mandate.Api;

/// <summary>
/// Read-only inspection surface over persisted runs.
/// </summary>
/// <remarks>
/// Deliberately read-only: mutating a run (approving, resuming, stopping) is an
/// audited, authenticated action and is exposed only through the CLI, which records
/// the acting human identity. An HTTP surface that could approve its own runs would
/// defeat the segregation-of-duties control.
/// </remarks>
internal static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        WebApplication app = builder.Build();

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/api/v1/engine", () => Results.Ok(new
        {
            version = BuildInfo.Version,
            fingerprint = BuildInfo.Fingerprint,
            runtime = BuildInfo.RuntimeVersion,
        }));

        app.Run();
    }
}
