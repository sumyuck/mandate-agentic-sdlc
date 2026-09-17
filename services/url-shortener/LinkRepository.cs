using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Service;

/// <summary>
/// The only component that opens a <see cref="SqliteConnection"/> or writes SQL. Owns the
/// insert-with-collision-retry algorithm, the atomic redirect-with-increment statement, and the
/// stats query. See ADR 0001 for why correctness here rests on SQLite's own locking and unique
/// index rather than application-level coordination.
/// </summary>
public sealed class LinkRepository
{
    private const int MaxGeneratedCodeAttempts = 5;
    private const int SqliteConstraintViolation = 19;

    private readonly string _connectionString;

    public LinkRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<RepositoryCreateResult> CreateAsync(string url, string? alias, DateTimeOffset? expiresAt)
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        if (alias is not null)
        {
            try
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO links(code, original_url, created_at, expires_at, click_count)
                    VALUES ($code, $url, $createdAt, $expiresAt, 0);
                    """;
                insert.Parameters.AddWithValue("$code", alias);
                insert.Parameters.AddWithValue("$url", url);
                insert.Parameters.AddWithValue("$createdAt", ToStorageString(createdAt));
                insert.Parameters.AddWithValue("$expiresAt", (object?)ToStorageString(expiresAt) ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync();
                return new RepositoryCreateResult(alias, true);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintViolation)
            {
                return new RepositoryCreateResult(null, false);
            }
        }

        for (int attempt = 0; attempt < MaxGeneratedCodeAttempts; attempt++)
        {
            long id;
            using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO links(code, original_url, created_at, expires_at, click_count)
                    VALUES (NULL, $url, $createdAt, $expiresAt, 0);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$url", url);
                insert.Parameters.AddWithValue("$createdAt", ToStorageString(createdAt));
                insert.Parameters.AddWithValue("$expiresAt", (object?)ToStorageString(expiresAt) ?? DBNull.Value);
                object? scalar = await insert.ExecuteScalarAsync();
                id = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }

            string code = CodeGenerator.Encode(id);
            try
            {
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText = "UPDATE links SET code = $code WHERE id = $id;";
                update.Parameters.AddWithValue("$code", code);
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync();
                return new RepositoryCreateResult(code, true);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintViolation)
            {
                // The generated code collided with a pre-existing alias. The row with the NULL
                // code stays behind (inert: no query ever matches code IS NULL); retry with a
                // fresh, higher id.
            }
        }

        throw new InvalidOperationException("Failed to generate a unique code after repeated attempts.");
    }

    public async Task<RedirectResult> RedirectAsync(string code)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using (SqliteCommand update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE links
                SET click_count = click_count + 1
                WHERE code = $code AND (expires_at IS NULL OR expires_at > $now)
                RETURNING original_url;
                """;
            update.Parameters.AddWithValue("$code", code);
            update.Parameters.AddWithValue("$now", ToStorageString(now));
            using SqliteDataReader reader = await update.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return RedirectResult.Redirect(reader.GetString(0));
            }
        }

        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT 1 FROM links WHERE code = $code;";
        select.Parameters.AddWithValue("$code", code);
        object? exists = await select.ExecuteScalarAsync();
        return exists is null ? RedirectResult.NotFound() : RedirectResult.Expired();
    }

    public async Task<LinkStats?> GetStatsAsync(string code)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = """
            SELECT code, original_url, created_at, expires_at, click_count
            FROM links WHERE code = $code;
            """;
        select.Parameters.AddWithValue("$code", code);

        using SqliteDataReader reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        string resolvedCode = reader.GetString(0);
        string url = reader.GetString(1);
        DateTimeOffset createdAt = ParseStorageString(reader.GetString(2))!.Value;
        DateTimeOffset? expiresAt = reader.IsDBNull(3) ? null : ParseStorageString(reader.GetString(3));
        long clickCount = reader.GetInt64(4);

        return new LinkStats(resolvedCode, url, createdAt, expiresAt, clickCount);
    }

    private static string? ToStorageString(DateTimeOffset? value) =>
        value is null ? null : value.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static string ToStorageString(DateTimeOffset value) =>
        value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseStorageString(string? value) =>
        value is null
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}