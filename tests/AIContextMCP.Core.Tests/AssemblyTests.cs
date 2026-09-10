using System.Reflection;
using Xunit;

namespace AIContextMCP.Core.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void CoreAssemblyLoads() => Assert.NotNull(Assembly.Load("AIContextMCP.Core"));
}
