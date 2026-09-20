using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AIContextMCP.Core;

internal sealed partial class RepositoryBoundary
{
    private const int MaximumIndexEntries = 512;
    private const int MaximumWorktreeEntries = 512;
    private const int MaximumWorktreeDirectories = 512;
    private const int MaximumIndexBytes = 1024 * 1024;
    private const int MaximumLooseObjectBytes = 1024 * 1024;
    private const int MaximumWorktreeFileBytes = 1024 * 1024;
    private const long MaximumWorktreeBytes = 8L * 1024 * 1024;
    private const uint FileNotifyChangeFileName = 0x00000001;
    private const uint FileNotifyChangeDirectoryName = 0x00000002;
    private const uint WaitTimeout = 0x00000102;

    internal GitWorkingTreeSnapshot ObserveWorkingTree(GitMetadataSnapshot snapshot)
        => ObserveWorkingTree(snapshot, null);

    internal GitWorkingTreeSnapshot ObserveWorkingTree(GitMetadataSnapshot snapshot, Action? afterWatchStarted)
    {
        try
        {
            using var notification = OpenWorktreeChangeNotification(snapshot.CanonicalPath);
            afterWatchStarted?.Invoke();
            var index = TryOpenVerifiedFile(Path.Combine(snapshot.GitDirectory, "index"))
                ?? throw new UnsupportedWorkingTreeException();
            snapshot.Hold(index);
            var entries = ReadIndex(ReadBoundedBytes(index, MaximumIndexBytes));
            if (snapshot.HasUnsupportedWorkingTreeConfiguration
                || MetadataEntryExists(Path.Combine(snapshot.GitDirectory, "info", "attributes")))
            {
                throw new UnsupportedWorkingTreeException();
            }
            var rootAttributesPath = Path.Combine(snapshot.CanonicalPath, ".gitattributes");
            var hasRootAttributes = MetadataEntryExists(rootAttributesPath);
            if (hasRootAttributes)
            {
                FileStream rootAttributes;
                try
                {
                    rootAttributes = TryOpenVerifiedFile(rootAttributesPath) ?? throw new UnsupportedWorkingTreeException();
                }
                catch (ApplicationException)
                {
                    throw new UnsupportedWorkingTreeException();
                }
                snapshot.Hold(rootAttributes);
                if (!SupportsLfOnlyRootAttributes(rootAttributes)) throw new UnsupportedWorkingTreeException();
            }
            if (entries.Any(entry => IsSensitivePath(entry.Path)))
            {
                throw new UnsupportedWorkingTreeException();
            }

            var headTree = ReadCommitTree(snapshot);
            var indexTree = BuildIndexTree(entries);
            var indexed = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!indexed.TryAdd(entry.Path, entry))
                {
                    throw new UnsupportedWorkingTreeException();
                }
            }

            var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changes = new List<WorktreeChange>();
            var contentBytes = 0L;
            var files = 0;
            var directories = 0;
            EnumerateWorktree(snapshot.CanonicalPath, string.Empty, snapshot, indexed, observed, changes, snapshot.Autocrlf is true && !hasRootAttributes, hasRootAttributes, ref contentBytes, ref files, ref directories);
            foreach (var entry in entries)
            {
                if (!observed.Contains(entry.Path))
                {
                    changes.Add(new WorktreeChange(entry.Path, "deleted", null));
                }
            }

            if (WaitForSingleObject(notification, 0) != WaitTimeout)
            {
                throw new UnsupportedWorkingTreeException();
            }

