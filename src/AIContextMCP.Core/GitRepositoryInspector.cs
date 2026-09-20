using System.Text.RegularExpressions;

namespace AIContextMCP.Core;

internal sealed class GitRepositoryInspector
{
    private static readonly Regex ScpRemote = new("^(?:[^@\\s]+@)?(?<host>[^:\\s/]+):(?<path>[^\\s]+)$", RegexOptions.CultureInvariant);
    private readonly RepositoryBoundary _boundary;

    public GitRepositoryInspector(RepositoryBoundary boundary)
    {
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
    }

    public Task<GitRepositoryState> InspectAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var snapshot = _boundary.AcquireGitMetadataSnapshot(repositoryPath);
        var workingTree = _boundary.ObserveWorkingTree(snapshot);
        var normalizedRemote = NormalizeRemote(snapshot.OriginUrl);
        var (organization, repositoryName) = DescribeRepository(normalizedRemote, snapshot.CanonicalPath);
        if (organization?.Length > StorageLimits.Name || repositoryName.Length > StorageLimits.Name)
        {
            throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Git repository identity exceeds its limit.");
        }

        return Task.FromResult(new GitRepositoryState(
            snapshot.CanonicalPath,
            snapshot.Branch,
            snapshot.HeadCommitSha,
            normalizedRemote,
            organization,
            repositoryName,
            workingTree.State,
            workingTree.Fingerprint,
            workingTree.Warning));
    }

    internal static string? NormalizeRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }

        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh" or "git")
        {
            var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty };
            return BoundedRemote(TrimGitSuffix(builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/')));
        }

        var scp = ScpRemote.Match(remote);
        if (scp.Success)
        {
            return NormalizeRemote($"ssh://{scp.Groups["host"].Value.ToLowerInvariant()}/{scp.Groups["path"].Value.TrimStart('/')}");
        }

        return null;
    }

    private static string BoundedRemote(string remote)
    {
        if (remote.Length > StorageLimits.Identifier)
        {
            throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Git remote exceeds its limit.");
        }

        return remote;
    }

    private static (string? Organization, string RepositoryName) DescribeRepository(string? remote, string root)
    {
        var fallback = new DirectoryInfo(root).Name;
        if (remote is null || !Uri.TryCreate(remote, UriKind.Absolute, out var uri))
        {
            return (null, fallback);
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = parts.LastOrDefault();
        return (parts.Length > 1 ? parts[^2] : null, string.IsNullOrWhiteSpace(name) ? fallback : name);
    }

    private static string TrimGitSuffix(string value) => value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
}

public static class GitRemoteIdentity
{
    public static string? Normalize(string? remote) => GitRepositoryInspector.NormalizeRemote(remote);
}
