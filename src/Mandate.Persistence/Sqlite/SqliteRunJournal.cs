using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Microsoft.Data.Sqlite;

namespace Mandate.Persistence.Sqlite;

/// <summary>
/// The durable audit log, in SQLite.
/// </summary>
/// <remarks>
/// <para>
/// SQLite rather than a server database because the submission has to clone and run: a
/// reviewer without a database up would otherwise see nothing. It is also genuinely
/// sufficient here — a run is a few hundred appends, and the workload is append-then-read.
/// </para>
/// <para>
/// Appends take an immediate write transaction and read the run's tail inside it, so the hash
/// chain is assembled under the same lock that inserts the row. Two nodes finishing at the
/// same instant therefore cannot produce two events claiming the same predecessor.
/// </para>
/// <para>
/// The store enforces append-only at the storage layer with triggers, not merely by
/// convention. An <c>UPDATE</c> or <c>DELETE</c> against the event table is refused whoever
/// issues it, which is what makes "this log has not been edited" a property of the store
/// rather than a claim about the code that writes to it.
/// </para>
/// </remarks>
public sealed class SqliteRunJournal : IRunJournal, IRunCatalogue, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SqliteRunJournal(SqliteConnection connection) => _connection = connection;

    /// <summary>The default store location, relative to the working directory.</summary>
    public const string DefaultPath = ".mandate/runs.db";

    /// <summary>Opens or creates a store at the given path.</summary>
    public static SqliteRunJournal Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return OpenConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
    }

    /// <summary>
    /// Opens a private in-memory store, for tests that want the real SQL path without a file.
    /// </summary>
    public static SqliteRunJournal OpenInMemory() => OpenConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = $"mandate-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString());

    private static SqliteRunJournal OpenConnection(string connectionString)
    {
        SqliteConnection connection = new(connectionString);
        connection.Open();

        using (SqliteCommand pragma = connection.CreateCommand())
        {
            // WAL keeps readers working during a write, and full synchronous means a recorded
            // approval survives a crash. Durability matters more than throughput for a log
            // whose purpose is to be evidence.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(connection);
        return new SqliteRunJournal(connection);
    }

    /// <inheritdoc />
    public async Task<RunEvent> AppendAsync(
        RunId runId, Func<RunEvent?, RunEvent> build, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using SqliteTransaction transaction =
                _connection.BeginTransaction(IsolationLevel.Serializable);

            RunEvent? tail = ReadTail(runId, transaction);
            RunEvent appended = build(tail);

            if (appended.RunId != runId)
            {
                throw new InvalidOperationException(
                    $"An event for {appended.RunId} was built while appending to {runId}.");
            }

            Insert(appended, transaction);
            transaction.Commit();

            return appended;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Loads a run's events verbatim, as read back from exported evidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not on <see cref="IRunJournal"/>. The port takes a factory precisely so
    /// the engine cannot choose an event's predecessor; import is the one operation that
    /// must supply the links, because it is restoring a chain rather than extending one, and
    /// keeping it off the port means the engine still cannot reach it.
    /// </para>
    /// <para>
    /// Events are inserted exactly as given, digests included. Nothing is recomputed, so an
    /// imported run that was altered on disk verifies as altered.
    /// </para>
    /// </remarks>
    /// <returns>How many events were written.</returns>
    /// <exception cref="InvalidOperationException">The run is already in the store.</exception>
    public async Task<int> ImportAsync(
        RunId runId, ImmutableArray<RunEvent> events, CancellationToken cancellationToken)
    {
        if (events.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A run cannot be imported with no events.", nameof(events));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using SqliteTransaction transaction =
                _connection.BeginTransaction(IsolationLevel.Serializable);

            if (ReadTail(runId, transaction) is not null)
            {
                throw new InvalidOperationException(
                    $"{runId} is already in this store. The log is append-only, so an " +
                    "existing run is never overwritten by an import.");
            }

            foreach (RunEvent @event in events)
            {
                if (@event.RunId != runId)
                {
                    throw new InvalidOperationException(
                        $"An event for {@event.RunId} was found while importing {runId}.");
                }

                Insert(@event, transaction);
            }

            transaction.Commit();
            return events.Length;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<ImmutableArray<RunEvent>> ReadAsync(
        RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, sequence, occurred_at, kind, node_id, actor,
                   payload_json, previous_hash, hash
            FROM run_events
            WHERE run_id = $runId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$runId", runId.Value);

        ImmutableArray<RunEvent>.Builder events = ImmutableArray.CreateBuilder<RunEvent>();

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            events.Add(Materialise(reader));
        }

        return Task.FromResult(events.ToImmutable());
    }

    /// <inheritdoc />
    public Task<ImmutableArray<RunSummary>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText =
            SummaryQueryHead + SummaryQueryGroup + " ORDER BY started_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        ImmutableArray<RunSummary>.Builder summaries = ImmutableArray.CreateBuilder<RunSummary>();

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            summaries.Add(Summarise(reader));
        }

        return Task.FromResult(summaries.ToImmutable());
    }

    /// <inheritdoc />
    public Task<RunSummary?> FindAsync(RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText =
            SummaryQueryHead + " AND events.run_id = $runId" + SummaryQueryGroup + ";";
        command.Parameters.AddWithValue("$runId", runId.Value);

        using SqliteDataReader reader = command.ExecuteReader();

        return Task.FromResult(reader.Read() ? Summarise(reader) : null);
    }

    /// <summary>Rebuilds a run's state from its persisted log.</summary>
    public async Task<RunState?> RebuildAsync(RunId runId, CancellationToken cancellationToken)
    {
        ImmutableArray<RunEvent> events = await ReadAsync(runId, cancellationToken)
            .ConfigureAwait(false);

        return events.IsEmpty ? null : RunState.Rebuild(runId, events);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writeLock.Dispose();
        _connection.Dispose();
    }

    // The listing is derived from the events rather than kept in a second table, so there is
    // no denormalised copy of a run's status that can drift away from its log.
    //
    // Split at the WHERE clause deliberately. A caller adding a filter has to be able to put
    // it before the grouping: appended after `GROUP BY events.run_id`, an `AND` binds to the
    // grouping expression instead of the filter, which is valid SQL that quietly matches
    // every run. Keeping the two halves separate makes that mistake unrepresentable.
    private const string SummaryQueryHead = """
        SELECT
            events.run_id                                               AS run_id,
            MIN(events.occurred_at)                                     AS started_at,
            MAX(events.occurred_at)                                     AS updated_at,
            COUNT(*)                                                    AS event_count,
            (SELECT planned.payload_json FROM run_events planned
              WHERE planned.run_id = events.run_id AND planned.sequence = 1) AS planned_json,
            (SELECT completed.payload_json FROM run_events completed
              WHERE completed.run_id = events.run_id
                AND completed.kind = 'RunCompleted'
              ORDER BY completed.sequence DESC LIMIT 1)                 AS completed_json
        FROM run_events events
        WHERE 1 = 1
        """;

    private const string SummaryQueryGroup = " GROUP BY events.run_id";

    private RunEvent? ReadTail(RunId runId, SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, sequence, occurred_at, kind, node_id, actor,
                   payload_json, previous_hash, hash
            FROM run_events
            WHERE run_id = $runId
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", runId.Value);

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read() ? Materialise(reader) : null;
    }

    private void Insert(RunEvent @event, SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO run_events
                (run_id, sequence, occurred_at, kind, node_id, actor,
                 payload_json, previous_hash, hash)
            VALUES
                ($runId, $sequence, $occurredAt, $kind, $nodeId, $actor,
                 $payloadJson, $previousHash, $hash);
            """;

        command.Parameters.AddWithValue("$runId", @event.RunId.Value);
        command.Parameters.AddWithValue("$sequence", @event.Sequence);
        command.Parameters.AddWithValue(
            "$occurredAt", @event.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$kind", @event.Kind.ToString());
        command.Parameters.AddWithValue("$nodeId", (object?)@event.NodeId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", @event.Actor.Value);
        command.Parameters.AddWithValue("$payloadJson", @event.PayloadJson);
        command.Parameters.AddWithValue("$previousHash", @event.PreviousHash.Hex);
        command.Parameters.AddWithValue("$hash", @event.Hash.Hex);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Rebuilds an event exactly as it was written, including its recorded digest.
    /// </summary>
    /// <remarks>
    /// The digest is taken from the row rather than recomputed. A mismatch between what was
    /// stored and what the contents hash to now is precisely the tampering signal verification
    /// exists to surface; recomputing here would erase it.
    /// </remarks>
    private static RunEvent Materialise(SqliteDataReader reader) => RunEvent.Rehydrate(
        sequence: reader.GetInt64(1),
        runId: RunId.Parse(reader.GetString(0)),
        occurredAt: DateTimeOffset.Parse(
            reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        kind: Enum.Parse<RunEventKind>(reader.GetString(3)),
        nodeId: reader.IsDBNull(4) ? null : NodeId.Parse(reader.GetString(4)),
        actor: Actor.Parse(reader.GetString(5)),
        payloadJson: reader.GetString(6),
        previousHash: Sha256Hash.Parse(reader.GetString(7)),
        storedHash: Sha256Hash.Parse(reader.GetString(8)));

    private static RunSummary Summarise(SqliteDataReader reader)
    {
        RunPlannedPayload? planned = reader.IsDBNull(4)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<RunPlannedPayload>(
                reader.GetString(4), Core.Serialization.MandateJson.Canonical);

        RunCompletedPayload? completed = reader.IsDBNull(5)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<RunCompletedPayload>(
                reader.GetString(5), Core.Serialization.MandateJson.Canonical);

        RunStatus status = completed is not null
                           && Enum.TryParse(completed.Status, out RunStatus parsed)
            ? parsed
            // No completion event means the process did not get to write one: the run was
            // interrupted rather than finished, and saying so is more useful than guessing.
            : RunStatus.Running;

        return new RunSummary(
            RunId.Parse(reader.GetString(0)),
            planned is null ? "unknown" : $"{planned.Workflow}@{planned.Version}",
            planned?.Scenario ?? "unknown",
            planned?.Request ?? string.Empty,
            status,
            DateTimeOffset.Parse(
                reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(
                reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt64(3));
    }
}
