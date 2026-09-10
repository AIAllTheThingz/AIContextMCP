using AIContextMCP.Storage.Sqlite;

namespace AIContextMCP.Storage.Sqlite.Tests;

internal sealed class TemporaryDatabase : IAsyncDisposable
{
    private static readonly string BaseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AIContextMCP.Storage.Tests"));

    private TemporaryDatabase(string directory, SqliteContextStorage storage)
    {
        Directory = directory;
        DatabasePath = Path.Combine(directory, "storage.db");
        Storage = storage;
    }

    public string Directory { get; }
    public string DatabasePath { get; }
    public SqliteContextStorage Storage { get; }

    public static TemporaryDatabase Create(int busyTimeoutMilliseconds = 3_000)
    {
        var directory = Path.Combine(BaseDirectory, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "storage.db");
        var artifactRoot = Path.Combine(directory, "artifacts");
        System.IO.Directory.CreateDirectory(artifactRoot);
        return new TemporaryDatabase(directory, new SqliteContextStorage(new SqliteStorageOptions
        {
            DatabasePath = databasePath,
            ArtifactRoot = artifactRoot,
            BusyTimeoutMilliseconds = busyTimeoutMilliseconds
        }));
    }

    public ValueTask DisposeAsync()
    {
        DeleteOwnedDirectory(Directory);

        return ValueTask.CompletedTask;
    }

    internal static void DeleteOwnedDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var relativePath = Path.GetRelativePath(BaseDirectory, fullPath);
        if (relativePath is "." or ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Refusing to delete a test directory outside {BaseDirectory}.");
        }

        try
        {
            if (System.IO.Directory.Exists(fullPath))
            {
                System.IO.Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Temporary test database cleanup failed: {fullPath}", exception);
        }
    }
}
