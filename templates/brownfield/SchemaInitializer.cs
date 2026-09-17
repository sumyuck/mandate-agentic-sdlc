using Microsoft.Data.Sqlite;

namespace Service;

/// <summary>Creates the schema if absent and sets the pragmas that make concurrent access safe.</summary>
public static class SchemaInitializer
{
    public static void Initialize(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (SqliteCommand pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragmaCommand.ExecuteNonQuery();
        }

        using SqliteCommand createCommand = connection.CreateCommand();
        createCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS links (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                code          TEXT UNIQUE,
                original_url  TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                expires_at    TEXT,
                click_count   INTEGER NOT NULL DEFAULT 0
            );
            """;
        createCommand.ExecuteNonQuery();
    }
}