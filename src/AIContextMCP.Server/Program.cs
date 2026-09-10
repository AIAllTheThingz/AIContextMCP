using AIContextMCP.Server;
using Microsoft.Extensions.Hosting;

using var host = HostComposition.Build(args);
await host.RunAsync();
