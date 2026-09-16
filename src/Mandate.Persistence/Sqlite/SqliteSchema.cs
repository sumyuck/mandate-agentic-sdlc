using Microsoft.Data.Sqlite;

namespace Mandate.Persistence.Sqlite;

/// <summary>
/// The persisted schema, and the only place it is created or migrated.
/// </summary>
/// <remarks>
/// <para>
/// One table. The event log is the sole source of truth, so there is no denormalised run
/// table to drift out of step with it — a run listing is a query over the events, not a
/// second record of the same facts.
/// </para>
/// <para>
/// The stored digest is a plain column rather than something the database derives, because
/// the whole point of verification is to compare what was written against what the contents
/// hash to now. A database-computed digest would agree with itself by construction.
/// </para>
/// </remarks>
internal static class SqliteSchema
{
    /// <summary>The schema version this build writes and expects.</summary>
    public const int Version = 1;

    public static void EnsureCreated(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version     INTEGER NOT NULL,
                applied_at  TEXT    NOT NULL
            );
            """);

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS run_events (
                run_id        TEXT    NOT NULL,
                sequence      INTEGER NOT NULL,
                occurred_at   TEXT    NOT NULL,
                kind          TEXT    NOT NULL,
                node_id       TEXT    NULL,
                actor         TEXT    NOT NULL,
                payload_json  TEXT    NOT NULL,
                previous_hash TEXT    NOT NULL,
                hash          TEXT    NOT NULL,
                PRIMARY KEY (run_id, sequence)
            );
            """);

        // A run's events are always read in sequence order, and the listing reads the first
        // and last event of every run.
        Execute(connection, transaction, """
            CREATE INDEX IF NOT EXISTS ix_run_events_run_sequence
                ON run_events (run_id, sequence);
            """);

        // The audit log is append-only by contract. Making that a database trigger means an
        // edit through any client - not just this engine - is refused at the storage layer,
        // rather than relying on every writer to behave.
        Execute(connection, transaction, """
            CREATE TRIGGER IF NOT EXISTS trg_run_events_no_update
            BEFORE UPDATE ON run_events
            BEGIN
                SELECT RAISE(ABORT, 'run_events is append-only: audit events cannot be updated');
            END;
            """);

        Execute(connection, transaction, """
            CREATE TRIGGER IF NOT EXISTS trg_run_events_no_delete
            BEFORE DELETE ON run_events
            BEGIN
                SELECT RAISE(ABORT, 'run_events is append-only: audit events cannot be deleted');
            END;
            """);

        RecordVersion(connection, transaction);

        transaction.Commit();
    }

    private static void RecordVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";

        long current = Convert.ToInt64(read.ExecuteScalar(), provider: null);

        if (current == Version)
        {
            return;
        }

        if (current > Version)
        {
            throw new InvalidOperationException(
                $"The store was written by a newer build (schema {current}); this build "
                + $"understands schema {Version}. Refusing to read it rather than risk "
                + "misinterpreting recorded evidence.");
        }

        using SqliteCommand write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText =
            "INSERT INTO schema_version (version, applied_at) VALUES ($version, $appliedAt);";
        write.Parameters.AddWithValue("$version", Version);
        write.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        write.ExecuteNonQuery();
    }

    private static void Execute(
        SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
