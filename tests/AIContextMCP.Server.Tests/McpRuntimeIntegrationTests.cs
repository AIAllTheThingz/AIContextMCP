using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIContextMCP.Server;
using AIContextMCP.Storage.Sqlite;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AIContextMCP.Server.Tests;

public sealed class McpRuntimeIntegrationTests
{
    [Fact]
    public async Task Stdio_runtime_preserves_replay_and_reports_repository_freshness()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = timeout.Token;
        var first = await fixture.StartAsync(cancellationToken);
        try
        {
            var tools = await first.Client.ListToolsAsync(new ListToolsRequestParams(), cancellationToken);
            Assert.Equal(8, tools.Tools.Count);
            Assert.Equal(8, tools.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());

            foreach (var tool in tools.Tools)
            {
                Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, tool.Name, Arguments(("unexpected", true)), cancellationToken));
            }

            Assert.NotEmpty((await first.Client.ListToolsAsync(new ListToolsRequestParams(), cancellationToken)).Tools);
            Assert.Equal("NotFound", await ErrorCodeAsync(first.Client, "project.bootstrap", Arguments(
                ("repositoryPath", fixture.RepositoryPath)), cancellationToken));
            Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, "project.bootstrap", Arguments(
                ("repositoryPath", fixture.RepositoryPath),
                ("register", true)), cancellationToken));
            Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, "project.bootstrap", Arguments(
                ("requestId", ""),
                ("repositoryPath", fixture.RepositoryPath),
                ("register", true)), cancellationToken));
            Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, "project.bootstrap", Arguments(
                ("repositoryPath", fixture.RepositoryPath),
                ("register", null)), cancellationToken));
            Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, "context.search", Arguments(
                ("projectId", "not-a-uuid")), cancellationToken));
            Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, "context.search", Arguments(
                ("projectId", Guid.NewGuid().ToString("D")),
                ("maxResults", null)), cancellationToken));
            Assert.Equal("PathRejected", await ErrorCodeAsync(first.Client, "project.bootstrap", Arguments(
                ("requestId", RequestId()),
                ("repositoryPath", fixture.OutsideRepositoryPath),
                ("register", true)), cancellationToken));
            Assert.NotEmpty((await first.Client.ListToolsAsync(new ListToolsRequestParams(), cancellationToken)).Tools);

            var bootstrap = Success(await CallAsync(first.Client, "project.bootstrap", Arguments(
                ("requestId", RequestId()),
                ("repositoryPath", fixture.RepositoryPath),
                ("register", true)), cancellationToken));
            var projectId = bootstrap.GetProperty("project").GetProperty("id").GetString()!;
            var repositoryId = bootstrap.GetProperty("repository").GetProperty("id").GetString()!;
            var cleanSnapshot = bootstrap.GetProperty("git").Clone();
            var minimumBudgetSearch = Success(await CallAsync(first.Client, "context.search", Arguments(
                ("projectId", projectId), ("maxBytes", 8 * 1024)), cancellationToken));
            Assert.Empty(minimumBudgetSearch.GetProperty("entries").EnumerateArray());
            Assert.Empty(Success(await CallAsync(first.Client, "context.search", Arguments(("projectId", projectId)), cancellationToken)).GetProperty("entries").EnumerateArray());
            Assert.Empty(Success(await CallAsync(first.Client, "decision.list", Arguments(("projectId", projectId)), cancellationToken)).GetProperty("decisions").EnumerateArray());
            Assert.Empty(Success(await CallAsync(first.Client, "context.search", Arguments(
                ("projectId", projectId),
                ("repositoryId", null)), cancellationToken)).GetProperty("entries").EnumerateArray());

            var cleanRecord = Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("category", "context"),
                ("summary", "Clean repository observation."),
                ("source", Source()),
                ("observedSnapshot", cleanSnapshot));
            var cleanEntryId = Success(await CallAsync(first.Client, "context.record", cleanRecord, cancellationToken)).GetProperty("entryId").GetString()!;

            await File.WriteAllTextAsync(fixture.ReadmePath, "dirty\n", cancellationToken);
            var dirtyBootstrap = Success(await CallAsync(first.Client, "project.bootstrap", Arguments(("repositoryPath", fixture.RepositoryPath)), cancellationToken));
            var dirtySnapshot = dirtyBootstrap.GetProperty("git").Clone();
            Assert.Equal("Dirty", dirtySnapshot.GetProperty("completeness").GetString());
            var dirtyEntryId = Success(await CallAsync(first.Client, "context.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("category", "context"),
                ("summary", "Dirty repository observation."),
                ("source", Source()),
                ("observedSnapshot", dirtySnapshot)), cancellationToken)).GetProperty("entryId").GetString()!;
            await File.WriteAllTextAsync(fixture.ReadmePath, RuntimeFixture.InitialReadme, cancellationToken);

            var restored = Success(await CallAsync(first.Client, "context.search", Arguments(
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("maxResults", 100)), cancellationToken));
            Assert.Equal("Current", Entry(restored, cleanEntryId).GetProperty("freshness").GetString());
            Assert.Equal("Stale", Entry(restored, dirtyEntryId).GetProperty("freshness").GetString());

            await File.WriteAllTextAsync(fixture.ReadmePath, "changed head\n", cancellationToken);
            await fixture.GitAsync(cancellationToken, "add", "README.md");
            await fixture.GitAsync(cancellationToken, "commit", "-qm", "advance head");
            var changedHead = Success(await CallAsync(first.Client, "context.search", Arguments(
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("maxResults", 100)), cancellationToken));
            Assert.Equal("Stale", Entry(changedHead, cleanEntryId).GetProperty("freshness").GetString());

            var currentBootstrap = Success(await CallAsync(first.Client, "project.bootstrap", Arguments(("repositoryPath", fixture.RepositoryPath)), cancellationToken));
            var currentSnapshot = currentBootstrap.GetProperty("git").Clone();
            var currentSnapshotToken = currentSnapshot.GetProperty("snapshotToken").GetString()!;
            Assert.Equal("Clean", currentSnapshot.GetProperty("completeness").GetString());
            var branchEntryId = Success(await CallAsync(first.Client, "context.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("category", "context"),
                ("summary", "Current branch observation."),
                ("source", Source()),
                ("observedSnapshot", currentSnapshot)), cancellationToken)).GetProperty("entryId").GetString()!;
            var decisionId = Success(await CallAsync(first.Client, "decision.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("title", "Persisted runtime decision"),
                ("decision", "Keep MCP storage durable."),
                ("rationale", "Restart retrieval requires durable state."),
                ("source", Source()),
                ("observedSnapshot", currentSnapshot)), cancellationToken)).GetProperty("decisionId").GetString()!;
            var projectDecisionId = Success(await CallAsync(first.Client, "decision.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("title", "Project-level current-state decision"),
                ("decision", "Keep current-state checks scoped to the observed repository."),
                ("rationale", "Project records still need an authenticated repository observation for transitions."),
                ("source", Source()),
                ("observedSnapshot", currentSnapshot)), cancellationToken)).GetProperty("decisionId").GetString()!;
            Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, "decision.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("title", "Missing current token"),
                ("decision", "This transition must not bypass a current token."),
                ("rationale", "The predecessor is project scoped."),
                ("source", Source()),
                ("observedSnapshot", currentSnapshot),
                ("supersedesId", projectDecisionId),
                ("expectedVersion", 1),
                ("resolutionEvidence", "The predecessor remains auditable.")), cancellationToken));
            Assert.Equal("StaleState", await ErrorCodeAsync(first.Client, "decision.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("title", "Stale project token"),
                ("decision", "This transition must reject an old repository state."),
                ("rationale", "The predecessor is project scoped."),
                ("source", Source()),
                ("observedSnapshot", cleanSnapshot),
                ("supersedesId", projectDecisionId),
                ("expectedVersion", 1),
                ("resolutionEvidence", "The predecessor remains auditable."),
                ("expectedSnapshotToken", cleanSnapshot.GetProperty("snapshotToken").GetString())), cancellationToken));
            var projectDecisionReplacementId = Success(await CallAsync(first.Client, "decision.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("title", "Project-level current-state replacement"),
                ("decision", "Allow a project transition only with the current repository token."),
                ("rationale", "The repository is resolved from the signed observation."),
                ("source", Source()),
                ("observedSnapshot", currentSnapshot),
                ("supersedesId", projectDecisionId),
                ("expectedVersion", 1),
                ("resolutionEvidence", "The predecessor remains auditable."),
                ("expectedSnapshotToken", currentSnapshotToken)), cancellationToken)).GetProperty("decisionId").GetString()!;
            Assert.NotEqual(projectDecisionId, projectDecisionReplacementId);
            var testRunId = Success(await CallAsync(first.Client, "test.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("name", "Runtime process test"),
                ("status", "Passed"),
                ("passed", 1),
                ("failed", 0),
                ("skipped", 0),
                ("summary", "Runtime test passed."),
                ("commandData", "dotnet test runtime"),
                ("source", Source()),
                ("runSnapshot", currentSnapshot),
                ("observedUtc", DateTimeOffset.UtcNow)), cancellationToken)).GetProperty("testRunId").GetString()!;
            var longFindingDescription = new string('x', 4097);
            var findingId = Success(await CallAsync(first.Client, "finding.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("title", "Persisted runtime finding"),
                ("severity", "Info"),
                ("status", "Open"),
                ("description", longFindingDescription),
                ("source", Source()),
                ("observedSnapshot", currentSnapshot)), cancellationToken)).GetProperty("findingId").GetString()!;
            var findingDetail = Success(await CallAsync(first.Client, "context.search", Arguments(
                ("projectId", projectId), ("repositoryId", repositoryId), ("findingId", findingId)), cancellationToken));
            var detail = Assert.Single(findingDetail.GetProperty("findings").EnumerateArray());
            Assert.Equal(findingId, detail.GetProperty("findingId").GetString());
            Assert.Equal(longFindingDescription[..4096], detail.GetProperty("description").GetString());
            Assert.True(findingDetail.GetProperty("truncated").GetBoolean());
            Assert.Contains(findingDetail.GetProperty("warnings").EnumerateArray(), warning => warning.GetString() == "Finding detail fields were truncated for the response budget.");
            var minimumBudgetFindingDetail = Success(await CallAsync(first.Client, "context.search", Arguments(
                ("projectId", projectId), ("findingId", findingId), ("maxBytes", McpJson.MinimumResponseBytes)), cancellationToken));
            Assert.True(minimumBudgetFindingDetail.GetProperty("truncated").GetBoolean());
            Assert.Single(minimumBudgetFindingDetail.GetProperty("findings").EnumerateArray());
            var supersedingTest = Success(await CallAsync(first.Client, "test.record", Arguments(
                ("requestId", RequestId()), ("projectId", projectId), ("repositoryId", repositoryId),
                ("name", "Replacement runtime test"), ("status", "Passed"), ("passed", 1), ("failed", 0), ("skipped", 0),
                ("summary", "Replacement test passed."), ("commandData", "dotnet test runtime"), ("source", Source()),
                ("runSnapshot", currentSnapshot), ("observedUtc", DateTimeOffset.UtcNow), ("supersedesId", testRunId)), cancellationToken));
            var supersedingTestRunId = supersedingTest.GetProperty("testRunId").GetString()!;
            Assert.Equal(testRunId, supersedingTest.GetProperty("supersedesId").GetString());
            Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, "context.search", Arguments(
                ("projectId", projectId), ("findingId", findingId), ("query", "filtered")), cancellationToken));
            var revisedFinding = Success(await CallAsync(first.Client, "finding.record", Arguments(
                ("requestId", RequestId()), ("projectId", projectId), ("repositoryId", repositoryId),
                ("findingId", findingId), ("title", "Persisted runtime finding"), ("severity", "Info"), ("status", "Open"),
                ("description", "Revised persistence marker."), ("source", Source("revised-test")), ("observedSnapshot", currentSnapshot),
                ("expectedVersion", 1), ("expectedSnapshotToken", currentSnapshotToken)), cancellationToken));
            Assert.Equal(2, revisedFinding.GetProperty("version").GetInt32());
            var revisedDetail = Success(await CallAsync(first.Client, "context.search", Arguments(
                ("projectId", projectId), ("repositoryId", repositoryId), ("findingId", findingId)), cancellationToken));
            var revisedDetailItem = Assert.Single(revisedDetail.GetProperty("findings").EnumerateArray());
            Assert.Equal(2, revisedDetailItem.GetProperty("revision").GetInt32());
            Assert.Equal("revised-test", revisedDetailItem.GetProperty("provenance").GetString());
            Assert.False(revisedDetail.GetProperty("truncated").GetBoolean());
            var storage = new SqliteContextStorage(new SqliteStorageOptions { DatabasePath = fixture.DatabasePath, ArtifactRoot = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "artifacts") });
            {
                await storage.InitializeAsync(cancellationToken);
                var references = await storage.ListMcpReferencesAsync(AIContextMCP.Core.McpRecordKind.TestRun,
                    Guid.Parse(supersedingTestRunId), cancellationToken: cancellationToken);
                Assert.Contains(references, reference => reference.Role == AIContextMCP.Core.McpReferenceRole.Supersedes
                    && reference.Reference.Id == Guid.Parse(testRunId));
            }
            Assert.Equal("InvalidInput", await ErrorCodeAsync(first.Client, "handoff.create", HandoffArguments(
                projectId, null, currentSnapshot, null), cancellationToken));
            Assert.Equal("NotFound", await ErrorCodeAsync(first.Client, "handoff.create", HandoffArguments(
                projectId, repositoryId, currentSnapshot, currentSnapshotToken,
                tests: [RecordReference(projectId, repositoryId, decisionId)]), cancellationToken));
            Assert.Equal("CrossProjectReference", await ErrorCodeAsync(first.Client, "handoff.create", HandoffArguments(
                projectId, repositoryId, currentSnapshot, currentSnapshotToken,
                decisions: [RecordReference(projectId, Guid.NewGuid().ToString("D"), decisionId)]), cancellationToken));
            var forgedDocument = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
                ["repositoryId"] = repositoryId,
                ["kind"] = "repository-document",
                ["relativePath"] = "README.md",
                ["hash"] = new string('0', 64)
            };
            Assert.Equal("Conflict", await ErrorCodeAsync(first.Client, "handoff.create", HandoffArguments(
                projectId, repositoryId, currentSnapshot, currentSnapshotToken,
                relevantFiles: [forgedDocument]), cancellationToken));
            var handoffId = Success(await CallAsync(first.Client, "handoff.create", HandoffArguments(
                projectId, repositoryId, currentSnapshot, currentSnapshotToken,
                decisions: [RecordReference(projectId, repositoryId, decisionId)],
                tests: [RecordReference(projectId, repositoryId, testRunId)],
                findings: [RecordReference(projectId, repositoryId, findingId)]), cancellationToken)).GetProperty("handoffId").GetString()!;
            await AssertPersistedRecordsAsync(first.Client, projectId, repositoryId, fixture.RepositoryPath, decisionId, supersedingTestRunId, findingId, handoffId, cancellationToken);

            const string secret = "password=synthetic-mcp-secret";
            Assert.Equal("SecretRejected", await ErrorCodeAsync(first.Client, "context.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("category", "context"),
                ("summary", secret),
                ("source", Source()),
                ("observedSnapshot", cleanSnapshot)), cancellationToken));
            Assert.DoesNotContain(secret, first.StandardError, StringComparison.Ordinal);

            await first.DisposeAsync();
            Assert.Equal(0, first.ExitCode);

            var second = await fixture.StartAsync(cancellationToken);
            try
            {
                var replay = Success(await CallAsync(second.Client, "context.record", cleanRecord, cancellationToken));
                Assert.Equal(cleanEntryId, replay.GetProperty("entryId").GetString());
                var afterRestart = Success(await CallAsync(second.Client, "context.search", Arguments(
                    ("projectId", projectId),
                    ("repositoryId", repositoryId),
                    ("maxResults", 100)), cancellationToken));
                Assert.Equal(1, afterRestart.GetProperty("entries").EnumerateArray().Count(item => item.GetProperty("entryId").GetString() == cleanEntryId));
                await AssertPersistedRecordsAsync(second.Client, projectId, repositoryId, fixture.RepositoryPath, decisionId, supersedingTestRunId, findingId, handoffId, cancellationToken);

                await fixture.GitAsync(cancellationToken, "branch", "runtime-alt");
                await fixture.GitAsync(cancellationToken, "checkout", "-q", "runtime-alt");
                var branchChangedBootstrap = Success(await CallAsync(second.Client, "project.bootstrap", Arguments(("repositoryPath", fixture.RepositoryPath)), cancellationToken));
                var branchChangedSnapshot = branchChangedBootstrap.GetProperty("git").Clone();
                Assert.Equal(currentSnapshot.GetProperty("head").GetString(), branchChangedSnapshot.GetProperty("head").GetString());
                Assert.NotEqual(currentSnapshot.GetProperty("branch").GetString(), branchChangedSnapshot.GetProperty("branch").GetString());
                var branchChanged = Success(await CallAsync(second.Client, "context.search", Arguments(
                    ("projectId", projectId),
                    ("repositoryId", repositoryId),
                    ("maxResults", 100)), cancellationToken));
                Assert.Equal("Stale", Entry(branchChanged, branchEntryId).GetProperty("freshness").GetString());
                await AssertContextCursorPaginationAsync(second.Client, projectId, repositoryId, branchChangedSnapshot, cancellationToken);
            }
            finally
            {
                await second.DisposeAsync();
            }

            Assert.Equal(0, second.ExitCode);
            Assert.DoesNotContain(secret, second.StandardError, StringComparison.Ordinal);

            var raw = await fixture.StartRawAsync();
            try
            {
                await raw.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"runtime-test","version":"1"}}}""", cancellationToken);
                var initialized = await raw.ReadMessageAsync(cancellationToken);
                Assert.Equal(1, initialized.Body.GetProperty("id").GetInt64());
                Assert.True(initialized.Body.GetProperty("result").ValueKind == JsonValueKind.Object);
                await raw.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""", cancellationToken);

                await raw.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","method":"tools/list","params":{}}""", cancellationToken);
                AssertInvalidRawResponse(await raw.ReadMessageAsync(cancellationToken));
                await raw.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"context.search","arguments":{"projectId":"one","projectId":"two"}}}""", cancellationToken);
                AssertInvalidRawResponse(await raw.ReadMessageAsync(cancellationToken));
                await raw.WriteLineAsync("{" + "\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/list\",\"params\":{}" + "}" + new string(' ', McpJson.RequestBytes), cancellationToken);
                AssertInvalidRawResponse(await raw.ReadMessageAsync(cancellationToken));

                const string rawSecretId = "password=wire-secret";
                await raw.WriteLineAsync("{" + "\"jsonrpc\":\"2.0\",\"id\":\"" + rawSecretId + "\",\"method\":\"tools/list\",\"params\":{}" + "}", cancellationToken);
                AssertInvalidRawResponse(await raw.ReadMessageAsync(cancellationToken));
                await raw.WriteLineAsync("{" + "\"jsonrpc\":\"2.0\",\"id\":\"" + new string('x', 513) + "\",\"method\":\"tools/list\",\"params\":{}" + "}", cancellationToken);
                AssertInvalidRawResponse(await raw.ReadMessageAsync(cancellationToken));

                var escapedId = new string('<', 512);
                await raw.WriteLineAsync("{" + "\"jsonrpc\":\"2.0\",\"id\":\"" + escapedId + "\",\"method\":\"tools/list\",\"params\":{}" + "}", cancellationToken);
                var escapedResponse = await raw.ReadMessageAsync(cancellationToken);
                Assert.Equal(escapedId, escapedResponse.Body.GetProperty("id").GetString());
                Assert.True(Encoding.UTF8.GetByteCount(escapedResponse.Line) <= McpJson.ResponseBytes, $"The escaped-ID tools/list response was {Encoding.UTF8.GetByteCount(escapedResponse.Line)} bytes and exceeded the {McpJson.ResponseBytes}-byte protocol response limit.");

                await raw.WriteLineAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/list","params":{}}""", cancellationToken);
                var listed = await raw.ReadMessageAsync(cancellationToken);
                Assert.Equal(5, listed.Body.GetProperty("id").GetInt64());
                Assert.Equal(8, listed.Body.GetProperty("result").GetProperty("tools").GetArrayLength());
                Assert.True(Encoding.UTF8.GetByteCount(listed.Line) <= McpJson.ResponseBytes, "The tools/list response exceeded the protocol response limit.");

                var rawCall = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 6,
                    method = "tools/call",
                    @params = new { name = "context.search", arguments = new { projectId, repositoryId, maxResults = 20, maxBytes = McpJson.ResponseBytes } }
                }, McpJson.Options);
                await raw.WriteLineAsync(rawCall, cancellationToken);
                var called = await raw.ReadMessageAsync(cancellationToken);
                Assert.Equal(6, called.Body.GetProperty("id").GetInt64());
                var callResult = called.Body.GetProperty("result");
                var text = callResult.GetProperty("content")[0].GetProperty("text").GetString();
                Assert.Equal(callResult.GetProperty("structuredContent").GetRawText(), JsonDocument.Parse(text!).RootElement.GetRawText());
                Assert.True(Encoding.UTF8.GetByteCount(called.Line) <= McpJson.ResponseBytes, "The tools/call response exceeded the protocol response limit.");
                Assert.DoesNotContain(rawSecretId, raw.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(rawSecretId, raw.StandardError, StringComparison.Ordinal);
            }
            finally
            {
                await raw.DisposeAsync();
            }

            Assert.Equal(0, raw.ExitCode);
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    private static async Task<CallToolResult> CallAsync(McpClient client, string name, IDictionary<string, JsonElement> arguments, CancellationToken cancellationToken) =>
        await client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = arguments }, cancellationToken);

    private static async Task<string> ErrorCodeAsync(McpClient client, string name, IDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        var result = await CallAsync(client, name, arguments, cancellationToken);
        Assert.True(result.IsError == true);
        var root = Structured(result);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(root.GetRawText(), JsonDocument.Parse(text.Text).RootElement.GetRawText());
        Assert.False(root.GetProperty("ok").GetBoolean());
        return root.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static void AssertInvalidRawResponse(RawMessage response)
    {
        Assert.Equal(JsonValueKind.Null, response.Body.GetProperty("id").ValueKind);
        Assert.Equal((int)McpErrorCode.InvalidRequest, response.Body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("Invalid JSON-RPC request.", response.Body.GetProperty("error").GetProperty("message").GetString());
    }

    private static async Task AssertPersistedRecordsAsync(
        McpClient client,
        string projectId,
        string repositoryId,
        string repositoryPath,
        string decisionId,
        string testRunId,
        string findingId,
        string handoffId,
        CancellationToken cancellationToken)
    {
        var decisions = Success(await CallAsync(client, "decision.list", Arguments(
            ("projectId", projectId),
            ("repositoryId", repositoryId),
            ("maxResults", 50)), cancellationToken));
        var decision = SingleItem(decisions.GetProperty("decisions"), "decisionId", decisionId);
        Assert.Equal("Persisted runtime decision", decision.GetProperty("title").GetString());
        Assert.Equal("Keep MCP storage durable.", decision.GetProperty("decision").GetString());
        Assert.Equal("Accepted", decision.GetProperty("status").GetString());
        Assert.Equal("test", decision.GetProperty("source").GetProperty("kind").GetString());

        var bootstrap = Success(await CallAsync(client, "project.bootstrap", Arguments(("repositoryPath", repositoryPath)), cancellationToken));
        var validation = bootstrap.GetProperty("validation");
        Assert.Equal(testRunId, validation.GetProperty("id").GetString());
        Assert.Equal("Passed", validation.GetProperty("status").GetString());
        Assert.Equal(1, validation.GetProperty("passed").GetInt64());
        var finding = SingleItem(bootstrap.GetProperty("findings"), "id", findingId);
        Assert.Equal("Persisted runtime finding", finding.GetProperty("title").GetString());
        Assert.Equal("Revised persistence marker.", finding.GetProperty("summary").GetString());
        var handoff = bootstrap.GetProperty("handoff");
        Assert.Equal(handoffId, handoff.GetProperty("handoffId").GetString());
        Assert.Equal("Persist the runtime handoff.", handoff.GetProperty("objective").GetString());
        Assert.Equal("Read persisted runtime state.", handoff.GetProperty("nextAction").GetString());
    }

    private static async Task AssertContextCursorPaginationAsync(
        McpClient client,
        string projectId,
        string repositoryId,
        JsonElement snapshot,
        CancellationToken cancellationToken)
    {
        const string category = "cursor-page";
        var recordedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index <= 100; index++)
        {
            var recorded = Success(await CallAsync(client, "context.record", Arguments(
                ("requestId", RequestId()),
                ("projectId", projectId),
                ("repositoryId", repositoryId),
                ("category", category),
                ("summary", $"p{index:D3}"),
                ("source", Source()),
                ("observedSnapshot", snapshot)), cancellationToken));
            recordedIds.Add(recorded.GetProperty("entryId").GetString()!);
        }

        Assert.Equal(101, recordedIds.Count);
        Assert.Equal("LimitExceeded", await ErrorCodeAsync(client, "context.search", Arguments(
            ("projectId", projectId),
            ("repositoryId", repositoryId),
            ("category", category),
            ("maxResults", 100),
            ("maxBytes", McpJson.ResponseBytes)), cancellationToken));
        const int pageSize = 20;
        var first = Success(await CallAsync(client, "context.search", Arguments(
            ("projectId", projectId),
            ("repositoryId", repositoryId),
            ("category", category),
            ("maxResults", pageSize),
            ("maxBytes", McpJson.ResponseBytes)), cancellationToken));
        var firstEntries = first.GetProperty("entries");
        Assert.Equal(pageSize, firstEntries.GetArrayLength());
        Assert.True(first.GetProperty("truncated").GetBoolean());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        var firstIds = firstEntries.EnumerateArray().Select(entry => entry.GetProperty("entryId").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(pageSize, firstIds.Count);
        Assert.Equal("InvalidInput", await ErrorCodeAsync(client, "context.search", Arguments(
            ("projectId", projectId),
            ("repositoryId", repositoryId),
            ("category", "cursor-filter-tamper"),
            ("maxResults", pageSize),
            ("maxBytes", McpJson.ResponseBytes),
            ("cursor", cursor)), cancellationToken));

        var second = Success(await CallAsync(client, "context.search", Arguments(
            ("projectId", projectId),
            ("repositoryId", repositoryId),
            ("category", category),
            ("maxResults", pageSize),
            ("maxBytes", McpJson.ResponseBytes),
            ("cursor", cursor)), cancellationToken));
        var secondEntries = second.GetProperty("entries");
        Assert.Equal(pageSize, secondEntries.GetArrayLength());
        foreach (var entry in secondEntries.EnumerateArray())
        {
            var entryId = entry.GetProperty("entryId").GetString()!;
            Assert.Contains(entryId, recordedIds);
            Assert.DoesNotContain(entryId, firstIds);
        }
        Assert.True(second.GetProperty("truncated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(second.GetProperty("nextCursor").GetString()));
    }

    private static JsonElement Success(CallToolResult result)
    {
        Assert.False(result.IsError == true, result.StructuredContent?.GetRawText());
        var root = Structured(result);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(root.GetRawText(), JsonDocument.Parse(text.Text).RootElement.GetRawText());
        Assert.True(root.GetProperty("ok").GetBoolean());
        return root.GetProperty("data").Clone();
    }

    private static JsonElement Structured(CallToolResult result)
    {
        Assert.True(result.StructuredContent.HasValue);
        return result.StructuredContent!.Value;
    }

    private static JsonElement Entry(JsonElement result, string id) => SingleItem(result.GetProperty("entries"), "entryId", id);

    private static JsonElement SingleItem(JsonElement items, string idProperty, string id)
    {
        var matches = items.EnumerateArray().Where(item => item.GetProperty(idProperty).GetString() == id).ToArray();
        Assert.Single(matches);
        return matches[0];
    }

    private static Dictionary<string, JsonElement> Arguments(params (string Name, object? Value)[] values) => values.ToDictionary(
        value => value.Name,
        value => value.Value is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(value.Value, McpJson.Options),
        StringComparer.Ordinal);

    private static Dictionary<string, object> Source(string kind = "test") => new(StringComparer.Ordinal)
    {
        ["kind"] = kind,
        ["observedUtc"] = DateTimeOffset.UtcNow
    };

    private static Dictionary<string, string> RecordReference(string projectId, string repositoryId, string id) => new(StringComparer.Ordinal)
    {
        ["projectId"] = projectId,
        ["repositoryId"] = repositoryId,
        ["kind"] = "record",
        ["id"] = id
    };

    private static Dictionary<string, JsonElement> HandoffArguments(
        string projectId,
        string? repositoryId,
        JsonElement observedSnapshot,
        string? expectedSnapshotToken,
        IReadOnlyList<object>? decisions = null,
        IReadOnlyList<object>? tests = null,
        IReadOnlyList<object>? findings = null,
        IReadOnlyList<object>? relevantFiles = null) => Arguments(
            ("requestId", RequestId()),
            ("projectId", projectId),
            ("repositoryId", repositoryId),
            ("objective", "Persist the runtime handoff."),
            ("completedWork", "Recorded runtime state."),
            ("activeWork", "Verify restart retrieval."),
            ("blockers", Array.Empty<object>()),
            ("decisions", decisions ?? Array.Empty<object>()),
            ("tests", tests ?? Array.Empty<object>()),
            ("findings", findings ?? Array.Empty<object>()),
            ("relevantFiles", relevantFiles ?? Array.Empty<object>()),
            ("artifacts", Array.Empty<object>()),
            ("nextAction", "Read persisted runtime state."),
            ("observedSnapshot", observedSnapshot),
            ("expectedSnapshotToken", expectedSnapshotToken));

    private static string RequestId() => $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():D}";

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        public const string InitialReadme = "initial\n";
        private readonly string _root;

        private RuntimeFixture(string root)
        {
            _root = root;
            Directory = Path.Combine(root, "workspace");
            RepositoryPath = Path.Combine(Directory, "repository");
            OutsideRepositoryPath = Path.Combine(root, "outside-repository");
            ReadmePath = Path.Combine(RepositoryPath, "README.md");
            DatabasePath = Path.Combine(Directory, "runtime.db");
            ArtifactRoot = Path.Combine(Directory, "artifacts");
        }

        public string Directory { get; }
        public string RepositoryPath { get; }
        public string OutsideRepositoryPath { get; }
        public string ReadmePath { get; }
        public string DatabasePath { get; }
        public string ArtifactRoot { get; }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AIContextMCP.Server.Runtime.Tests", Guid.NewGuid().ToString("N")));
            System.IO.Directory.CreateDirectory(root);
            var fixture = new RuntimeFixture(root);
            System.IO.Directory.CreateDirectory(fixture.RepositoryPath);
            System.IO.Directory.CreateDirectory(fixture.OutsideRepositoryPath);
            System.IO.Directory.CreateDirectory(fixture.ArtifactRoot);
            await fixture.GitAsync(CancellationToken.None, "init", "-q");
            await fixture.GitAsync(CancellationToken.None, "config", "user.email", "runtime@example.invalid");
            await fixture.GitAsync(CancellationToken.None, "config", "user.name", "Runtime Test");
            await File.WriteAllTextAsync(fixture.ReadmePath, InitialReadme);
            await fixture.GitAsync(CancellationToken.None, "add", "README.md");
            await fixture.GitAsync(CancellationToken.None, "commit", "-qm", "initial");
            await fixture.GitAtAsync(fixture.OutsideRepositoryPath, CancellationToken.None, "init", "-q");
            return fixture;
        }

        public async Task<RuntimeProcess> StartAsync(CancellationToken cancellationToken)
        {
            var (process, errors) = StartServerProcess();
            var loggerFactory = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.None));
            try
            {
                var transport = new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, loggerFactory);
                var client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ProtocolVersion = "2025-11-25",
                    InitializationTimeout = TimeSpan.FromSeconds(20)
                }, loggerFactory, cancellationToken);
                return new RuntimeProcess(client, loggerFactory, errors, process);
            }
            catch
            {
                loggerFactory.Dispose();
                StopProcess(process);
                throw;
            }
        }

        public Task<RawProcess> StartRawAsync()
        {
            var (process, errors) = StartServerProcess();
            return Task.FromResult(new RawProcess(process, errors));
        }

        private (Process Process, ConcurrentQueue<string> Errors) StartServerProcess()
        {
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["AIContextMCP_DatabasePath"] = DatabasePath;
            environment["AIContextMCP_ArtifactRoot"] = ArtifactRoot;
            environment["AIContextMCP_ApprovedRepositoryRoots__0"] = Directory;
            environment["AIContextMCP_MinimumLogLevel"] = "Information";
            var errors = new ConcurrentQueue<string>();
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Directory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(typeof(HostComposition).Assembly.Location);
            startInfo.Environment.Clear();
            foreach (var value in environment) startInfo.Environment[value.Key] = value.Value;
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null) errors.Enqueue(eventArgs.Data);
            };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Unable to start the MCP server process.");
                process.BeginErrorReadLine();
                return (process, errors);
            }
            catch
            {
                StopProcess(process);
                throw;
            }
        }

        private static void StopProcess(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        public async Task GitAsync(CancellationToken cancellationToken, params string[] arguments)
            => await GitAtAsync(RepositoryPath, cancellationToken, arguments);

        public async Task GitAtAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardError = process!.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            Assert.True(process.ExitCode == 0, await standardError);
        }

        public ValueTask DisposeAsync()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AIContextMCP.Server.Runtime.Tests"));
            if (!_root.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to delete an unowned runtime test directory.");
            if (System.IO.Directory.Exists(_root))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                }

                System.IO.Directory.Delete(_root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RuntimeProcess(McpClient client, ILoggerFactory loggerFactory, ConcurrentQueue<string> errors, Process process) : IAsyncDisposable
    {
        private readonly Task<ClientCompletionDetails> _completion = client.Completion;
        private int _disposed;

        public McpClient Client { get; } = client;
        public int? ExitCode { get; private set; }
        public string StandardError => string.Join("\n", errors);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                ExitCode = process.ExitCode;
                await _completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await Client.DisposeAsync();
                process.Dispose();
                loggerFactory.Dispose();
            }
        }
    }

    private sealed class RawProcess(Process process, ConcurrentQueue<string> errors) : IAsyncDisposable
    {
        private readonly StreamWriter _input = new(process.StandardInput.BaseStream, new UTF8Encoding(false), 4096, leaveOpen: false) { NewLine = "\n" };
        private readonly StreamReader _outputReader = new(process.StandardOutput.BaseStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
        private readonly ConcurrentQueue<string> _output = new();
        private int _disposed;

        public int? ExitCode { get; private set; }
        public string Output => string.Join("\n", _output);
        public string StandardError => string.Join("\n", errors);

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _input.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }

        public async Task<RawMessage> ReadMessageAsync(CancellationToken cancellationToken)
        {
            var line = await _outputReader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("The MCP server closed stdout before responding.");
            _output.Enqueue(line);
            using var document = JsonDocument.Parse(line);
            return new RawMessage(line, document.RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                _input.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                ExitCode = process.ExitCode;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                _outputReader.Dispose();
                _input.Dispose();
                process.Dispose();
            }
        }
    }

    private sealed record RawMessage(string Line, JsonElement Body);
}
