using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AIContextMCP.Core.Tests;

public sealed class GitBoundaryTests
{
    [Fact]
    public async Task Inspector_reads_fixed_metadata_without_running_git()
    {
        using var fixture = new ControlledGitFixture();
        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);
        Assert.Equal(fixture.RepositoryPath, state.CanonicalPath);
        Assert.Equal(fixture.Run("branch", "--show-current"), state.Branch);
        Assert.Equal(fixture.Run("rev-parse", "HEAD"), state.HeadCommitSha);
        Assert.Equal(WorkingTreeState.Clean, state.WorkingTree);
        Assert.Matches("^[0-9a-f]{64}$", state.WorkingTreeFingerprint);
    }

    [Theory]
    [InlineData(@"D:\Projects\.AIContextMCP-Test\missing")]
    [InlineData(@"D:\Projects\.AIContextMCP-Test\repo-prefix")]
    [InlineData(@"D:\Other\repo")]
    [InlineData(@"D:\Projects")]
    [InlineData(@"D:\Projects\.AIContextMCP-Test\..\repo")]
    [InlineData(@"\\server\share\repo")]
    public void Boundary_rejects_unsafe_or_missing_paths(string path)
    {
        using var fixture = new ControlledGitFixture();
        var boundary = new RepositoryBoundary([@"D:\Projects"]);
        Assert.Throws<ApplicationException>(() => boundary.ValidateRepositoryPath(path));
    }

    [Fact]
    public void Boundary_rejects_non_git_and_unsafe_config()
    {
        using var fixture = new ControlledGitFixture();
        File.Delete(Path.Combine(fixture.RepositoryPath, ".git", "config"));
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, ".git", "config"), "[include]\n path = outside\n");
        var boundary = new RepositoryBoundary([@"D:\Projects"]);
        Assert.Throws<ApplicationException>(() => boundary.PreflightGitRepository(fixture.RepositoryPath));
        var plain = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "plain"); Directory.CreateDirectory(plain);
        Assert.Throws<ApplicationException>(() => boundary.PreflightGitRepository(plain));
    }

    [Fact]
    public async Task Inspector_distinguishes_an_unborn_repository_from_a_rejected_path()
    {
        using var fixture = new ControlledGitFixture();
        var unborn = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "unborn");
        Directory.CreateDirectory(unborn);
        fixture.Run("-C", unborn, "init");

        var error = await Assert.ThrowsAsync<ApplicationException>(() => new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(unborn, CancellationToken.None));

        Assert.Equal(ApplicationErrorCode.UnbornRepository, error.Code);
    }

    [Fact]
    public async Task Inspector_reads_a_packed_branch_ref()
    {
        using var fixture = new ControlledGitFixture();
        fixture.Run("pack-refs", "--all", "--prune");
        Directory.Delete(Path.Combine(fixture.RepositoryPath, ".git", "refs", "heads"), recursive: true);

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(fixture.Run("rev-parse", "HEAD"), state.HeadCommitSha);
    }

    [Fact]
    public async Task Inspector_reads_a_packed_branch_ref_after_64_kib()
    {
        using var fixture = new ControlledGitFixture();
        var commit = fixture.Run("rev-parse", "HEAD");
        var branch = fixture.Run("branch", "--show-current");
        var packed = new StringBuilder("# pack-refs with: peeled fully-peeled\n");
        for (var index = 0; index < 1500; index++) packed.Append(commit).Append(" refs/tags/test-").Append(index.ToString("D4")).Append('\n');
        packed.Append(commit).Append(" refs/heads/").Append(branch).Append('\n');
        var packedPath = Path.Combine(fixture.RepositoryPath, ".git", "packed-refs");
        File.WriteAllText(packedPath, packed.ToString(), new UTF8Encoding(false));
        Assert.True(new FileInfo(packedPath).Length > 64 * 1024);
        Directory.Delete(Path.Combine(fixture.RepositoryPath, ".git", "refs", "heads"), recursive: true);

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(commit, state.HeadCommitSha);
    }

    [Fact]
    public async Task Inspector_rejects_a_dangling_reparse_point_for_loose_refs()
    {
        using var fixture = new ControlledGitFixture();
        var heads = Path.Combine(fixture.RepositoryPath, ".git", "refs", "heads");
        var target = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "dangling-heads-target");
        Directory.Delete(heads, recursive: true);
        Directory.CreateDirectory(target);
        CreateJunction(heads, target);
        Directory.Delete(target);
        try
        {
            var error = await Assert.ThrowsAsync<ApplicationException>(() => new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None));
            Assert.Equal(ApplicationErrorCode.PathRejected, error.Code);
        }
        finally
        {
            try { Directory.Delete(heads); } catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task Inspector_rejects_sensitive_parent_directory_even_with_source_extension()
    {
        using var fixture = new ControlledGitFixture();
        var path = Path.Combine(fixture.RepositoryPath, "secrets.cs", "config.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "safe-looking");

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
    }

    [Theory]
    [InlineData("CredentialProvider.cs")]
    [InlineData("CredentialProvider.sh")]
    public async Task Inspector_allows_credential_handling_source_files(string fileName)
    {
        using var fixture = new ControlledGitFixture();
        var path = Path.Combine(fixture.RepositoryPath, fileName);
        File.WriteAllText(path, "internal sealed class CredentialProvider { }");

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Dirty, state.WorkingTree);
        Assert.NotNull(state.WorkingTreeFingerprint);
    }

    [Theory]
    [InlineData("[include]\n\tpath = D:/outside\n", "D:/outside")]
    [InlineData("[include]\npath = D:/out\\\nside\n", "D:/outside")]
    [InlineData("\uFEFF[include]\npath = D:/outside\n", "D:/outside")]
    public void Boundary_rejects_git_recognized_include_variants(string config, string expectedPath)
    {
        using var fixture = new ControlledGitFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, ".git", "config"), config, new UTF8Encoding(false));
        Assert.Equal(expectedPath, fixture.Run("config", "--local", "--get", "include.path"));
        Assert.Throws<ApplicationException>(() => new RepositoryBoundary([@"D:\Projects"]).PreflightGitRepository(fixture.RepositoryPath));
    }

    [Fact]
    public void Boundary_rejects_linked_git_metadata()
    {
        using var fixture = new ControlledGitFixture();
        var metadata = Path.Combine(fixture.RepositoryPath, ".git");
        Directory.Move(metadata, metadata + "-saved");
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, ".git"), "gitdir: D:\\outside\\metadata\n");
        var boundary = new RepositoryBoundary([@"D:\Projects"]);
        Assert.Throws<ApplicationException>(() => boundary.PreflightGitRepository(fixture.RepositoryPath));
    }

    [Fact]
    public async Task Snapshot_does_not_consume_new_external_metadata_while_held()
    {
        using var fixture = new ControlledGitFixture();
        var boundary = new RepositoryBoundary([@"D:\Projects"]);
        var info = Path.Combine(fixture.RepositoryPath, ".git", "objects", "info");
        Directory.CreateDirectory(info);
        var alternate = Path.Combine(info, "alternates");
        using (var snapshot = boundary.AcquireGitMetadataSnapshot(fixture.RepositoryPath))
        {
            File.WriteAllText(alternate, "D:\\outside\n");
            var state = await new GitRepositoryInspector(boundary).InspectAsync(fixture.RepositoryPath, CancellationToken.None);
            Assert.Equal(snapshot.HeadCommitSha, state.HeadCommitSha);
            Assert.Equal(snapshot.Branch, state.Branch);
            Assert.Equal(WorkingTreeState.Clean, state.WorkingTree);
            Assert.NotNull(state.WorkingTreeFingerprint);
        }

        Assert.True(File.Exists(alternate));
    }

    [Fact]
    public void Snapshot_blocks_git_directory_rename_while_held()
    {
        using var fixture = new ControlledGitFixture();
        var gitDirectory = Path.Combine(fixture.RepositoryPath, ".git");
        using (new RepositoryBoundary([@"D:\Projects"]).AcquireGitMetadataSnapshot(fixture.RepositoryPath))
        {
            Assert.NotNull(Record.Exception(() => Directory.Move(gitDirectory, gitDirectory + "-moved")));
        }

        Assert.True(Directory.Exists(gitDirectory));
    }

    [Fact]
    public async Task Inspector_fingerprints_staged_and_unstaged_changes_to_the_same_path()
    {
        using var fixture = new ControlledGitFixture();
        var inspector = new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"]));
        var path = Path.Combine(fixture.RepositoryPath, "README.md");
        var clean = await inspector.InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        File.WriteAllText(path, "staged change\n");
        fixture.Run("add", "README.md");
        var staged = await inspector.InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        File.WriteAllText(path, "unstaged change\n");
        var unstaged = await inspector.InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        File.WriteAllText(path, "different unstaged change\n");
        var changedAgain = await inspector.InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Clean, clean.WorkingTree);
        Assert.Equal(WorkingTreeState.Dirty, staged.WorkingTree);
        Assert.Equal(WorkingTreeState.Dirty, unstaged.WorkingTree);
        Assert.Equal(WorkingTreeState.Dirty, changedAgain.WorkingTree);
        Assert.NotEqual(clean.WorkingTreeFingerprint, staged.WorkingTreeFingerprint);
        Assert.NotEqual(staged.WorkingTreeFingerprint, unstaged.WorkingTreeFingerprint);
        Assert.NotEqual(unstaged.WorkingTreeFingerprint, changedAgain.WorkingTreeFingerprint);
    }

    [Theory]
    [InlineData("true", WorkingTreeState.Clean)]
    [InlineData("input", WorkingTreeState.Clean)]
    [InlineData("false", WorkingTreeState.Dirty)]
    public async Task Inspector_respects_effective_autocrlf_for_crlf_worktree_content(string autocrlf, WorkingTreeState expected)
    {
        using var fixture = new ControlledGitFixture();
        fixture.Run("config", "core.autocrlf", autocrlf);
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "README.md"), "fixture\r\n", new UTF8Encoding(false));

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(expected, state.WorkingTree);
        Assert.Equal(expected == WorkingTreeState.Clean, string.Equals(fixture.Run("hash-object", "--path=README.md", "README.md"), fixture.Run("rev-parse", ":README.md"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspector_keeps_real_content_changes_dirty_when_autocrlf_normalizes_line_endings()
    {
        using var fixture = new ControlledGitFixture();
        fixture.Run("config", "core.autocrlf", "true");
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "README.md"), "changed\r\n", new UTF8Encoding(false));

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Dirty, state.WorkingTree);
        Assert.NotEmpty(fixture.Run("status", "--porcelain"));
    }

    [Fact]
    public async Task Inspector_keeps_binary_content_dirty_when_autocrlf_is_enabled()
    {
        using var fixture = new ControlledGitFixture();
        fixture.Run("config", "core.autocrlf", "true");
        File.WriteAllBytes(Path.Combine(fixture.RepositoryPath, "README.md"), [.. Encoding.ASCII.GetBytes("fixture"), 0, (byte)'\r', (byte)'\n']);

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Dirty, state.WorkingTree);
        Assert.NotEmpty(fixture.Run("status", "--porcelain"));
    }

    [Fact]
    public async Task Inspector_returns_unknown_for_ambiguous_crlf_content_when_autocrlf_is_enabled()
    {
        using var fixture = new ControlledGitFixture();
        fixture.Run("config", "core.autocrlf", "true");
        File.WriteAllBytes(Path.Combine(fixture.RepositoryPath, "README.md"), [.. Encoding.ASCII.GetBytes("fixture"), 0x1A, (byte)'\r', (byte)'\n']);

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
    }

    [Fact]
    public async Task Inspector_returns_unknown_when_info_attributes_can_override_autocrlf()
    {
        using var fixture = new ControlledGitFixture();
        fixture.Run("config", "core.autocrlf", "true");
        var attributes = Path.Combine(fixture.RepositoryPath, ".git", "info", "attributes");
        Directory.CreateDirectory(Path.GetDirectoryName(attributes)!);
        File.WriteAllText(attributes, "README.md -text\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "README.md"), "fixture\r\n", new UTF8Encoding(false));

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
        Assert.NotEmpty(fixture.Run("status", "--porcelain"));
    }

    [Theory]
    [InlineData("* text\n")]
    [InlineData("* text=auto eol=lf\n*.ps1 text eol=lf\n")]
    public async Task Inspector_accepts_lf_only_root_attributes_and_reports_git_consistent_states(string attributes)
    {
        using var fixture = new ControlledGitFixture();
        var attributesPath = Path.Combine(fixture.RepositoryPath, ".gitattributes");
        File.WriteAllText(attributesPath, attributes, new UTF8Encoding(false));
        fixture.Run("add", ".gitattributes");
        fixture.Run("-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "attributes");
        var inspector = new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"]));

        var clean = await inspector.InspectAsync(fixture.RepositoryPath, CancellationToken.None);
        Assert.Empty(fixture.Run("status", "--porcelain"));
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "README.md"), "changed\n", new UTF8Encoding(false));
        var dirty = await inspector.InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Clean, clean.WorkingTree);
        Assert.Equal(WorkingTreeState.Dirty, dirty.WorkingTree);
        Assert.NotEmpty(fixture.Run("status", "--porcelain"));
        Assert.NotEqual(fixture.Run("hash-object", "--path=README.md", "README.md"), fixture.Run("rev-parse", ":README.md"));
    }

    [Fact]
    public async Task Inspector_returns_unknown_for_cr_content_with_lf_only_root_attributes()
    {
        using var fixture = new ControlledGitFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, ".gitattributes"), "* text=auto eol=lf\n", new UTF8Encoding(false));
        fixture.Run("add", ".gitattributes");
        fixture.Run("-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "attributes");
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "README.md"), "fixture\r\n", new UTF8Encoding(false));

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
        Assert.Equal(fixture.Run("hash-object", "--path=README.md", "README.md"), fixture.Run("rev-parse", ":README.md"));
    }

    [Theory]
    [InlineData("* text eol=crlf\n")]
    [InlineData("* -text\n")]
    [InlineData("[attr]identity text\n")]
    [InlineData("src/*.cs text eol=lf\n")]
    [InlineData("* text filter=clean\n")]
    [InlineData("* text=auto eol=lf\r\n")]
    public async Task Inspector_returns_unknown_for_unsupported_root_attributes(string attributes)
    {
        using var fixture = new ControlledGitFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, ".gitattributes"), attributes, new UTF8Encoding(false));

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
    }

    [Fact]
    public async Task Inspector_returns_unknown_for_nested_attributes()
    {
        using var fixture = new ControlledGitFixture();
        var nested = Path.Combine(fixture.RepositoryPath, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, ".gitattributes"), "* text=auto eol=lf\n", new UTF8Encoding(false));

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
    }

    [Fact]
    public async Task Inspector_marks_untracked_content_dirty_and_fingerprints_it()
    {
        using var fixture = new ControlledGitFixture();
        var inspector = new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"]));
        var clean = await inspector.InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        File.WriteAllText(Path.Combine(fixture.RepositoryPath, "untracked.txt"), "new content\n");
        var dirty = await inspector.InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Clean, clean.WorkingTree);
        Assert.Equal(WorkingTreeState.Dirty, dirty.WorkingTree);
        Assert.NotNull(dirty.WorkingTreeFingerprint);
        Assert.NotEqual(clean.WorkingTreeFingerprint, dirty.WorkingTreeFingerprint);
    }

    [Fact]
    public async Task Inspector_returns_unknown_without_reading_case_insensitive_sensitive_path()
    {
        using var fixture = new ControlledGitFixture();
        var keyPath = Path.Combine(fixture.RepositoryPath, "ID_RSA");
        using var key = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        key.WriteByte((byte)'x');
        key.Flush(flushToDisk: true);

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
    }

    [Fact]
    public async Task Inspector_returns_unknown_for_an_unsupported_index_feature()
    {
        using var fixture = new ControlledGitFixture();
        var indexPath = Path.Combine(fixture.RepositoryPath, ".git", "index");
        var index = File.ReadAllBytes(indexPath);
        index[12 + 60] |= 0x40;
        SHA1.HashData(index.AsSpan(0, index.Length - 20)).CopyTo(index, index.Length - 20);
        File.WriteAllBytes(indexPath, index);

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
    }

    [Fact]
    public async Task Inspector_returns_unknown_for_packed_head_objects()
    {
        using var fixture = new ControlledGitFixture();
        fixture.Run("repack", "-ad");

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
    }

    [Fact]
    public async Task Inspector_returns_unknown_for_an_oversized_worktree_file()
    {
        using var fixture = new ControlledGitFixture();
        File.WriteAllBytes(Path.Combine(fixture.RepositoryPath, "too-large.txt"), new byte[1024 * 1024 + 1]);

        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);

        Assert.Equal(WorkingTreeState.Unknown, state.WorkingTree);
        Assert.Null(state.WorkingTreeFingerprint);
    }

    [Fact]
    public void Observer_returns_unknown_when_a_file_is_created_after_the_watcher_is_armed()
    {
        using var fixture = new ControlledGitFixture();
        var createdPath = Path.Combine(fixture.RepositoryPath, "created-after-watch.txt");
        var boundary = new RepositoryBoundary([@"D:\Projects"]);
        var created = false;

        using var snapshot = boundary.AcquireGitMetadataSnapshot(fixture.RepositoryPath);
        var state = boundary.ObserveWorkingTree(snapshot, () =>
        {
            File.WriteAllText(createdPath, "created\n");
            created = true;
        });

        Assert.True(created);
        Assert.Equal(WorkingTreeState.Unknown, state.State);
        Assert.Null(state.Fingerprint);
    }

    [Fact]
    public async Task Inspector_sanitizes_origin_from_verified_config()
    {
        using var fixture = new ControlledGitFixture();
        File.AppendAllText(Path.Combine(fixture.RepositoryPath, ".git", "config"), "\n[remote \"origin\"]\n\turl = https://user:secret@example.invalid/org/repo.git\n");
        var state = await new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None);
        Assert.Equal("https://example.invalid/org/repo", state.Remote);
        Assert.Equal("org", state.Organization);
        Assert.Equal("repo", state.RepositoryName);
    }

    [Theory]
    [InlineData("\turl = \"https://example.invalid/org/repo.git\"\n")]
    [InlineData("\turl = https://example.invalid/org/repo.git\n\turl = https://example.invalid/org/other.git\n")]
    public async Task Inspector_rejects_unsupported_origin_syntax(string urls)
    {
        using var fixture = new ControlledGitFixture();
        File.AppendAllText(Path.Combine(fixture.RepositoryPath, ".git", "config"), $"\n[remote \"origin\"]\n{urls}");
        var error = await Assert.ThrowsAsync<ApplicationException>(() => new GitRepositoryInspector(new RepositoryBoundary([@"D:\Projects"])).InspectAsync(fixture.RepositoryPath, CancellationToken.None));
        Assert.Equal(ApplicationErrorCode.PathRejected, error.Code);
    }

    [Fact]
    public void Boundary_rejects_common_directory_metadata()
    {
        using var fixture = new ControlledGitFixture();
        File.WriteAllText(Path.Combine(fixture.RepositoryPath, ".git", "commondir"), ".\n");
        var error = Assert.Throws<ApplicationException>(() => new RepositoryBoundary([@"D:\Projects"]).PreflightGitRepository(fixture.RepositoryPath));
        Assert.Equal(ApplicationErrorCode.PathRejected, error.Code);
    }

    [Fact]
    public void Artifact_boundary_hashes_only_a_verified_regular_relative_file()
    {
        using var fixture = new ControlledGitFixture();
        var evidence = Path.Combine(fixture.RepositoryPath, "evidence.txt");
        File.WriteAllText(evidence, "sanitized evidence\n", new UTF8Encoding(false));

        var actual = ArtifactFileBoundary.Validate(fixture.RepositoryPath, "evidence.txt", StorageLimits.Content);

        Assert.Equal("evidence.txt", actual.Reference);
        Assert.Equal(new FileInfo(evidence).Length, actual.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidence))).ToLowerInvariant(), actual.ContentHash);
        var outside = Assert.Throws<ApplicationException>(() => ArtifactFileBoundary.Validate(fixture.RepositoryPath, "..\\outside.txt", StorageLimits.Content));
        Assert.Equal(ApplicationErrorCode.PathRejected, outside.Code);

        var target = Path.Combine(fixture.RepositoryPath, "artifact-target");
        var link = Path.Combine(fixture.RepositoryPath, "artifact-link");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "linked.txt"), "not directly reachable\n", new UTF8Encoding(false));
        CreateJunction(link, target);
        try
        {
            var reparse = Assert.Throws<ApplicationException>(() => ArtifactFileBoundary.Validate(fixture.RepositoryPath, "artifact-link\\linked.txt", StorageLimits.Content));
            Assert.Equal(ApplicationErrorCode.PathRejected, reparse.Code);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Fact]
    public void Boundary_rejects_reparse_point_repository()
    {
        using var fixture = new ControlledGitFixture();
        var link = Path.Combine(Path.GetDirectoryName(fixture.RepositoryPath)!, "junction");
        CreateJunction(link, fixture.RepositoryPath);
        try
        {
            var boundary = new RepositoryBoundary([@"D:\Projects"]);
            Assert.Throws<ApplicationException>(() => boundary.ValidateRepositoryPath(link));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target }
        });
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var complete = Task.WhenAll(process.WaitForExitAsync(), output, error);
        if (Task.WhenAny(complete, Task.Delay(TimeSpan.FromSeconds(10))).GetAwaiter().GetResult() != complete)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            Task.WhenAny(complete, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
            throw new TimeoutException("Timed out creating controlled junction.");
        }

        complete.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
    }

}
