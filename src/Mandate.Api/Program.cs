using System.Collections.Immutable;
using Mandate.Core.Diagnostics;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Observability.Metrics;
using Mandate.Persistence.Sqlite;

namespace Mandate.Api;

/// <summary>
/// Read-only inspection surface over persisted runs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately read-only. Mutating a run — approving, resuming, waiving, stopping — is an
/// audited act that records who performed it, and those go through the CLI where the acting
/// human is named. An HTTP surface that could approve its own runs would defeat the
/// segregation-of-duties control this system is built around.
/// </para>
/// <para>
/// The catalogue is consumed through <see cref="IRunCatalogue"/> rather than the concrete
/// store, which is the port's justification: this is the second consumer, and it has no
/// business being able to append.
/// </para>
/// </remarks>
internal static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        string storePath = builder.Configuration["Mandate:Store"] ?? SqliteRunJournal.DefaultPath;

        SqliteRunJournal store = SqliteRunJournal.Open(storePath);

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IRunCatalogue>(store);

        WebApplication app = builder.Build();

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        app.MapGet("/api/v1/engine", () => Results.Ok(new
        {
            version = BuildInfo.Version,
            fingerprint = BuildInfo.Fingerprint,
            runtime = BuildInfo.RuntimeVersion,
            store = storePath,
        }));

        app.MapGet("/api/v1/runs", async (
            IRunCatalogue catalogue, CancellationToken cancellationToken, int limit = 20) =>
        {
            ImmutableArray<RunSummary> runs =
                await catalogue.ListAsync(limit, cancellationToken).ConfigureAwait(false);

            return Results.Ok(runs.Select(run => new
            {
                runId = run.RunId.Value,
                run.Workflow,
                run.Scenario,
                run.Request,
                status = run.Status.ToString(),
                run.StartedAt,
                run.UpdatedAt,
                run.EventCount,
                run.IsWaitingOnHuman,
            }));
        });

        app.MapGet("/api/v1/runs/{runId}", async (
            string runId,
            IRunCatalogue catalogue,
            CancellationToken cancellationToken) =>
        {
            if (!RunId.TryParse(runId, out RunId parsed))
            {
                return Results.BadRequest(new { error = $"'{runId}' is not a run id." });
            }

            RunSummary? summary =
                await catalogue.FindAsync(parsed, cancellationToken).ConfigureAwait(false);

            return summary is null
                ? Results.NotFound(new { error = $"No run {runId}." })
                : Results.Ok(new
                {
                    runId = summary.RunId.Value,
                    summary.Workflow,
                    summary.Scenario,
                    summary.Request,
                    status = summary.Status.ToString(),
                    summary.StartedAt,
                    summary.UpdatedAt,
                    summary.EventCount,
                });
        });

        app.MapGet("/api/v1/runs/{runId}/metrics", async (
            string runId,
            SqliteRunJournal journal,
            CancellationToken cancellationToken) =>
        {
            if (!RunId.TryParse(runId, out RunId parsed))
            {
                return Results.BadRequest(new { error = $"'{runId}' is not a run id." });
            }

            ImmutableArray<RunEvent> events =
                await journal.ReadAsync(parsed, cancellationToken).ConfigureAwait(false);

            if (events.IsEmpty)
            {
                return Results.NotFound(new { error = $"No run {runId}." });
            }

            RunMetrics metrics = MetricsCalculator.Compute(parsed, events);
            AuditVerification verification = AuditChain.Verify(parsed, events);

            return Results.Ok(new
            {
                runId = metrics.RunId.Value,
                status = metrics.Status.ToString(),
                chainIntact = verification.IsIntact,
                successRate = metrics.SuccessRate,
                retryRate = metrics.RetryRate,
                rollbackRate = metrics.RollbackRate,
                gateBlockRate = metrics.GateBlockRate,
                autonomyRatio = metrics.AutonomyRatio,
                endToEndSeconds = metrics.EndToEnd.TotalSeconds,
                meanTimeToRecoverySeconds = metrics.MeanTimeToRecovery?.TotalSeconds,
                unrecoveredFailures = metrics.UnrecoveredFailures,
                replansPerformed = metrics.ReplansPerformed,
                eventCount = metrics.EventCount,
            });
        });

        app.Run();
    }
}
