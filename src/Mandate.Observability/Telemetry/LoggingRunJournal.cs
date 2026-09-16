using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Microsoft.Extensions.Logging;

namespace Mandate.Observability.Telemetry;

/// <summary>
/// Emits a structured log line for every audit event, without changing what is recorded.
/// </summary>
/// <remarks>
/// <para>
/// A decorator rather than a change to the engine, and rather than a second place that
/// decides what is worth logging. The audit log already answers "what happened"; the
/// operational log answers "what is happening", and the two must never disagree — so they
/// come from the same source.
/// </para>
/// <para>
/// Fields are flat and consistently named so a log platform can correlate on them:
/// <c>runId</c>, <c>nodeId</c>, <c>sequence</c>, <c>actor</c>. The digest is included so a
/// line in an operational log can be tied back to the exact audit event it describes.
/// </para>
/// </remarks>
public sealed class LoggingRunJournal(IRunJournal inner, ILogger<LoggingRunJournal> logger)
    : IRunJournal
{
    /// <inheritdoc />
    public async Task<RunEvent> AppendAsync(
        RunId runId, Func<RunEvent?, RunEvent> build, CancellationToken cancellationToken)
    {
        RunEvent appended = await inner.AppendAsync(runId, build, cancellationToken)
            .ConfigureAwait(false);

        // Severity follows what the event means for the run, not how interesting it looks.
        LogLevel level = appended.Kind switch
        {
            RunEventKind.RunCompleted or RunEventKind.NodeStateChanged
                or RunEventKind.ApprovalRequested or RunEventKind.ReplanPerformed => LogLevel.Information,

            RunEventKind.PolicyViolationBlocked or RunEventKind.NodeRetryBudgetExhausted
                or RunEventKind.SafeStopRequested or RunEventKind.ReplanRefused => LogLevel.Warning,

            RunEventKind.NodeCompensationStarted or RunEventKind.WorkspaceReverted =>
                LogLevel.Warning,

            _ => LogLevel.Debug,
        };

        // Guarded: the digest and actor formatting are wasted work when the level is off,
        // and on a busy run this is called for every event.
        if (!logger.IsEnabled(level))
        {
            return appended;
        }

#pragma warning disable CA2254 // The template is fixed; the values vary.
        logger.Log(
            level,
            "{Event} runId={RunId} nodeId={NodeId} actor={Actor} sequence={Sequence} digest={Digest}",
            appended.Kind,
            appended.RunId.Value,
            appended.NodeId?.Value ?? "-",
            appended.Actor.Value,
            appended.Sequence,
            appended.Hash.Abbreviated);
#pragma warning restore CA2254

        return appended;
    }

    /// <inheritdoc />
    public Task<ImmutableArray<RunEvent>> ReadAsync(
        RunId runId, CancellationToken cancellationToken) =>
        inner.ReadAsync(runId, cancellationToken);
}
