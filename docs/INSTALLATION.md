# Installation

Install the .NET 10 SDK and Git on Windows, then clone the repository and publish the server:

```powershell
git clone https://github.com/AIAllTheThingz/AIContextMCP.git
Set-Location AIContextMCP
dotnet restore AIContextMCP.slnx
dotnet build AIContextMCP.slnx --configuration Release
dotnet test AIContextMCP.slnx --configuration Release
dotnet publish .\src\AIContextMCP.Server\AIContextMCP.Server.csproj --configuration Release --output .\publish
```

Copy [config.example.toml](config.example.toml), replace `<checkout>`, `<local-data>`, and `<approved-repository-root>`, and add the resulting stanza to the local MCP client configuration. The approved root must be a parent containing the repository. Keep the database, artifact root, and client configuration outside the checkout.

Start the published server through the client. For a new project, send one `project_bootstrap` request with `register=true` and a unique `requestId`; after it succeeds, use `register=false` with `includeWorkingTree=true` for normal read-only startup. Do not run from a raw `bin` DLL or copy only a DLL; the complete publish directory is required.

Historical local validation recorded restart recovery and 92 passing tests, but those results do not certify this candidate release. Re-run the commands above and the clean-source checks before publication.
