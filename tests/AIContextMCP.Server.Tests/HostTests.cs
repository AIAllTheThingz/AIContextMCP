using AIContextMCP.Core;
using AIContextMCP.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIContextMCP.Server.Tests;

public sealed class HostTests
{
    [Fact]
    public void DefaultOptionsUsePortableStoragePaths()
    {
        var options = new ServerOptions();
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");

        Assert.Equal(Path.Combine(dataDirectory, "AIContextMCP.db"), options.DatabasePath);
        Assert.Equal(Path.Combine(dataDirectory, "artifacts"), options.ArtifactRoot);
        Assert.Equal([@"D:\Projects"], options.ApprovedRepositoryRoots);
    }

    [Fact]
    public async Task HostComposesTypedOptionsAndLogging()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var expected = CreateOptions(directory);
            using var host = HostComposition.Build(expected);
            var options = host.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
            Assert.Equal(expected.DatabasePath, options.DatabasePath);
            Assert.Equal(expected.ArtifactRoot, options.ArtifactRoot);
            Assert.IsAssignableFrom<IAIContextStorage>(host.Services.GetRequiredService<IAIContextStorage>());
            Assert.IsAssignableFrom<IAIContextApplication>(host.Services.GetRequiredService<IAIContextApplication>());
            Assert.NotNull(host.Services.GetRequiredService<ILoggerFactory>());
            await host.StartAsync();
            await host.StopAsync();
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task InvalidDatabasePathIsRejectedOnStart()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var options = CreateOptions(directory);
            options.DatabasePath = " ";
            using var host = HostComposition.Build(options);
            await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static ServerOptions CreateOptions(string directory) => new()
    {
        DatabasePath = Path.Combine(directory, "server.db"),
        ArtifactRoot = Path.Combine(directory, "artifacts"),
        ApprovedRepositoryRoots = [directory],
        MinimumLogLevel = LogLevel.None
    };

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AIContextMCP.Server.Tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AIContextMCP.Server.Tests"));
        var fullPath = Path.GetFullPath(directory);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to delete an unowned test directory.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }
}
