using Microsoft.Data.Sqlite;

namespace AIContextMCP.Storage.Sqlite;

internal static class SqliteSchema
{
    public const int CurrentVersion = 5;

    public static readonly IReadOnlySet<string> RequiredTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "SchemaVersion", "Projects", "Repositories", "ContextEntries", "Decisions", "Phases", "TestRuns", "Findings", "Artifacts", "Handoffs", "MutationReceipts", "McpRecordObservations", "McpRecordReferences"
    };

    public static readonly IReadOnlySet<string> RequiredIndexes = new HashSet<string>(StringComparer.Ordinal)
    {
        "IX_Repositories_ProjectId", "IX_Repositories_Remote", "IX_ContextEntries_Project_Category_Status_CreatedUtc", "IX_ContextEntries_Repository_Branch_CommitSha",
        "IX_Decisions_Project_Status", "IX_Decisions_SupersedesDecisionId", "IX_Decisions_SupersededByDecisionId", "IX_Decisions_Repository_Branch_CommitSha", "IX_Phases_Project_Status", "IX_Phases_OneActivePerProject",
        "IX_Phases_Repository_Branch_CommitSha", "IX_TestRuns_Project_Status_CreatedUtc", "IX_TestRuns_Repository_Branch_CommitSha", "IX_Findings_Project_Status_Severity",
        "IX_Findings_Id_Revision", "IX_Findings_Repository_Branch_CommitSha", "IX_Artifacts_Project_ContentHash", "IX_Artifacts_Repository", "IX_Handoffs_Project_CreatedUtc", "IX_Handoffs_Repository",
        "IX_MutationReceipts_ExpiresUtc", "IX_McpRecordObservations_Project_Kind_ObservedUtc_Id", "IX_McpRecordReferences_ReferenceScope"
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RequiredColumns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["SchemaVersion"] = Set("Version", "MigrationId", "AppliedUtc"),
        ["Projects"] = Set("Id", "StableProjectId", "Name", "Status", "CreatedUtc", "UpdatedUtc"),
        ["Repositories"] = Set("Id", "ProjectId", "CanonicalPath", "Remote", "Organization", "RepositoryName", "DefaultBranch", "LastKnownBranch", "LastKnownCommitSha", "Status", "CreatedUtc", "UpdatedUtc"),
        ["ContextEntries"] = Set("Id", "ProjectId", "RepositoryId", "Category", "Title", "Summary", "Content", "AuthoritativeReference", "Branch", "CommitSha", "Status", "ContentHash", "CreatedUtc", "UpdatedUtc", "SupersededUtc", "Tier", "Objective", "PhaseId"),
        ["Decisions"] = Set("Id", "ProjectId", "RepositoryId", "Category", "Component", "Title", "DecisionText", "Rationale", "AuthoritativeReference", "ResolutionEvidence", "OriginatingCommitSha", "Branch", "Status", "SupersedesDecisionId", "SupersededByDecisionId", "CreatedUtc", "UpdatedUtc", "SupersededUtc", "Version"),
        ["Phases"] = Set("Id", "ProjectId", "RepositoryId", "PhaseKey", "Objective", "Status", "Branch", "CommitSha", "StartedUtc", "CompletedUtc", "CreatedUtc", "UpdatedUtc", "Version", "ContextEntryId"),
        ["TestRuns"] = Set("Id", "ProjectId", "RepositoryId", "CommandText", "Branch", "CommitSha", "Passed", "Failed", "Skipped", "Total", "Status", "ArtifactId", "ArtifactReference", "StartedUtc", "CompletedUtc", "CreatedUtc", "Name", "Summary", "Evidence", "ObservedUtc"),
        ["Findings"] = Set("Id", "Revision", "ProjectId", "RepositoryId", "Category", "Title", "Description", "Severity", "Status", "Resolution", "ResolutionEvidence", "Branch", "CommitSha", "AuthoritativeReference", "CreatedUtc", "UpdatedUtc", "Component", "Location", "Remediation"),
        ["Artifacts"] = Set("Id", "ProjectId", "RepositoryId", "ArtifactType", "Title", "Reference", "ContentHash", "SizeBytes", "Branch", "CommitSha", "CreatedUtc", "UpdatedUtc"),
        ["Handoffs"] = Set("Id", "ProjectId", "RepositoryId", "Branch", "CommitSha", "Objective", "CompletedWork", "ActiveWork", "ActiveBlockers", "ImportantDecisions", "LatestTestStatus", "UnresolvedFindings", "RelevantFilesJson", "RelevantArtifactsJson", "RecommendedNextAction", "CreatedUtc", "UpdatedUtc", "PhaseId"),
        ["MutationReceipts"] = Set("Scope", "Tool", "RequestId", "CanonicalPayloadHash", "ReceiptJson", "IssuedUtc", "ExpiresUtc", "CompletedUtc"),
        ["McpRecordObservations"] = Set("RecordKind", "RecordId", "Revision", "ProjectId", "RepositoryId", "SourceKind", "SourceObservedUtc", "SourceReferenceKind", "SourceReferenceRepositoryId", "SourceReferenceId", "SourceReferenceRelativePath", "SourceReferenceUri", "SourceReferenceHash", "CanonicalRoot", "Branch", "Head", "WorkingTreeFingerprint", "SnapshotObservedUtc", "Completeness", "RecordObservedUtc"),
        ["McpRecordReferences"] = Set("RecordKind", "RecordId", "Revision", "Role", "Ordinal", "ReferencedProjectId", "ReferencedRepositoryId", "Kind", "ReferenceId", "RelativePath", "Uri", "Hash")
    };

    public static readonly IReadOnlyDictionary<string, int> MinimumForeignKeys = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Repositories"] = 1, ["ContextEntries"] = 2, ["Decisions"] = 4, ["Phases"] = 2, ["TestRuns"] = 3, ["Findings"] = 2, ["Artifacts"] = 2, ["Handoffs"] = 2, ["McpRecordObservations"] = 2, ["McpRecordReferences"] = 3
    };

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    public static IReadOnlySet<string> GetRequiredTables(int version) => version switch
    {
        1 or 2 => new HashSet<string>(RequiredTables.Except(["MutationReceipts", "McpRecordObservations", "McpRecordReferences"]), StringComparer.Ordinal),
        3 => new HashSet<string>(RequiredTables.Except(["McpRecordObservations", "McpRecordReferences"]), StringComparer.Ordinal),
        4 or 5 => RequiredTables,
        _ => new HashSet<string>(StringComparer.Ordinal)
    };

    public static IReadOnlySet<string> GetRequiredIndexes(int version) => version switch
    {
        1 => new HashSet<string>(RequiredIndexes.Where(index => index is not "IX_Decisions_Repository_Branch_CommitSha" and not "IX_Findings_Repository_Branch_CommitSha" and not "IX_MutationReceipts_ExpiresUtc" and not "IX_McpRecordObservations_Project_Kind_ObservedUtc_Id" and not "IX_McpRecordReferences_ReferenceScope"), StringComparer.Ordinal),
        2 => new HashSet<string>(RequiredIndexes.Except(["IX_MutationReceipts_ExpiresUtc", "IX_McpRecordObservations_Project_Kind_ObservedUtc_Id", "IX_McpRecordReferences_ReferenceScope"]), StringComparer.Ordinal),
        3 => new HashSet<string>(RequiredIndexes.Except(["IX_McpRecordObservations_Project_Kind_ObservedUtc_Id", "IX_McpRecordReferences_ReferenceScope"]), StringComparer.Ordinal),
        4 or 5 => RequiredIndexes,
        _ => new HashSet<string>(StringComparer.Ordinal)
    };

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> GetRequiredColumns(int version)
    {
        if (version is 4 or 5)
        {
            return RequiredColumns;
        }

        return RequiredColumns
            .Where(pair => pair.Key is not "MutationReceipts" and not "McpRecordObservations" and not "McpRecordReferences")
            .ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)new HashSet<string>(pair.Value.Except(pair.Key switch
            {
                "Decisions" when version == 1 => ["Branch"],
                "Findings" when version == 1 => ["Branch", "CommitSha", "AuthoritativeReference"],
                "ContextEntries" => ["Tier", "Objective", "PhaseId"],
                "Decisions" => ["Version"],
                "Phases" => ["Version", "ContextEntryId"],
                "TestRuns" => ["Name", "Summary", "Evidence", "ObservedUtc"],
                "Findings" => ["Component", "Location", "Remediation"],
                "Handoffs" => ["PhaseId"],
                _ => []
            }), StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, int> GetMinimumForeignKeys(int version) => version is 4 or 5
        ? MinimumForeignKeys
        : MinimumForeignKeys
            .Where(pair => pair.Key is not "McpRecordObservations" and not "McpRecordReferences")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static async Task ApplyVersionOneAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE SchemaVersion (
                Version INTEGER PRIMARY KEY CHECK (Version > 0),
                MigrationId TEXT NOT NULL UNIQUE,
                AppliedUtc TEXT NOT NULL
            );

            CREATE TABLE Projects (
                Id TEXT PRIMARY KEY,
                StableProjectId TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                Status INTEGER NOT NULL CHECK (Status IN (0, 1)),
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );

            CREATE TABLE Repositories (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                CanonicalPath TEXT NOT NULL UNIQUE,
                Remote TEXT NULL,
                Organization TEXT NULL,
                RepositoryName TEXT NOT NULL,
                DefaultBranch TEXT NOT NULL,
                LastKnownBranch TEXT NULL,
                LastKnownCommitSha TEXT NULL,
                Status INTEGER NOT NULL CHECK (Status IN (0, 1)),
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (Id, ProjectId),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT
            );

            CREATE TABLE ContextEntries (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                Category TEXT NOT NULL,
                Title TEXT NOT NULL,
                Summary TEXT NOT NULL,
                Content TEXT NULL,
                AuthoritativeReference TEXT NULL,
                Branch TEXT NULL,
                CommitSha TEXT NULL,
                Status INTEGER NOT NULL CHECK (Status IN (0, 1, 2)),
                ContentHash TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                SupersededUtc TEXT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (RepositoryId, ProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE TABLE Decisions (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                Category TEXT NOT NULL,
                Component TEXT NOT NULL,
                Title TEXT NOT NULL,
                DecisionText TEXT NOT NULL,
                Rationale TEXT NOT NULL,
                AuthoritativeReference TEXT NULL,
                ResolutionEvidence TEXT NULL,
                OriginatingCommitSha TEXT NULL,
                Status INTEGER NOT NULL CHECK (Status IN (0, 1, 2, 3, 4)),
                SupersedesDecisionId TEXT NULL,
                SupersededByDecisionId TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                SupersededUtc TEXT NULL,
                UNIQUE (Id, ProjectId),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (RepositoryId, ProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT,
                FOREIGN KEY (SupersedesDecisionId, ProjectId) REFERENCES Decisions(Id, ProjectId) ON DELETE RESTRICT,
                FOREIGN KEY (SupersededByDecisionId, ProjectId) REFERENCES Decisions(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE TABLE Phases (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                PhaseKey TEXT NOT NULL,
                Objective TEXT NOT NULL,
                Status INTEGER NOT NULL CHECK (Status IN (0, 1, 2, 3)),
                Branch TEXT NULL,
                CommitSha TEXT NULL,
                StartedUtc TEXT NULL,
                CompletedUtc TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (Id, ProjectId),
                UNIQUE (ProjectId, PhaseKey),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (RepositoryId, ProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE TABLE Artifacts (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                ArtifactType TEXT NOT NULL,
                Title TEXT NOT NULL,
                Reference TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL CHECK (SizeBytes >= 0),
                Branch TEXT NULL,
                CommitSha TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (Id, ProjectId),
                UNIQUE (ProjectId, Reference, ContentHash),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (RepositoryId, ProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE TABLE TestRuns (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                CommandText TEXT NOT NULL,
                Branch TEXT NULL,
                CommitSha TEXT NULL,
                Passed INTEGER NOT NULL CHECK (Passed >= 0),
                Failed INTEGER NOT NULL CHECK (Failed >= 0),
                Skipped INTEGER NOT NULL CHECK (Skipped >= 0),
                Total INTEGER NOT NULL CHECK (Total >= 0),
                Status INTEGER NOT NULL CHECK (Status IN (0, 1, 2, 3, 4)),
                ArtifactId TEXT NULL,
                ArtifactReference TEXT NULL,
                StartedUtc TEXT NULL,
                CompletedUtc TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (RepositoryId, ProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT,
                FOREIGN KEY (ArtifactId, ProjectId) REFERENCES Artifacts(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE TABLE Findings (
                Id TEXT NOT NULL,
                Revision INTEGER NOT NULL CHECK (Revision > 0),
                ProjectId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                Category INTEGER NOT NULL CHECK (Category IN (0, 1, 2, 3, 4)),
                Title TEXT NOT NULL,
                Description TEXT NOT NULL,
                Severity INTEGER NOT NULL CHECK (Severity IN (0, 1, 2, 3, 4)),
                Status INTEGER NOT NULL CHECK (Status IN (0, 1, 2, 3)),
                Resolution TEXT NULL,
                ResolutionEvidence TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY (Id, Revision),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (RepositoryId, ProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE TABLE Handoffs (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                Branch TEXT NULL,
                CommitSha TEXT NULL,
                Objective TEXT NOT NULL,
                CompletedWork TEXT NOT NULL,
                ActiveWork TEXT NOT NULL,
                ActiveBlockers TEXT NOT NULL,
                ImportantDecisions TEXT NOT NULL,
                LatestTestStatus TEXT NOT NULL,
                UnresolvedFindings TEXT NOT NULL,
                RelevantFilesJson TEXT NOT NULL,
                RelevantArtifactsJson TEXT NOT NULL,
                RecommendedNextAction TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (RepositoryId, ProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE INDEX IX_Repositories_ProjectId ON Repositories(ProjectId);
            CREATE INDEX IX_Repositories_Remote ON Repositories(Remote);
            CREATE INDEX IX_ContextEntries_Project_Category_Status_CreatedUtc ON ContextEntries(ProjectId, Category, Status, CreatedUtc DESC);
            CREATE INDEX IX_ContextEntries_Repository_Branch_CommitSha ON ContextEntries(RepositoryId, Branch, CommitSha);
            CREATE INDEX IX_Decisions_Project_Status ON Decisions(ProjectId, Status);
            CREATE INDEX IX_Decisions_SupersedesDecisionId ON Decisions(SupersedesDecisionId);
            CREATE INDEX IX_Decisions_SupersededByDecisionId ON Decisions(SupersededByDecisionId);
            CREATE INDEX IX_Phases_Project_Status ON Phases(ProjectId, Status);
            CREATE UNIQUE INDEX IX_Phases_OneActivePerProject ON Phases(ProjectId) WHERE Status = 1;
            CREATE INDEX IX_Phases_Repository_Branch_CommitSha ON Phases(RepositoryId, Branch, CommitSha);
            CREATE INDEX IX_TestRuns_Project_Status_CreatedUtc ON TestRuns(ProjectId, Status, CreatedUtc DESC);
            CREATE INDEX IX_TestRuns_Repository_Branch_CommitSha ON TestRuns(RepositoryId, Branch, CommitSha);
            CREATE INDEX IX_Findings_Project_Status_Severity ON Findings(ProjectId, Status, Severity);
            CREATE INDEX IX_Findings_Id_Revision ON Findings(Id, Revision DESC);
            CREATE INDEX IX_Artifacts_Project_ContentHash ON Artifacts(ProjectId, ContentHash);
            CREATE INDEX IX_Artifacts_Repository ON Artifacts(RepositoryId);
            CREATE INDEX IX_Handoffs_Project_CreatedUtc ON Handoffs(ProjectId, CreatedUtc DESC);
            CREATE INDEX IX_Handoffs_Repository ON Handoffs(RepositoryId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task ApplyVersionTwoAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE Decisions ADD COLUMN Branch TEXT NULL;
            ALTER TABLE Findings ADD COLUMN Branch TEXT NULL;
            ALTER TABLE Findings ADD COLUMN CommitSha TEXT NULL;
            ALTER TABLE Findings ADD COLUMN AuthoritativeReference TEXT NULL;
            CREATE INDEX IX_Decisions_Repository_Branch_CommitSha ON Decisions(RepositoryId, Branch, OriginatingCommitSha);
            CREATE INDEX IX_Findings_Repository_Branch_CommitSha ON Findings(RepositoryId, Branch, CommitSha);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task ApplyVersionThreeAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE MutationReceipts (
                Scope TEXT NOT NULL,
                Tool TEXT NOT NULL,
                RequestId TEXT NOT NULL,
                CanonicalPayloadHash TEXT NOT NULL,
                ReceiptJson TEXT NOT NULL,
                IssuedUtc TEXT NOT NULL,
                ExpiresUtc TEXT NOT NULL,
                CompletedUtc TEXT NOT NULL,
                PRIMARY KEY (Scope, Tool, RequestId)
            );

            CREATE INDEX IX_MutationReceipts_ExpiresUtc ON MutationReceipts(ExpiresUtc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task ApplyVersionFourAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE ContextEntries ADD COLUMN Tier INTEGER NOT NULL DEFAULT 2 CHECK (Tier BETWEEN 1 AND 3);
            ALTER TABLE ContextEntries ADD COLUMN Objective TEXT NULL;
            ALTER TABLE ContextEntries ADD COLUMN PhaseId TEXT NULL;
            ALTER TABLE Decisions ADD COLUMN Version INTEGER NOT NULL DEFAULT 1 CHECK (Version > 0);
            ALTER TABLE Phases ADD COLUMN Version INTEGER NOT NULL DEFAULT 1 CHECK (Version > 0);
            ALTER TABLE Phases ADD COLUMN ContextEntryId TEXT NULL;
            ALTER TABLE TestRuns ADD COLUMN Name TEXT NULL;
            ALTER TABLE TestRuns ADD COLUMN Summary TEXT NULL;
            ALTER TABLE TestRuns ADD COLUMN Evidence TEXT NULL;
            ALTER TABLE TestRuns ADD COLUMN ObservedUtc TEXT NULL;
            ALTER TABLE Findings ADD COLUMN Component TEXT NULL;
            ALTER TABLE Findings ADD COLUMN Location TEXT NULL;
            ALTER TABLE Findings ADD COLUMN Remediation TEXT NULL;
            ALTER TABLE Handoffs ADD COLUMN PhaseId TEXT NULL;

            CREATE TABLE McpRecordObservations (
                RecordKind INTEGER NOT NULL CHECK (RecordKind IN (0, 1, 2, 3, 4)),
                RecordId TEXT NOT NULL,
                Revision INTEGER NOT NULL CHECK (Revision > 0),
                ProjectId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                SourceKind TEXT NOT NULL,
                SourceObservedUtc TEXT NOT NULL,
                SourceReferenceKind TEXT NULL,
                SourceReferenceRepositoryId TEXT NULL,
                SourceReferenceId TEXT NULL,
                SourceReferenceRelativePath TEXT NULL,
                SourceReferenceUri TEXT NULL,
                SourceReferenceHash TEXT NULL,
                CanonicalRoot TEXT NOT NULL,
                Branch TEXT NULL,
                Head TEXT NULL,
                WorkingTreeFingerprint TEXT NULL,
                SnapshotObservedUtc TEXT NOT NULL,
                Completeness TEXT NOT NULL,
                RecordObservedUtc TEXT NOT NULL,
                PRIMARY KEY (RecordKind, RecordId, Revision),
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (RepositoryId, ProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE TABLE McpRecordReferences (
                RecordKind INTEGER NOT NULL,
                RecordId TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                Role INTEGER NOT NULL CHECK (Role IN (0, 1, 2, 3, 4, 5, 6, 7)),
                Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                ReferencedProjectId TEXT NOT NULL,
                ReferencedRepositoryId TEXT NULL,
                Kind TEXT NOT NULL,
                ReferenceId TEXT NULL,
                RelativePath TEXT NULL,
                Uri TEXT NULL,
                Hash TEXT NULL,
                PRIMARY KEY (RecordKind, RecordId, Revision, Role, Ordinal),
                FOREIGN KEY (RecordKind, RecordId, Revision) REFERENCES McpRecordObservations(RecordKind, RecordId, Revision) ON DELETE RESTRICT,
                FOREIGN KEY (ReferencedProjectId) REFERENCES Projects(Id) ON DELETE RESTRICT,
                FOREIGN KEY (ReferencedRepositoryId, ReferencedProjectId) REFERENCES Repositories(Id, ProjectId) ON DELETE RESTRICT
            );

            CREATE INDEX IX_McpRecordObservations_Project_Kind_ObservedUtc_Id
                ON McpRecordObservations(ProjectId, RecordKind, RecordObservedUtc DESC, RecordId ASC);
            CREATE INDEX IX_McpRecordReferences_ReferenceScope
                ON McpRecordReferences(ReferencedProjectId, ReferencedRepositoryId, Kind, ReferenceId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task ApplyVersionFiveAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ContextEntries AS predecessor
            SET Status = 1,
                SupersededUtc = (
                    SELECT MIN(successor.CreatedUtc)
                    FROM McpRecordReferences reference
                    JOIN ContextEntries successor ON successor.Id = reference.RecordId
                    WHERE reference.RecordKind = 0 AND reference.Revision = 1 AND reference.Role = 2 AND reference.Kind = 'record'
                      AND reference.ReferenceId = predecessor.Id
                      AND reference.ReferencedProjectId = predecessor.ProjectId
                      AND ((reference.ReferencedRepositoryId IS NULL AND predecessor.RepositoryId IS NULL) OR reference.ReferencedRepositoryId = predecessor.RepositoryId)
                      AND successor.ProjectId = predecessor.ProjectId
                      AND ((successor.RepositoryId IS NULL AND predecessor.RepositoryId IS NULL) OR successor.RepositoryId = predecessor.RepositoryId)
                      AND successor.Id <> predecessor.Id
                ),
                UpdatedUtc = (
                    SELECT MIN(successor.CreatedUtc)
                    FROM McpRecordReferences reference
                    JOIN ContextEntries successor ON successor.Id = reference.RecordId
                    WHERE reference.RecordKind = 0 AND reference.Revision = 1 AND reference.Role = 2 AND reference.Kind = 'record'
                      AND reference.ReferenceId = predecessor.Id
                      AND reference.ReferencedProjectId = predecessor.ProjectId
                      AND ((reference.ReferencedRepositoryId IS NULL AND predecessor.RepositoryId IS NULL) OR reference.ReferencedRepositoryId = predecessor.RepositoryId)
                      AND successor.ProjectId = predecessor.ProjectId
                      AND ((successor.RepositoryId IS NULL AND predecessor.RepositoryId IS NULL) OR successor.RepositoryId = predecessor.RepositoryId)
                      AND successor.Id <> predecessor.Id
                )
            WHERE predecessor.Status = 0 AND predecessor.SupersededUtc IS NULL
              AND EXISTS (
                    SELECT 1
                    FROM McpRecordReferences reference
                    JOIN ContextEntries successor ON successor.Id = reference.RecordId
                    WHERE reference.RecordKind = 0 AND reference.Revision = 1 AND reference.Role = 2 AND reference.Kind = 'record'
                      AND reference.ReferenceId = predecessor.Id
                      AND reference.ReferencedProjectId = predecessor.ProjectId
                      AND ((reference.ReferencedRepositoryId IS NULL AND predecessor.RepositoryId IS NULL) OR reference.ReferencedRepositoryId = predecessor.RepositoryId)
                      AND successor.ProjectId = predecessor.ProjectId
                      AND ((successor.RepositoryId IS NULL AND predecessor.RepositoryId IS NULL) OR successor.RepositoryId = predecessor.RepositoryId)
                      AND successor.Id <> predecessor.Id
                );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