            var fingerprint = ComputeFingerprint(snapshot.HeadCommitSha, headTree, indexTree, entries, changes);
            return new GitWorkingTreeSnapshot(
                !string.Equals(headTree, indexTree, StringComparison.OrdinalIgnoreCase) || changes.Count != 0
                    ? WorkingTreeState.Dirty
                    : WorkingTreeState.Clean,
                fingerprint);
        }
        catch (UnsupportedWorkingTreeException)
        {
            return new GitWorkingTreeSnapshot(WorkingTreeState.Unknown, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or FormatException or OverflowException)
        {
            return new GitWorkingTreeSnapshot(WorkingTreeState.Unknown, null);
        }
    }

    private static IReadOnlyList<IndexEntry> ReadIndex(byte[] bytes)
    {
        if (bytes.Length < 32
            || bytes[0] != (byte)'D'
            || bytes[1] != (byte)'I'
            || bytes[2] != (byte)'R'
            || bytes[3] != (byte)'C'
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)) != 2)
        {
            throw new UnsupportedWorkingTreeException();
        }

        if (!CryptographicOperations.FixedTimeEquals(
                SHA1.HashData(bytes.AsSpan(0, bytes.Length - 20)),
                bytes.AsSpan(bytes.Length - 20, 20)))
        {
            throw new UnsupportedWorkingTreeException();
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
        if (count > MaximumIndexEntries)
        {
            throw new UnsupportedWorkingTreeException();
        }

        var entries = new List<IndexEntry>((int)count);
        var trailer = bytes.Length - 20;
        var offset = 12;
        string? previous = null;
        for (var item = 0; item < count; item++)
        {
            var start = offset;
            if (start > trailer - 62)
            {
                throw new UnsupportedWorkingTreeException();
            }

            var mode = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start + 24, 4));
            var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(start + 60, 2));
            if (flags >> 12 != 0 || mode is not 0x000081A4 and not 0x000081ED)
            {
                throw new UnsupportedWorkingTreeException();
            }

            var nameStart = start + 62;
            var nameEnd = nameStart;
            while (nameEnd < trailer && bytes[nameEnd] != 0)
            {
                nameEnd++;
            }

            if (nameEnd == trailer)
            {
                throw new UnsupportedWorkingTreeException();
            }

            var nameLength = nameEnd - nameStart;
            if ((flags & 0x0FFF) != 0x0FFF && (flags & 0x0FFF) != nameLength)
            {
                throw new UnsupportedWorkingTreeException();
            }

            var path = ReadIndexPath(bytes.AsSpan(nameStart, nameLength));
            if (previous is not null && string.CompareOrdinal(previous, path) >= 0)
            {
                throw new UnsupportedWorkingTreeException();
            }

            var length = nameEnd + 1 - start;
            var padded = checked((length + 7) & ~7);
            offset = checked(start + padded);
            if (offset > trailer)
            {
                throw new UnsupportedWorkingTreeException();
            }

            entries.Add(new IndexEntry(path, mode, bytes.AsSpan(start + 40, 20).ToArray()));
            previous = path;
        }

        while (offset < trailer)
        {
            if (offset > trailer - 8 || bytes[offset] is < (byte)'A' or > (byte)'Z')
            {
                throw new UnsupportedWorkingTreeException();
            }

            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4, 4));
            if (size > trailer - offset - 8)
            {
                throw new UnsupportedWorkingTreeException();
            }

            offset = checked(offset + 8 + (int)size);
        }

        if (offset != trailer)
        {
            throw new UnsupportedWorkingTreeException();
        }

        return entries;
    }

    private static string ReadIndexPath(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > StorageLimits.Identifier)
        {
            throw new UnsupportedWorkingTreeException();
        }

        var characters = new char[bytes.Length];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] is < 0x20 or > 0x7E)
            {
                throw new UnsupportedWorkingTreeException();
            }

            characters[index] = (char)bytes[index];
        }

        var path = new string(characters);
        return IsSupportedRelativePath(path) ? path : throw new UnsupportedWorkingTreeException();
    }

    private static string ReadCommitTree(GitMetadataSnapshot snapshot)
    {
        var commit = ReadLooseObject(snapshot, snapshot.HeadCommitSha, "commit");
        var lineEnd = Array.IndexOf(commit, (byte)'\n');
        if (lineEnd != 45
            || commit[0] != (byte)'t'
            || commit[1] != (byte)'r'
            || commit[2] != (byte)'e'
            || commit[3] != (byte)'e'
            || commit[4] != (byte)' ')
        {
            throw new UnsupportedWorkingTreeException();
        }

        var tree = Encoding.ASCII.GetString(commit, 5, 40);
        return IsCommitSha(tree) ? tree.ToLowerInvariant() : throw new UnsupportedWorkingTreeException();
    }

    private static byte[] ReadLooseObject(GitMetadataSnapshot snapshot, string objectId, string expectedType)
    {
        if (!IsCommitSha(objectId))
        {
            throw new UnsupportedWorkingTreeException();
        }

        var objects = Path.Combine(snapshot.GitDirectory, "objects");
        if (!TryHoldDirectory(snapshot, objects))
        {
            throw new UnsupportedWorkingTreeException();
        }

        var shard = Path.Combine(objects, objectId[..2]);
        if (!TryHoldDirectory(snapshot, shard))
        {
            throw new UnsupportedWorkingTreeException();
        }

        var file = TryOpenVerifiedFile(Path.Combine(shard, objectId[2..]))
            ?? throw new UnsupportedWorkingTreeException();
        snapshot.Hold(file);
        var compressed = ReadBoundedBytes(file, MaximumLooseObjectBytes);
        using var compressedStream = new MemoryStream(compressed, writable: false);
        using var decompressor = new ZLibStream(compressedStream, CompressionMode.Decompress);
        using var uncompressed = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = decompressor.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (uncompressed.Length > MaximumLooseObjectBytes - read)
            {
                throw new UnsupportedWorkingTreeException();
            }

            uncompressed.Write(buffer, 0, read);
        }

        var raw = uncompressed.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(raw), Convert.FromHexString(objectId)))
        {
            throw new UnsupportedWorkingTreeException();
        }

        var separator = Array.IndexOf(raw, (byte)0);
        if (separator <= expectedType.Length
            || !Encoding.ASCII.GetString(raw, 0, separator).StartsWith($"{expectedType} ", StringComparison.Ordinal)
            || !int.TryParse(Encoding.ASCII.GetString(raw, expectedType.Length + 1, separator - expectedType.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
            || declared != raw.Length - separator - 1)
        {
            throw new UnsupportedWorkingTreeException();
        }

        return raw[(separator + 1)..];
    }

    private static string BuildIndexTree(IReadOnlyList<IndexEntry> entries)
    {
        var root = new TreeNode();
        foreach (var entry in entries)
        {
            var node = root;
            var segments = entry.Path.Split('/');
            foreach (var segment in segments[..^1])
            {
                if (node.Files.ContainsKey(segment))
                {
                    throw new UnsupportedWorkingTreeException();
                }

                if (!node.Directories.TryGetValue(segment, out var child))
                {
                    child = new TreeNode();
                    node.Directories.Add(segment, child);
                }

                node = child;
            }

            var name = segments[^1];
            if (node.Directories.ContainsKey(name) || !node.Files.TryAdd(name, entry))
            {
                throw new UnsupportedWorkingTreeException();
            }
        }

        return Convert.ToHexString(ComputeTreeObject(root)).ToLowerInvariant();
    }

    private static byte[] ComputeTreeObject(TreeNode node)
    {
        var members = new List<TreeMember>(node.Files.Count + node.Directories.Count);
        foreach (var file in node.Files)
        {
            members.Add(new TreeMember(file.Key, file.Value.Mode == 0x000081A4 ? "100644" : "100755", file.Value.ObjectId, false));
        }

        foreach (var directory in node.Directories)
        {
            members.Add(new TreeMember(directory.Key, "40000", ComputeTreeObject(directory.Value), true));
        }

        members.Sort(static (left, right) => string.CompareOrdinal(left.Directory ? $"{left.Name}/" : left.Name, right.Directory ? $"{right.Name}/" : right.Name));
        using var content = new MemoryStream();
        foreach (var member in members)
        {
            content.Write(Encoding.ASCII.GetBytes(member.Mode));
            content.WriteByte((byte)' ');
            content.Write(Encoding.ASCII.GetBytes(member.Name));
            content.WriteByte(0);
            content.Write(member.ObjectId);
        }

        if (content.Length > MaximumIndexBytes)
        {
            throw new UnsupportedWorkingTreeException();
        }

        var header = Encoding.ASCII.GetBytes($"tree {content.Length}\0");
        var raw = new byte[checked(header.Length + content.Length)];
        header.CopyTo(raw, 0);
        content.GetBuffer().AsSpan(0, checked((int)content.Length)).CopyTo(raw.AsSpan(header.Length));
        return SHA1.HashData(raw);
    }

    private static void EnumerateWorktree(
        string directory,
        string relativeDirectory,
        GitMetadataSnapshot snapshot,
        IReadOnlyDictionary<string, IndexEntry> indexed,
        ISet<string> observed,
        ICollection<WorktreeChange> changes,
        bool normalizeLineEndings,
        bool requireLf,
        ref long contentBytes,
        ref int files,
        ref int directories)
    {
        HoldWorktreeDirectory(snapshot, directory, ref directories);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(path);
            if (relativeDirectory.Length == 0 && name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsSupportedPathSegment(name))
            {
                throw new UnsupportedWorkingTreeException();
            }

            var relativePath = relativeDirectory.Length == 0 ? name : $"{relativeDirectory}/{name}";
            if (name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) && relativeDirectory.Length != 0)
            {
                throw new UnsupportedWorkingTreeException();
            }

            if (IsSensitivePath(relativePath))
            {
                throw new UnsupportedWorkingTreeException();
            }

            if (Directory.Exists(path))
            {
                EnumerateWorktree(path, relativePath, snapshot, indexed, observed, changes, normalizeLineEndings, requireLf, ref contentBytes, ref files, ref directories);
                continue;
            }

            if (++files > MaximumWorktreeEntries)
            {
                throw new UnsupportedWorkingTreeException();
            }

            var file = TryOpenVerifiedFile(path) ?? throw new UnsupportedWorkingTreeException();
            snapshot.Hold(file);
            var hashes = HashWorkingFile(file, normalizeLineEndings, requireLf, ref contentBytes);
            if (indexed.TryGetValue(relativePath, out var entry))
            {
                observed.Add(entry.Path);
                if (!MatchesIndexBlob(hashes, entry))
                {
                    changes.Add(new WorktreeChange(entry.Path, "modified", hashes.ContentHash));
                }
            }
            else
            {
                changes.Add(new WorktreeChange(relativePath, "untracked", hashes.ContentHash));
            }
        }
    }

    private static ChangeNotificationHandle OpenWorktreeChangeNotification(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is null || new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                throw new UnsupportedWorkingTreeException();
            }

            var handle = FindFirstChangeNotification(path, true, FileNotifyChangeFileName | FileNotifyChangeDirectoryName);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new UnsupportedWorkingTreeException();
            }

            return handle;
        }
        catch (UnsupportedWorkingTreeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new UnsupportedWorkingTreeException();
        }
    }

    private static void HoldWorktreeDirectory(GitMetadataSnapshot snapshot, string path, ref int directories)
    {
        if (!snapshot.HasDirectory(path) && ++directories > MaximumWorktreeDirectories)
        {
            throw new UnsupportedWorkingTreeException();
        }

        HoldDirectory(snapshot, path);
    }

    private static bool TryHoldDirectory(GitMetadataSnapshot snapshot, string path)
    {
        if (snapshot.HasDirectory(path))
        {
            return true;
        }

        var handle = TryOpenHandle(path, FileReadAttributes | FileListDirectory, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (handle is null)
        {
            return false;
        }

        try
        {
            VerifyHandle(handle, path, directory: true);
            snapshot.HoldDirectory(path, handle);
            return true;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool MatchesIndexBlob(FileHashes hashes, IndexEntry entry)
    {
        if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hashes.RawBlobId), entry.ObjectId)) return true;
        if (hashes.LineEndingsAmbiguous) throw new UnsupportedWorkingTreeException();
        if (hashes.NormalizedBlobId is null
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hashes.NormalizedBlobId), entry.ObjectId)) return false;

        return true;
    }

    private static FileHashes HashWorkingFile(FileStream stream, bool normalizeLineEndings, bool requireLf, ref long aggregateBytes)
    {
        var length = stream.Length;
        if (length > MaximumWorktreeFileBytes || aggregateBytes > MaximumWorktreeBytes - length)
        {
            throw new UnsupportedWorkingTreeException();
        }

        aggregateBytes += length;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[checked((int)length)];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        if (requireLf && bytes.Contains((byte)'\r')) throw new UnsupportedWorkingTreeException();
        content.AppendData(bytes);

        if (stream.Length != length)
        {
            throw new UnsupportedWorkingTreeException();
        }

        var normalized = normalizeLineEndings ? NormalizeLineEndings(bytes) : (Content: (byte[]?)null, Ambiguous: false);
        return new FileHashes(
            HashBlob(bytes),
            normalized.Content is null ? null : HashBlob(normalized.Content),
            Convert.ToHexString(content.GetHashAndReset()).ToLowerInvariant(),
            normalized.Ambiguous);
    }

    private static string HashBlob(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var blob = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        blob.AppendData(header);
        blob.AppendData(content);
        return Convert.ToHexString(blob.GetHashAndReset()).ToLowerInvariant();
    }

    private static (byte[]? Content, bool Ambiguous) NormalizeLineEndings(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return default;
        var normalized = new byte[bytes.Length];
        var count = 0;
        var lineEndings = false;
        var ambiguous = false;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == '\r')
            {
                if (index + 1 == bytes.Length || bytes[index + 1] != '\n') ambiguous = true;
                lineEndings = true;
                continue;
            }

            var value = bytes[index];
            if (value != '\t' && value != '\n' && (value < ' ' || value > '~')) ambiguous = true;
            normalized[count++] = bytes[index];
        }
        return lineEndings && !ambiguous ? (normalized[..count], false) : (null, lineEndings && ambiguous);
    }

    // ponytail: LF-only attributes avoid per-pattern normalization; add it when a supported transform is required.
    private static bool SupportsLfOnlyRootAttributes(FileStream attributes)
    {
        var bytes = ReadBoundedBytes(attributes, StorageLimits.Content);
        if (bytes.Length == 0 || bytes.Contains((byte)'\r')) return false;
        string content;
        try
        {
            content = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var lines = content.Split('\n');
        var count = content[^1] == '\n' ? lines.Length - 1 : lines.Length;
        if (count == 0) return false;
        for (var index = 0; index < count; index++)
        {
            var fields = lines[index].Split(' ');
            if (lines[index].Length == 0
                || lines[index] != lines[index].Trim()
                || fields.Length is < 2 or > 3
                || !IsLfOnlyAttributesPattern(fields[0])
                || fields[1] is not ("text" or "text=auto")
                || fields.Length == 3 && fields[2] != "eol=lf") return false;
        }

        return true;
    }

    private static bool IsLfOnlyAttributesPattern(string value) =>
        value == "*"
        || (value.Length > 2
            && value[0] == '*'
            && value[1] == '.'
            && value[2..].All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'));

    private static string ComputeFingerprint(
        string head,
        string headTree,
        string indexTree,
        IEnumerable<IndexEntry> entries,
        IEnumerable<WorktreeChange> changes)
    {
        var value = new StringBuilder()
            .Append("v1\0head\0").Append(head)
            .Append("\0head-tree\0").Append(headTree)
            .Append("\0index-tree\0").Append(indexTree);
        foreach (var entry in entries)
        {
            value.Append("\0index\0").Append(entry.Path).Append('\0').Append(entry.Mode).Append('\0').Append(Convert.ToHexString(entry.ObjectId).ToLowerInvariant());
        }

        foreach (var change in changes.OrderBy(change => change.Path, StringComparer.Ordinal).ThenBy(change => change.Status, StringComparer.Ordinal))
        {
            value.Append("\0worktree\0").Append(change.Path).Append('\0').Append(change.Status).Append('\0').Append(change.ContentHash);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant();
    }

    private static byte[] ReadBoundedBytes(FileStream stream, int maximum)
    {
        var length = stream.Length;
        if (length > maximum)
        {
            throw new UnsupportedWorkingTreeException();
        }

        var bytes = new byte[checked((int)length)];
        stream.Position = 0;
        stream.ReadExactly(bytes);

        if (stream.Length != length)
        {
            throw new UnsupportedWorkingTreeException();
        }

        return bytes;
    }

    private static FileStream? TryOpenVerifiedFile(string path)
    {
        var handle = TryOpenHandle(path, GenericRead, FileFlagOpenReparsePoint);
        if (handle is null)
        {
            return null;
        }

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

    private static bool IsSupportedRelativePath(string path) =>
        path.Split('/').All(IsSupportedPathSegment);

    private static bool IsSupportedPathSegment(string value)
    {
        if (value.Length == 0
            || value is "." or ".."
            || value.EndsWith('.') || value.EndsWith(' ')
            || value.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || IsReservedName(value))
        {
            return false;
        }

        return value.All(character => character is >= '!' and <= '~' && character is not '<' and not '>' and not ':' and not '"' and not '/' and not '\\' and not '|' and not '?' and not '*');
    }

    private static bool IsReservedName(string value)
    {
        var baseName = value.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (baseName.Length == 4
                && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && baseName[3] is >= '1' and <= '9');
    }

    private static bool IsSensitivePath(string path)
    {
        var segments = path.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            if (IsSensitiveSegment(segments[index], index == segments.Length - 1)) return true;
        }

        return false;
    }

    private static bool IsSensitiveSegment(string segment, bool leaf) =>
        segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
        || ((!leaf || !IsSourceFile(segment)) && (segment.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("password", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("token", StringComparison.OrdinalIgnoreCase)))
        || segment.Equals("id_rsa", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("id_dsa", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("id_ecdsa", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase)
        || segment.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
        || segment.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
        || segment.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
        || segment.EndsWith(".p12", StringComparison.OrdinalIgnoreCase);

    private static bool IsSourceFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".c" or ".cc" or ".cpp" or ".cs" or ".fs" or ".go" or ".h" or ".hpp" or ".java" or ".js" or ".jsx" or ".kt" or ".php" or ".ps1" or ".psm1" or ".py" or ".rb" or ".rs" or ".sql" or ".swift" or ".ts" or ".tsx" or ".vb" or ".sh" or ".bash" or ".zsh" or ".bat" or ".cmd";

    private sealed class UnsupportedWorkingTreeException : Exception;
    private sealed class TreeNode
    {
        public Dictionary<string, TreeNode> Directories { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IndexEntry> Files { get; } = new(StringComparer.Ordinal);
    }

    private sealed record IndexEntry(string Path, uint Mode, byte[] ObjectId);
    private sealed record TreeMember(string Name, string Mode, byte[] ObjectId, bool Directory);
    private sealed record WorktreeChange(string Path, string Status, string? ContentHash);
    private sealed record FileHashes(string RawBlobId, string? NormalizedBlobId, string ContentHash, bool LineEndingsAmbiguous);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ChangeNotificationHandle FindFirstChangeNotification(string path, bool watchSubtree, uint notifyFilter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(ChangeNotificationHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindCloseChangeNotification(IntPtr handle);

    private sealed class ChangeNotificationHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ChangeNotificationHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle() => FindCloseChangeNotification(handle);
    }
}

internal sealed record GitWorkingTreeSnapshot(WorkingTreeState State, string? Fingerprint);
