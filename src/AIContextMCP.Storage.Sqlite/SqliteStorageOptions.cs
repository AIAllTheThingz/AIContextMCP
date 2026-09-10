namespace AIContextMCP.Storage.Sqlite;

public sealed class SqliteStorageOptions
{
    public required string DatabasePath { get; init; }
    public required string ArtifactRoot { get; init; }
    public int BusyTimeoutMilliseconds { get; init; } = 3_000;
}
