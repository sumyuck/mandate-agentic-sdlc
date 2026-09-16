using Mandate.Core.Execution;
using Mandate.Core.Identifiers;

namespace Mandate.Persistence;

/// <summary>
/// Signals a safe stop through a file on disk.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than a signal or an in-process flag, because the operator asking a run to
/// stop is usually in a different process from the run — a second terminal, a supervisor, a
/// CI job. A path both can see is the simplest thing that works across all of them.
/// </para>
/// <para>
/// The request outlives the process on purpose: a run restarted while a stop is outstanding
/// should still stop.
/// </para>
/// </remarks>
public sealed class FileSafeStopMonitor(string directory) : ISafeStopMonitor
{
    /// <summary>Where stop requests are written by default.</summary>
    public const string DefaultDirectory = ".mandate/stop";

    /// <inheritdoc />
    public Task<bool> IsStopRequestedAsync(RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(PathFor(runId)));
    }

    /// <summary>Asks a run to stop at its next safe boundary.</summary>
    public async Task RequestAsync(RunId runId, string requestedBy, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            PathFor(runId),
            $"Requested by {requestedBy} at {DateTimeOffset.UtcNow:O}.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Withdraws a stop request.</summary>
    public void Clear(RunId runId)
    {
        string path = PathFor(runId);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(RunId runId) => Path.Combine(directory, $"{runId.Value}.stop");
}
