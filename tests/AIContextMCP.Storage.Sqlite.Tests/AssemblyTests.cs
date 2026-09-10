using System.Reflection;
using Xunit;

namespace AIContextMCP.Storage.Sqlite.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void StorageAssemblyLoads() => Assert.NotNull(Assembly.Load("AIContextMCP.Storage.Sqlite"));

    [Fact]
    public void ConstructingStorageDoesNotCreateTheProductionDatabase()
    {
        const string databasePath = "D:\\Astra\\mcp\\AIContextMCP\\data\\AIContextMCP.db";
        var existed = File.Exists(databasePath);
        var length = existed ? new FileInfo(databasePath).Length : 0L;
        _ = new AIContextMCP.Storage.Sqlite.SqliteContextStorage(new AIContextMCP.Storage.Sqlite.SqliteStorageOptions
        {
            DatabasePath = databasePath,
            ArtifactRoot = "D:\\Astra\\mcp\\AIContextMCP\\data\\artifacts"
        });

        Assert.Equal(existed, File.Exists(databasePath));
        Assert.Equal(length, existed ? new FileInfo(databasePath).Length : 0L);
    }
}
