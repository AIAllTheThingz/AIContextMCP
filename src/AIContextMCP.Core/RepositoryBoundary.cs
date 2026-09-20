using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AIContextMCP.Core;

internal sealed partial class RepositoryBoundary
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileListDirectory = 0x0001;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int GitMetadataBytes = 64 * 1024;
    private readonly string[] _roots;

    public RepositoryBoundary(IEnumerable<string> approvedRoots)
    {
        ArgumentNullException.ThrowIfNull(approvedRoots);
        _roots = approvedRoots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_roots.Length == 0)
        {
            throw new ArgumentException("At least one approved repository root is required.", nameof(approvedRoots));
        }
    }

    public string ValidateRepositoryPath(string path)
    {
        var fullPath = NormalizeCandidate(path);
        var root = _roots.FirstOrDefault(candidate => IsDescendant(fullPath, candidate));
        if (root is null)
        {
            throw new ApplicationException(ApplicationErrorCode.RepositoryOutsideApprovedRoot, "Repository path is outside approved roots.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new ApplicationException(ApplicationErrorCode.RepositoryNotFound, "Repository path was not found.");
        }

        RejectReparsePoints(root, fullPath);
        return fullPath;
    }

    public void PreflightGitRepository(string repositoryRoot)
    {
        using var snapshot = AcquireGitMetadataSnapshot(repositoryRoot);
    }

    public GitMetadataSnapshot AcquireGitMetadataSnapshot(string repositoryRoot)
    {
        var root = ValidateRepositoryPath(repositoryRoot);
        var snapshot = new GitMetadataSnapshot(root);
        try
        {
            HoldDirectoryChain(root, snapshot);
            var gitDirectory = Path.Combine(root, ".git");
            HoldDirectory(snapshot, gitDirectory, gitDirectory: true);
            if (MetadataEntryExists(Path.Combine(gitDirectory, "commondir")))
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git common directories are not supported.");
            }

            var config = OpenVerifiedFile(Path.Combine(gitDirectory, "config"));
            snapshot.Hold(config);
            var configText = ReadBoundedUtf8(config, "configuration");
            ValidateGitConfig(configText);

            var head = OpenVerifiedFile(Path.Combine(gitDirectory, "HEAD"));
            snapshot.Hold(head);
            var headText = ReadSingleLine(head, "HEAD");
            var (branch, commit) = ReadHead(gitDirectory, headText, snapshot);
            var lineEndings = ReadEffectiveAutocrlf(configText, snapshot);
            snapshot.Set(branch, commit, ReadOriginUrl(configText), lineEndings.Autocrlf, lineEndings.Unsupported);
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    internal static ArtifactFileValidation ValidateArtifactFile(string artifactRoot, string reference, int maximumBytes)
    {
        if (maximumBytes is < 1 or > StorageLimits.Content)
        {
            throw new ApplicationException(ApplicationErrorCode.InvalidInput, "Artifact size limit is invalid.");
        }

        var root = NormalizeRoot(artifactRoot);
        var path = ResolveArtifactPath(root, reference);
        try
        {
            using var snapshot = new GitMetadataSnapshot(root);
            HoldDirectoryChain(root, snapshot);
            HoldArtifactParentDirectories(root, path, snapshot);
            using var file = OpenVerifiedFile(path);
            if (file.Length > maximumBytes)
            {
                throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Artifact exceeds its size limit.");
            }

            var bytes = ReadArtifactBytes(file, maximumBytes);
            return new ArtifactFileValidation(reference, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or OverflowException)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Artifact could not be safely read.", null, exception);
        }
    }

    internal static ArtifactFileValidation ValidateRepositoryDocument(string repositoryRoot, string reference, int maximumBytes)
    {
        var normalized = reference.Replace('\\', '/');
        if (!IsSupportedRelativePath(normalized))
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Repository document must be a supported relative path.");
        }

        if (IsSensitivePath(normalized))
        {
            throw new ApplicationException(ApplicationErrorCode.SecretRejected, "Repository document may contain sensitive content.");
        }

        return ValidateArtifactFile(repositoryRoot, reference, maximumBytes);
    }

    private static string ResolveArtifactPath(string root, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)
            || reference.Length > StorageLimits.Reference
            || Path.IsPathRooted(reference)
            || reference.IndexOf('\0') >= 0
            || reference.Contains(':', StringComparison.Ordinal)
            || reference.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Artifact reference must be a safe relative path.");
        }

        try
        {
            var path = Path.GetFullPath(Path.Combine(root, reference));
            if (!IsDescendant(path, root))
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, "Artifact reference is outside the configured root.");
            }

            return path;
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Artifact reference is malformed.", null, exception);
        }
    }

    private static void HoldArtifactParentDirectories(string root, string path, GitMetadataSnapshot snapshot)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new ApplicationException(ApplicationErrorCode.PathRejected, "Artifact path has no parent.");
        var relative = Path.GetRelativePath(root, parent);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            HoldDirectory(snapshot, current);
        }
    }

    private static byte[] ReadArtifactBytes(FileStream stream, int maximumBytes)
    {
        try
        {
            var length = stream.Length;
            if (length > maximumBytes)
            {
                throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Artifact exceeds its size limit.");
            }

            var bytes = new byte[checked((int)length)];
            stream.Position = 0;
            stream.ReadExactly(bytes);
            if (stream.Length != length)
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, "Artifact changed while it was being read.");
            }

            return bytes;
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Artifact could not be safely read.", null, exception);
        }
    }

    private static (string? Branch, string Commit) ReadHead(string gitDirectory, string head, GitMetadataSnapshot snapshot)
    {
        if (IsCommitSha(head))
        {
            return (null, head.ToLowerInvariant());
        }

        const string prefix = "ref: ";
        if (!head.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ApplicationException(ApplicationErrorCode.NotGitRepository, "Git HEAD is unsupported or unborn.");
        }

        var branch = ParseBranch(head[prefix.Length..]);
        var segments = branch.Split('/');
        var referenceDirectory = Path.Combine(gitDirectory, "refs", "heads");
        var looseAvailable = Directory.Exists(referenceDirectory);
        if (looseAvailable) HoldRelativeDirectories(gitDirectory, ["refs", "heads"], snapshot);
        foreach (var segment in segments[..^1])
        {
            referenceDirectory = Path.Combine(referenceDirectory, segment);
            if (!Directory.Exists(referenceDirectory))
            {
                looseAvailable = false;
                break;
            }

            HoldDirectory(snapshot, referenceDirectory);
        }

        var referencePath = Path.Combine(referenceDirectory, segments[^1]);
        if (looseAvailable && File.Exists(referencePath))
        {
            var reference = OpenVerifiedFile(referencePath);
            snapshot.Hold(reference);
            var commit = ReadSingleLine(reference, "branch reference");
            if (!IsCommitSha(commit))
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git branch reference is invalid.");
            }

            return (branch, commit.ToLowerInvariant());
        }

        var packedPath = Path.Combine(gitDirectory, "packed-refs");
        if (File.Exists(packedPath))
        {
            var packed = OpenVerifiedFile(packedPath);
            snapshot.Hold(packed);
            var commit = ReadPackedBranch(packed, branch);
            if (commit is not null) return (branch, commit);
        }

        throw new ApplicationException(ApplicationErrorCode.UnbornRepository, "Git repository has no commit yet.");
    }

    private static string? ReadPackedBranch(FileStream packed, string branch)
    {
        var target = $"refs/heads/{branch}";
        foreach (var line in ReadBoundedUtf8(packed, "packed references").Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0 || value[0] is '#' or '^') continue;
            var separator = value.IndexOf(' ');
            if (separator <= 0 || !value[(separator + 1)..].Equals(target, StringComparison.Ordinal)) continue;
            var commit = value[..separator];
            if (!IsCommitSha(commit)) throw new ApplicationException(ApplicationErrorCode.PathRejected, "Packed Git branch reference is invalid.");
            return commit.ToLowerInvariant();
        }

        return null;
    }

    private static string ParseBranch(string reference)
    {
        const string prefix = "refs/heads/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)
            || reference.Length <= prefix.Length
            || reference.Length - prefix.Length > StorageLimits.ShortText)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git branch reference is unsupported.");
        }

        var branch = reference[prefix.Length..];
        var segments = branch.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith('.')
                || segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
                || segment.Contains("..", StringComparison.Ordinal)
                || segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')))
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git branch reference is unsupported.");
        }

        return branch;
    }

    private static bool IsCommitSha(string value) =>
        value.Length == 40 && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    private static string ReadSingleLine(FileStream stream, string name)
    {
        var value = ReadBoundedUtf8(stream, name);
        if (value.EndsWith("\r\n", StringComparison.Ordinal))
        {
            value = value[..^2];
        }
        else if (value.EndsWith('\n') || value.EndsWith('\r'))
        {
            value = value[..^1];
        }

        if (value.Length == 0 || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ApplicationException(ApplicationErrorCode.NotGitRepository, $"Git {name} is invalid.");
        }

        return value;
    }

    private static string ReadBoundedUtf8(FileStream stream, string name)
    {
        try
        {
            var length = stream.Length;
            if (length > GitMetadataBytes)
            {
                throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, $"Git {name} exceeds the supported size.");
            }

            var bytes = new byte[checked((int)length)];
            stream.Position = 0;
            stream.ReadExactly(bytes);

            if (stream.Length != length)
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, $"Git {name} changed while it was being read.");
            }

            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or OverflowException)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, $"Git {name} could not be safely read.", null, exception);
        }
    }

    private static void ValidateGitConfig(string config)
    {
        if (config.Split('\n').Any(IsUnsafeGitConfigLine))
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git configuration contains unsupported external dependencies.");
        }
    }

    private static string? ReadOriginUrl(string config)
    {
        var inOrigin = false;
        string? origin = null;
        foreach (var rawLine in config.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || !line[..separator].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = line[(separator + 1)..].Trim();
            if (url.Length == 0
                || url.IndexOfAny(['\0', '\r', '\n', '"', '\\']) >= 0
                || url.Any(char.IsWhiteSpace)
                || origin is not null)
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git origin URL is unsupported.");
            }

            origin = url;
        }

        return origin;
    }

    private static (bool? Autocrlf, bool Unsupported) ReadEffectiveAutocrlf(string localConfig, GitMetadataSnapshot snapshot)
    {
        var system = ReadSystemGitConfig(snapshot);
        var global = ReadGlobalGitConfig(snapshot);
        var local = ReadAutocrlf(localConfig);
        return (
            local.Autocrlf ?? global.Autocrlf ?? system.Autocrlf,
            local.Unsupported || global.Unsupported || system.Unsupported || HasUnsupportedConfigurationOverrides() || HasUnsupportedGlobalAttributes(snapshot));
    }

    private static (bool Exists, bool? Autocrlf, bool Unsupported) ReadSystemGitConfig(GitMetadataSnapshot snapshot)
    {
        var noSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        if (noSystem is not null)
        {
            var skip = ReadGitBoolean(noSystem);
            if (skip is null) return (false, null, true);
            if (skip.Value) return default;
        }

        var path = Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "etc", "gitconfig");
        return ReadGitConfig(path, snapshot);
    }

    private static (bool Exists, bool? Autocrlf, bool Unsupported) ReadGlobalGitConfig(GitMetadataSnapshot snapshot)
    {
        var configured = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        if (configured is not null) return ReadGitConfig(configured, snapshot);

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return (false, null, true);

        var xdgRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var xdgPath = Path.Combine(string.IsNullOrWhiteSpace(xdgRoot) ? Path.Combine(home, ".config") : xdgRoot, "git", "config");
        var xdg = ReadGitConfig(xdgPath, snapshot);
        var legacy = ReadGitConfig(Path.Combine(home, ".gitconfig"), snapshot);
        return (xdg.Exists || legacy.Exists, legacy.Autocrlf ?? xdg.Autocrlf, xdg.Unsupported || legacy.Unsupported);
    }

    private static (bool Exists, bool? Autocrlf, bool Unsupported) ReadGitConfig(string path, GitMetadataSnapshot snapshot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return (false, null, true);
            }

            var stream = TryOpenVerifiedFile(Path.GetFullPath(path));
            if (stream is null) return default;
            snapshot.Hold(stream);
            var setting = ReadAutocrlf(ReadBoundedUtf8(stream, "configuration"));
            return (true, setting.Autocrlf, setting.Unsupported);
        }
        catch (Exception exception) when (exception is ApplicationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (false, null, true);
        }
    }

    private static (bool? Autocrlf, bool Unsupported) ReadAutocrlf(string config)
    {
        var inCore = false;
        bool? value = null;
        var unsupported = false;
        foreach (var rawLine in config.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                unsupported |= line.StartsWith("[include", StringComparison.OrdinalIgnoreCase);
                unsupported |= line.StartsWith("[core", StringComparison.OrdinalIgnoreCase) && !line.Equals("[core]", StringComparison.OrdinalIgnoreCase);
                inCore = line.Equals("[core]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inCore) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            if (key.Equals("attributesfile", StringComparison.OrdinalIgnoreCase))
            {
                unsupported = true;
                continue;
            }

            if (!key.Equals("autocrlf", StringComparison.OrdinalIgnoreCase)) continue;
            var parsed = line[(separator + 1)..].Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => true,
                "false" or "no" or "0" => false,
                "input" => true,
                _ => (bool?)null
            };
            if (parsed is null) unsupported = true;
            else value = parsed;
        }
        return (value, unsupported);
    }

    private static bool HasUnsupportedConfigurationOverrides()
    {
        var count = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS"))
            || count is not null && !count.Equals("0", StringComparison.Ordinal);
    }

    private static bool HasUnsupportedGlobalAttributes(GitMetadataSnapshot snapshot)
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return true;

        var xdgRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return HasTransformAttributes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "etc", "gitattributes"), snapshot)
            || HasTransformAttributes(Path.Combine(string.IsNullOrWhiteSpace(xdgRoot) ? Path.Combine(home, ".config") : xdgRoot, "git", "attributes"), snapshot);
    }

    private static bool HasTransformAttributes(string path, GitMetadataSnapshot snapshot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) return true;
            var stream = TryOpenVerifiedFile(Path.GetFullPath(path));
            if (stream is null) return false;
            snapshot.Hold(stream);
            foreach (var rawLine in ReadBoundedUtf8(stream, "attributes").Split('\n'))
            {
                var fields = rawLine.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                for (var index = 1; index < fields.Length; index++)
                {
                    var attribute = fields[index].TrimStart('-', '!');
                    var equals = attribute.IndexOf('=');
                    if (equals >= 0) attribute = attribute[..equals];
                    if (attribute.Equals("text", StringComparison.OrdinalIgnoreCase)
                        || attribute.Equals("crlf", StringComparison.OrdinalIgnoreCase)
                        || attribute.Equals("eol", StringComparison.OrdinalIgnoreCase)
                        || attribute.Equals("filter", StringComparison.OrdinalIgnoreCase)
                        || attribute.Equals("working-tree-encoding", StringComparison.OrdinalIgnoreCase)
                        || attribute.Equals("ident", StringComparison.OrdinalIgnoreCase)
                        || attribute.Equals("binary", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is ApplicationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static bool? ReadGitBoolean(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => null
        };
    }

    private static void HoldDirectoryChain(string path, GitMetadataSnapshot snapshot)
    {
        var volumeRoot = Path.GetPathRoot(path) ?? throw new ApplicationException(ApplicationErrorCode.PathRejected, "Repository path has no volume root.");
        var current = volumeRoot;
        HoldDirectory(snapshot, current);
        var relative = Path.GetRelativePath(volumeRoot, path);
        foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            HoldDirectory(snapshot, current);
        }
    }

    private static void HoldRelativeDirectories(string baseDirectory, IEnumerable<string> segments, GitMetadataSnapshot snapshot)
    {
        var current = baseDirectory;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            HoldDirectory(snapshot, current);
        }
    }

    private static void HoldDirectory(GitMetadataSnapshot snapshot, string path, bool gitDirectory = false)
    {
        if (!snapshot.HasDirectory(path))
        {
            snapshot.HoldDirectory(path, OpenVerifiedDirectory(path, gitDirectory));
        }
    }

    private static bool MetadataEntryExists(string path)
    {
        var handle = TryOpenHandle(path, FileReadAttributes, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (handle is null)
        {
            return false;
        }

        using (handle)
        {
            VerifyHandle(handle, path, directory: null);
            return true;
        }
    }

    private static SafeFileHandle OpenVerifiedDirectory(string path, bool gitDirectory = false)
    {
        try
        {
            var handle = OpenHandle(path, FileReadAttributes | FileListDirectory, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
            try
            {
                VerifyHandle(handle, path, directory: true);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Win32Exception exception) when (gitDirectory && exception.NativeErrorCode is 2 or 3)
        {
            throw new ApplicationException(ApplicationErrorCode.NotGitRepository, "Git metadata is unavailable.", null, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git metadata could not be safely opened.", null, exception);
        }
    }

    private static FileStream OpenVerifiedFile(string path)
    {
        try
        {
            var handle = OpenHandle(path, GenericRead, FileFlagOpenReparsePoint);
            try
            {
                VerifyHandle(handle, path, directory: false);
                return new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git metadata could not be safely opened.", null, exception);
        }
    }

    private static SafeFileHandle OpenHandle(string path, uint access, uint flags)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Supported Git inspection requires Windows handles.");
        }

        var handle = CreateFile(path, access, (uint)FileShare.Read, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "Metadata handle could not be acquired.");
    }

    private static SafeFileHandle? TryOpenHandle(string path, uint access, uint flags)
    {
        try
        {
            return OpenHandle(path, access, flags);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or Win32Exception)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git metadata could not be safely inspected.", null, exception);
        }
    }

    private static void VerifyHandle(SafeFileHandle handle, string expectedPath, bool? directory)
    {
        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Metadata attributes could not be read.");
            }

            if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, "Repository paths containing reparse points are not supported.");
            }

            if (directory is { } expectedDirectory && ((information.FileAttributes & FileAttributes.Directory) != 0) != expectedDirectory)
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git metadata has an unsupported type.");
            }

            if (!string.Equals(NormalizeFinalPath(GetFinalPath(handle)), expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git metadata handle does not match the verified path.");
            }
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or PathTooLongException)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git metadata could not be safely verified.", null, exception);
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32 * 1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Metadata final path could not be read.");
        }

        return buffer.ToString();
    }

    private static string NormalizeFinalPath(string path)
    {
        const string extendedPrefix = "\\\\?\\";
        const string extendedUncPrefix = "\\\\?\\UNC\\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith(extendedPrefix, StringComparison.Ordinal))
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Git metadata has an unsupported final path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path[extendedPrefix.Length..]));
    }

    private static string NormalizeRoot(string path)
    {
        var fullPath = NormalizeCandidate(path);
        if (Directory.Exists(fullPath))
        {
            RejectReparsePoints(fullPath, fullPath);
        }

        return fullPath;
    }

    private static string NormalizeCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || path.IndexOf('\0') >= 0
            || path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Repository path must be an absolute local path without traversal.");
        }

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (fullPath.Length > StorageLimits.Identifier)
            {
                throw new ApplicationException(ApplicationErrorCode.ContentTooLarge, "Repository path exceeds its limit.");
            }

            return fullPath;
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Repository path is malformed.", null, exception);
        }
    }

    private static bool IsDescendant(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative is not "." and not ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool IsUnsafeGitConfigLine(string line)
    {
        var normalized = line.Trim().TrimStart('\uFEFF');
        return normalized.StartsWith("[include", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("[filter", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("path", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("worktree", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("excludesfile", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("attributesfile", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("fsmonitor", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("hookspath", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("sshcommand", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("askpass", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("worktreeconfig", StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparsePoints(string root, string path)
    {
        try
        {
            var volumeRoot = Path.GetPathRoot(root) ?? throw new ArgumentException("Path has no volume root.", nameof(root));
            EnsureNotReparsePoint(volumeRoot);
            var relative = Path.GetRelativePath(volumeRoot, path);
            if (relative == ".")
            {
                return;
            }

            var current = volumeRoot;
            foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                EnsureNotReparsePoint(current);
            }
        }
        catch (ApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Repository path could not be safely inspected.", null, exception);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ApplicationException(ApplicationErrorCode.PathRejected, "Repository paths containing reparse points are not supported.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

public sealed record ArtifactFileValidation(string Reference, long SizeBytes, string ContentHash);

public static class ArtifactFileBoundary
{
    public static ArtifactFileValidation Validate(string artifactRoot, string reference, int maximumBytes) =>
        RepositoryBoundary.ValidateArtifactFile(artifactRoot, reference, maximumBytes);
}

public static class RepositoryDocumentBoundary
{
    public static ArtifactFileValidation Validate(string repositoryRoot, string reference, int maximumBytes) =>
        RepositoryBoundary.ValidateRepositoryDocument(repositoryRoot, reference, maximumBytes);
}

internal sealed class GitMetadataSnapshot(string canonicalPath) : IDisposable
{
    private readonly List<IDisposable> _handles = [];
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public string CanonicalPath { get; } = canonicalPath;
    public string GitDirectory => Path.Combine(CanonicalPath, ".git");
    public string? Branch { get; private set; }
    public string? OriginUrl { get; private set; }
    public string HeadCommitSha { get; private set; } = string.Empty;

    internal void Hold(IDisposable handle) => _handles.Add(handle);
    internal bool HasDirectory(string path) => _directories.Contains(path);
    internal void HoldDirectory(string path, SafeFileHandle handle)
    {
        if (_directories.Add(path))
        {
            _handles.Add(handle);
        }
        else
        {
            handle.Dispose();
        }
    }
    internal bool? Autocrlf { get; private set; }
    internal bool HasUnsupportedWorkingTreeConfiguration { get; private set; }
    internal void Set(string? branch, string commit, string? originUrl, bool? autocrlf, bool hasUnsupportedWorkingTreeConfiguration) =>
        (Branch, HeadCommitSha, OriginUrl, Autocrlf, HasUnsupportedWorkingTreeConfiguration) = (branch, commit, originUrl, autocrlf, hasUnsupportedWorkingTreeConfiguration);

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--)
        {
            _handles[index].Dispose();
        }

        _handles.Clear();
        _directories.Clear();
    }
}
