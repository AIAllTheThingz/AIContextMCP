using AIContextMCP.Core;
using AIContextMCP.Storage.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIContextMCP.Server;

public static class HostComposition
{
    public static IHost Build(string[]? args = null)
        => BuildCore(args, null);

    public static IHost Build(ServerOptions options, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return BuildCore(args, options);
    }

    private static IHost BuildCore(string[]? args, ServerOptions? suppliedOptions)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "AIContextMCP.Server",
            Args = args,
            DisableDefaults = true
        });

        builder.Configuration.AddEnvironmentVariables("AIContextMCP_");
        builder.Services.AddOptions<ServerOptions>()
            .Bind(builder.Configuration)
            .Validate(options => !string.IsNullOrWhiteSpace(options.DatabasePath), "DatabasePath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ArtifactRoot), "ArtifactRoot is required.")
            .Validate(options => Enum.IsDefined(options.MinimumLogLevel), "MinimumLogLevel is invalid.")
            .ValidateOnStart();
        var settings = new ServerOptions();
        builder.Configuration.Bind(settings);
        if (suppliedOptions is not null) CopyOptions(settings, suppliedOptions);
        if (suppliedOptions is not null) builder.Services.PostConfigure<ServerOptions>(options => CopyOptions(options, suppliedOptions));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(settings.MinimumLogLevel);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
        builder.Logging.AddJsonConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.UseUtcTimestamp = true;
        });
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton<IAIContextStorage>(services =>
        {
            var options = services.GetRequiredService<IOptions<ServerOptions>>().Value;
            return new SqliteContextStorage(new SqliteStorageOptions
            {
                DatabasePath = options.DatabasePath,
                ArtifactRoot = options.ArtifactRoot
            });
        });
        builder.Services.AddSingleton<IAIContextApplication>(services =>
        {
            var options = services.GetRequiredService<IOptions<ServerOptions>>().Value;
            var logger = services.GetRequiredService<ILogger<AIContextApplication>>();
            return new AIContextApplication(
                services.GetRequiredService<IAIContextStorage>(),
                new ApplicationCoreOptions { ApprovedRepositoryRoots = options.ApprovedRepositoryRoots },
                (operation, outcome) => logger.LogInformation("application.operation {Operation} {Outcome}", operation, outcome));
        });
        builder.Services.AddSingleton<SnapshotTokens>();
        builder.Services.AddSingleton<McpToolAdapter>();
        builder.Services.AddSingleton<IHostedService, StorageInitializationService>();
        var input = new BoundedJsonLineStream(Console.OpenStandardInput(), McpJson.RequestBytes);
        var server = builder.Services.AddMcpServer()
            .WithStreamServerTransport(input, Console.OpenStandardOutput())
            .WithListToolsHandler(McpProtocolHandlers.ListToolsAsync)
            .WithCallToolHandler(McpProtocolHandlers.CallToolAsync);
        var transport = server.Services.Single(descriptor => descriptor.ServiceType == typeof(ITransport)).ImplementationInstance as StreamServerTransport
            ?? throw new InvalidOperationException("MCP stream transport was not registered.");
        input.Bind((id, token) => transport.SendMessageAsync(new JsonRpcError
        {
            Id = id ?? default,
            Error = new JsonRpcErrorDetail { Code = (int)McpErrorCode.InvalidRequest, Message = "Invalid JSON-RPC request." }
        }, token));
        return builder.Build();
    }

    private static void CopyOptions(ServerOptions target, ServerOptions source)
    {
        target.DatabasePath = source.DatabasePath;
        target.ArtifactRoot = source.ArtifactRoot;
        target.ApprovedRepositoryRoots = source.ApprovedRepositoryRoots.ToArray();
        target.MinimumLogLevel = source.MinimumLogLevel;
    }

    private sealed class StorageInitializationService(IAIContextStorage storage, ILogger<StorageInitializationService> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await storage.InitializeAsync(cancellationToken);
            }
            catch (StorageException exception)
            {
                logger.LogCritical("mcp.startup {Outcome} {Code}", "failure", exception.Code);
                throw new InvalidOperationException("Storage initialization failed.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class ServerOptions
{
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "AIContextMCP.db");
    public string ArtifactRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "artifacts");
    public string[] ApprovedRepositoryRoots { get; set; } = ["D:\\Projects"];
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;
}
