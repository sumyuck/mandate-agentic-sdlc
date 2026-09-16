namespace Mandate.Cli.Tests.Support;

/// <summary>A scratch directory that cleans itself up.</summary>
internal sealed class TemporaryWorkspace : IDisposable
{
    public TemporaryWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"mandate-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>A run store inside the scratch directory, so tests never touch the real one.</summary>
    public string Store => Path.Combine(Root, "runs.db");

    public string Path_(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);

    public string Write(string name, string content)
    {
        string path = Path_(name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(Root))
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A file lock on a temp directory is not worth failing a test over.
            }
        }
    }
}
