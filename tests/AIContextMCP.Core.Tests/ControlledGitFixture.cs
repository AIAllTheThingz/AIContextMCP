using System.Diagnostics;

namespace AIContextMCP.Core.Tests;

internal sealed class ControlledGitFixture : IDisposable
{
    private static readonly string ApprovedBase = @"D:\Projects\.AIContextMCP-Test";
    private readonly string _root;
    private bool _disposed;

    public string RepositoryPath { get; }
    public string GitExecutable { get; }

    public ControlledGitFixture()
    {
        Directory.CreateDirectory(ApprovedBase);
        _root = Path.Combine(ApprovedBase, Guid.NewGuid().ToString("N"));
        RepositoryPath = Path.Combine(_root, "repo");
        GitExecutable = Environment.GetEnvironmentVariable("AIContextMCP_GIT") ?? @"C:\Program Files\Git\cmd\git.exe";
        try
        {
            if (!File.Exists(GitExecutable)) throw new InvalidOperationException("Git executable is unavailable: " + GitExecutable);
            Directory.CreateDirectory(RepositoryPath);
            Run("init");
            File.WriteAllText(Path.Combine(RepositoryPath, "README.md"), "fixture\n");
            Run("add", ".");
            Run("-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "initial");
        }
        catch (Exception exception)
        {
            try
            {
                DeleteExactRoot(_root, ApprovedBase);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException($"Fixture setup failed and cleanup failed for {_root}.", exception, cleanupException);
            }

            throw;
        }
    }

    public string Run(params string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private async Task<string> RunAsync(string[] args)
    {
        var start = new ProcessStartInfo(GitExecutable)
        {
            WorkingDirectory = RepositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        var result = await RunProcessAsync(start, TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git failed in {RepositoryPath}: {result.StandardError}");
        }

        return result.StandardOutput.Trim();
    }

    private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Process did not start: {start.FileName}");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var complete = Task.WhenAll(process.WaitForExitAsync(), output, error);
        if (await Task.WhenAny(complete, Task.Delay(timeout)) != complete)
        {
            Kill(process);
            await Task.WhenAny(complete, Task.Delay(TimeSpan.FromSeconds(2)));
            throw new TimeoutException($"Process timed out after {timeout.TotalSeconds:0} seconds: {start.FileName}");
        }

        await complete;
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void DeleteExactRoot(string path, string basePath)
    {
        var fullPath = Path.GetFullPath(path);
        var fullBase = Path.GetFullPath(basePath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Refusing cleanup outside exact owned root: {fullPath}");
        }

        if (Directory.Exists(fullPath))
        {
            ClearReadOnlyFiles(fullPath);
            Directory.Delete(fullPath, recursive: true);
        }

        if (Directory.Exists(fullPath))
        {
            throw new IOException($"Owned test root remains after cleanup: {fullPath}");
        }
    }

    private static void ClearReadOnlyFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Refusing to follow reparse point during owned cleanup: {entry}");
            }

            if (Directory.Exists(entry))
            {
                ClearReadOnlyFiles(entry);
            }
            else if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = new List<Exception>();
        try
        {
            DeleteExactRoot(_root, ApprovedBase);
        }
        catch (Exception exception)
        {
            failures.Add(new IOException($"Fixture cleanup failed for {_root}.", exception));
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
