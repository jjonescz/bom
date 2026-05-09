#!/usr/bin/env dotnet
#:property Version=0.1.0
#:property Authors=Jan Jones
#:property Description=Manage BOM changes from the current pull request or working tree.
#:property PackageReadmeFile=README.md
#:property PackageOutputPath=./artifacts/nupkg
#:property PackageLicenseExpression=MIT
#:property RepositoryUrl=https://github.com/jjonescz/bom
#:property PackageProjectUrl=$(RepositoryUrl)
#:property RepositoryType=git
#:property Copyright=© Jan Jones

#:package System.CommandLine@2.0.0

#:package FileBasedApps@1.0.1
#:property FileBasedAppsIncludeReadme=true

using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

return await BomTool.RunAsync(args);

static class BomTool
{
    public static Task<int> RunAsync(string[] arguments)
    {
        RootCommand rootCommand = CreateRootCommand();
        return rootCommand.Parse(arguments).InvokeAsync();
    }

    static RootCommand CreateRootCommand()
    {
        Argument<string> resetTargetArgument = CreateTargetArgument("reset", "pr, worktree");
        Argument<string> checkTargetArgument = CreateTargetArgument("check", "pr, worktree");

        Command checkCommand = new("check", "Report BOM changes and fail if any are found.")
        {
            checkTargetArgument,
        };
        checkCommand.SetAction((ParseResult parseResult, CancellationToken _) => RunTargetCommandAsync(
            parseResult.GetValue(checkTargetArgument),
            ResetMode.Check));

        Command resetCommand = new("reset", "Reset BOM changes.")
        {
            resetTargetArgument,
        };
        resetCommand.SetAction((ParseResult parseResult, CancellationToken _) => RunTargetCommandAsync(
            parseResult.GetValue(resetTargetArgument),
            ResetMode.Reset));

        RootCommand rootCommand = new("Manage BOM changes.");
        rootCommand.Subcommands.Add(checkCommand);
        rootCommand.Subcommands.Add(resetCommand);

        return rootCommand;
    }

    static Argument<string> CreateTargetArgument(string commandName, string supportedValues)
    {
        return new Argument<string>("target")
        {
            Description = $"What to {commandName}. Supported values: {supportedValues}.",
        };
    }

    static async Task<int> RunTargetCommandAsync(string? target, ResetMode mode)
    {
        try
        {
            return target switch
            {
                "pr" => await ResetCurrentPrAsync(mode),
                "worktree" when mode == ResetMode.Check => await CheckCurrentWorktreeAsync(),
                "worktree" => await ResetCurrentWorktreeAsync(),
                _ => UnsupportedTarget(target, "pr, worktree"),
            };
        }
        catch (ToolFailureException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
    }

    static int UnsupportedTarget(string? target, string supportedValues)
    {
        Console.Error.WriteLine($"Unsupported target '{target}'. Supported values: {supportedValues}.");
        return 2;
    }

    static async Task<int> CheckCurrentWorktreeAsync()
    {
        string repositoryRoot = await GetRepositoryRootAsync();
        string currentPrefix = GetCurrentDirectoryPrefix(repositoryRoot);
        List<WorktreeChange> bomChanges = await GetCurrentWorktreeBomChangesAsync(repositoryRoot, currentPrefix);

        if (bomChanges.Count == 0)
        {
            Console.WriteLine($"No BOM changes found in the working tree under {FormatScope(currentPrefix)}.");
            return 0;
        }

        foreach (WorktreeChange change in bomChanges)
        {
            Console.WriteLine(change.ToDisplayText());
        }

        Console.Error.WriteLine($"Found {bomChanges.Count} working tree BOM change(s) under {FormatScope(currentPrefix)}.");
        return 1;
    }

    static async Task<int> ResetCurrentWorktreeAsync()
    {
        string repositoryRoot = await GetRepositoryRootAsync();
        string currentPrefix = GetCurrentDirectoryPrefix(repositoryRoot);
        List<WorktreeChange> bomChanges = await GetCurrentWorktreeBomChangesAsync(repositoryRoot, currentPrefix);

        if (bomChanges.Count == 0)
        {
            Console.WriteLine($"No BOM changes found in the working tree under {FormatScope(currentPrefix)}.");
            return 0;
        }

        foreach (WorktreeChange change in bomChanges)
        {
            bool expectedHasBom = HasUtf8Bom(await GetGitFileBytesAsync("HEAD", change.Path));
            bool shouldResetIndex = await IsIndexBomOnlyChangeAsync(change);
            await SetWorktreeBomAsync(repositoryRoot, change.Path, expectedHasBom);

            if (shouldResetIndex)
            {
                await RunRequiredAsync("git", ["restore", "--source", "HEAD", "--staged", "--", change.Path]);
            }

            Console.WriteLine($"reset {change.Path}");
        }

        Console.WriteLine($"Reset {bomChanges.Count} working tree BOM change(s) under {FormatScope(currentPrefix)}.");
        return 0;
    }

    static async Task<int> ResetCurrentPrAsync(ResetMode mode)
    {
        string repositoryRoot = await GetRepositoryRootAsync();
        string currentPrefix = GetCurrentDirectoryPrefix(repositoryRoot);
        PullRequestInfo pullRequest = await GetCurrentPullRequestAsync();
        string baseCommit = await EnsureBaseCommitAvailableAsync(pullRequest);
        IReadOnlyList<ChangeEntry> changes = await GetPullRequestChangesAsync(baseCommit);
        List<ResetOperation> operations = await BuildBomResetOperationsAsync(changes, currentPrefix, baseCommit);

        if (operations.Count == 0)
        {
            Console.WriteLine($"No BOM changes found under {FormatScope(currentPrefix)} for PR #{pullRequest.Number}.");
            return 0;
        }

        if (mode == ResetMode.Check)
        {
            foreach (ResetOperation operation in operations)
            {
                Console.WriteLine($"{operation.Kind.ToCheckDisplayText()} {operation.Path}");
            }

            Console.Error.WriteLine($"Found {operations.Count} BOM change(s) under {FormatScope(currentPrefix)} from PR #{pullRequest.Number} ({pullRequest.Url}).");
            return 1;
        }

        foreach (ResetOperation operation in operations)
        {
            await SetWorktreeBomAsync(repositoryRoot, operation.Path, operation.ExpectedHasBom);
            Console.WriteLine($"reset {operation.Path}");
        }

        Console.WriteLine($"Reset {operations.Count} path(s) under {FormatScope(currentPrefix)} from PR #{pullRequest.Number} ({pullRequest.Url}).");
        return 0;
    }

    static async Task<string> GetRepositoryRootAsync()
    {
        CommandResult result = await RunRequiredAsync("git", ["rev-parse", "--show-toplevel"]);
        return Path.GetFullPath(result.StdOut.Trim());
    }

    static string GetCurrentDirectoryPrefix(string repositoryRoot)
    {
        string normalizedRoot = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!currentDirectory.Equals(normalizedRoot, comparison) && !currentDirectory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new ToolFailureException($"The current directory is not inside the git repository root '{normalizedRoot}'.", 1);
        }

        string relativePath = Path.GetRelativePath(normalizedRoot, currentDirectory);
        if (relativePath == ".")
        {
            return string.Empty;
        }

        return NormalizeGitPath(relativePath).TrimEnd('/') + "/";
    }

    static async Task<PullRequestInfo> GetCurrentPullRequestAsync()
    {
        CommandResult result = await RunRequiredAsync("gh", ["pr", "view", "--json", "number,baseRefName,baseRefOid,url"]);

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StdOut);
            JsonElement root = document.RootElement;
            int number = root.GetProperty("number").GetInt32();
            string baseRefName = GetRequiredString(root, "baseRefName");
            string baseRefOid = GetRequiredString(root, "baseRefOid");
            string url = GetRequiredString(root, "url");

            return new PullRequestInfo(number, baseRefName, baseRefOid, url);
        }
        catch (JsonException ex)
        {
            throw new ToolFailureException($"Failed to parse GitHub CLI PR details: {ex.Message}", 1);
        }
        catch (KeyNotFoundException ex)
        {
            throw new ToolFailureException($"GitHub CLI PR details did not include an expected field: {ex.Message}", 1);
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolFailureException($"GitHub CLI PR details had an unexpected shape: {ex.Message}", 1);
        }
    }

    static async Task<string> EnsureBaseCommitAvailableAsync(PullRequestInfo pullRequest)
    {
        if (await GitObjectExistsAsync(pullRequest.BaseRefOid))
        {
            return pullRequest.BaseRefOid;
        }

        string remote = await GetPreferredRemoteAsync();
        await RunRequiredAsync("git", ["fetch", "--quiet", remote, pullRequest.BaseRefName]);

        if (await GitObjectExistsAsync(pullRequest.BaseRefOid))
        {
            return pullRequest.BaseRefOid;
        }

        if (await GitObjectExistsAsync("FETCH_HEAD"))
        {
            return "FETCH_HEAD";
        }

        throw new ToolFailureException($"Could not find or fetch the PR base commit '{pullRequest.BaseRefOid}'.", 1);
    }

    static async Task<bool> GitObjectExistsAsync(string revision)
    {
        CommandResult result = await RunAsync("git", ["cat-file", "-e", $"{revision}^{{commit}}"]);
        return result.ExitCode == 0;
    }

    static async Task<string> GetPreferredRemoteAsync()
    {
        CommandResult result = await RunRequiredAsync("git", ["remote"]);
        string[] remotes = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (remotes.Length == 0)
        {
            throw new ToolFailureException("The PR base commit is not available locally and this repository has no git remotes to fetch from.", 1);
        }

        return remotes.Contains("origin", StringComparer.Ordinal) ? "origin" : remotes[0];
    }

    static async Task<IReadOnlyList<ChangeEntry>> GetPullRequestChangesAsync(string baseCommit)
    {
        CommandResult result = await RunRequiredAsync("git", ["diff", "--name-status", "-z", "--find-renames", $"{baseCommit}...HEAD", "--"]);
        return ParseNameStatus(result.StdOut);
    }

    static async Task<IReadOnlyList<WorktreeChange>> GetCurrentWorktreeChangesAsync()
    {
        CommandResult result = await RunRequiredAsync("git", ["status", "--porcelain=v1", "-z", "--untracked-files=all"]);
        return ParsePorcelainStatus(result.StdOut);
    }

    static async Task<List<WorktreeChange>> GetCurrentWorktreeBomChangesAsync(string repositoryRoot, string currentPrefix)
    {
        IReadOnlyList<WorktreeChange> changes = await GetCurrentWorktreeChangesAsync();
        List<WorktreeChange> bomChanges = [];

        foreach (WorktreeChange change in changes)
        {
            if (await IsBomOnlyWorktreeChangeAsync(change, repositoryRoot, currentPrefix))
            {
                bomChanges.Add(change);
            }
        }

        return bomChanges;
    }

    static List<WorktreeChange> ParsePorcelainStatus(string output)
    {
        string[] tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<WorktreeChange> changes = [];
        int index = 0;

        while (index < tokens.Length)
        {
            string token = tokens[index++];
            if (token.Length < 4)
            {
                throw new ToolFailureException("Unexpected git status output while parsing a changed path.", 1);
            }

            string status = token[..2];
            string path = token[3..];
            string? oldPath = null;

            if (status.Contains('R') || status.Contains('C'))
            {
                if (index >= tokens.Length)
                {
                    throw new ToolFailureException("Unexpected git status output while parsing a renamed or copied path.", 1);
                }

                oldPath = tokens[index++];
            }

            changes.Add(new WorktreeChange(status, oldPath, path));
        }

        return changes;
    }

    static List<ChangeEntry> ParseNameStatus(string output)
    {
        string[] tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<ChangeEntry> entries = [];
        int index = 0;

        while (index < tokens.Length)
        {
            string status = tokens[index++];
            if (status.Length == 0)
            {
                continue;
            }

            char kind = status[0];
            if (kind is 'R' or 'C')
            {
                if (index + 1 >= tokens.Length)
                {
                    throw new ToolFailureException("Unexpected git diff output while parsing a rename or copy entry.", 1);
                }

                entries.Add(new ChangeEntry(kind, tokens[index++], tokens[index++]));
            }
            else
            {
                if (index >= tokens.Length)
                {
                    throw new ToolFailureException("Unexpected git diff output while parsing a changed path.", 1);
                }

                entries.Add(new ChangeEntry(kind, null, tokens[index++]));
            }
        }

        return entries;
    }

    static async Task<List<ResetOperation>> BuildBomResetOperationsAsync(IReadOnlyList<ChangeEntry> changes, string currentPrefix, string baseCommit)
    {
        List<ResetOperation> operations = [];
        HashSet<ResetOperation> seenOperations = [];

        foreach (ChangeEntry change in changes)
        {
            if (change.Kind != 'M' || !IsInCurrentScope(change.Path, currentPrefix))
            {
                continue;
            }

            byte[] baseBytes = await GetGitFileBytesAsync(baseCommit, change.Path);
            byte[] headBytes = await GetGitFileBytesAsync("HEAD", change.Path);
            if (HasBomChange(baseBytes, headBytes))
            {
                AddIfInScope(new ResetOperation(ResetOperationKind.Restore, change.Path, HasUtf8Bom(baseBytes)), currentPrefix, operations, seenOperations);
            }
        }

        return operations;
    }

    static void AddIfInScope(ResetOperation operation, string currentPrefix, List<ResetOperation> operations, HashSet<ResetOperation> seenOperations)
    {
        if (!IsInCurrentScope(operation.Path, currentPrefix) || !seenOperations.Add(operation))
        {
            return;
        }

        operations.Add(operation);
    }

    static bool IsInCurrentScope(string gitPath, string currentPrefix)
    {
        if (currentPrefix.Length == 0)
        {
            return true;
        }

        return gitPath.StartsWith(currentPrefix, StringComparison.Ordinal);
    }

    static async Task<bool> IsBomOnlyWorktreeChangeAsync(WorktreeChange change, string repositoryRoot, string currentPrefix)
    {
        if (!IsInCurrentScope(change.Path, currentPrefix)
            || change.OldPath is not null
            || change.Status == "??"
            || !change.Status.Contains('M')
            || change.Status.Contains('A')
            || change.Status.Contains('D')
            || change.Status.Contains('R')
            || change.Status.Contains('C')
            || change.Status.Contains('U'))
        {
            return false;
        }

        string fullPath = GetWorktreePath(repositoryRoot, change.Path);
        if (!File.Exists(fullPath))
        {
            return false;
        }

        byte[] headBytes = await GetGitFileBytesAsync("HEAD", change.Path);
        byte[] worktreeBytes = await File.ReadAllBytesAsync(fullPath);
        return HasBomChange(headBytes, worktreeBytes);
    }

    static async Task<bool> IsIndexBomOnlyChangeAsync(WorktreeChange change)
    {
        char indexStatus = change.Status[0];
        if (indexStatus is ' ' or '?' or 'A' or 'D' or 'R' or 'C' or 'U')
        {
            return false;
        }

        byte[] headBytes = await GetGitFileBytesAsync("HEAD", change.Path);
        byte[] indexBytes = await GetGitFileBytesAsync(":", change.Path);
        return HasBomChange(headBytes, indexBytes) && HasSameContentExceptBomAndLineEndings(headBytes, indexBytes);
    }

    static async Task SetWorktreeBomAsync(string repositoryRoot, string gitPath, bool expectedHasBom)
    {
        string fullPath = GetWorktreePath(repositoryRoot, gitPath);
        if (!File.Exists(fullPath))
        {
            throw new ToolFailureException($"Cannot reset BOM for '{gitPath}' because the file does not exist in the working tree.", 1);
        }

        byte[] bytes = await File.ReadAllBytesAsync(fullPath);
        bool actualHasBom = HasUtf8Bom(bytes);
        if (actualHasBom == expectedHasBom)
        {
            return;
        }

        byte[] updatedBytes = expectedHasBom ? AddUtf8Bom(bytes) : StripUtf8Bom(bytes).ToArray();
        await File.WriteAllBytesAsync(fullPath, updatedBytes);
    }

    static string GetWorktreePath(string repositoryRoot, string gitPath)
    {
        return Path.Combine(repositoryRoot, gitPath.Replace('/', Path.DirectorySeparatorChar));
    }

    static byte[] AddUtf8Bom(byte[] bytes)
    {
        byte[] updatedBytes = new byte[bytes.Length + 3];
        updatedBytes[0] = 0xEF;
        updatedBytes[1] = 0xBB;
        updatedBytes[2] = 0xBF;
        bytes.CopyTo(updatedBytes, 3);
        return updatedBytes;
    }

    static bool HasBomChange(byte[] oldBytes, byte[] newBytes)
    {
        return HasUtf8Bom(oldBytes) != HasUtf8Bom(newBytes);
    }

    static bool HasSameContentExceptBomAndLineEndings(byte[] oldBytes, byte[] newBytes)
    {
        return NormalizeLineEndings(StripUtf8Bom(oldBytes)).SequenceEqual(NormalizeLineEndings(StripUtf8Bom(newBytes)));
    }

    static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes is [0xEF, 0xBB, 0xBF, ..];
    }

    static ReadOnlySpan<byte> StripUtf8Bom(byte[] bytes)
    {
        return HasUtf8Bom(bytes) ? bytes.AsSpan(3) : bytes;
    }

    static byte[] NormalizeLineEndings(ReadOnlySpan<byte> bytes)
    {
        List<byte> normalized = [];

        for (int index = 0; index < bytes.Length; index++)
        {
            byte current = bytes[index];
            if (current == '\r')
            {
                if (index + 1 < bytes.Length && bytes[index + 1] == '\n')
                {
                    index++;
                }

                normalized.Add((byte)'\n');
                continue;
            }

            normalized.Add(current);
        }

        return [.. normalized];
    }

    static string FormatScope(string currentPrefix)
    {
        return currentPrefix.Length == 0 ? "the repository root" : currentPrefix.TrimEnd('/');
    }

    static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            throw new KeyNotFoundException(propertyName);
        }

        return property.GetString() ?? throw new KeyNotFoundException(propertyName);
    }

    static string NormalizeGitPath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    static Task<byte[]> GetGitFileBytesAsync(string revision, string path)
    {
        string objectName = revision == ":" ? $":{path}" : $"{revision}:{path}";
        return RunRequiredForBytesAsync("git", ["show", objectName]);
    }

    static async Task<CommandResult> RunRequiredAsync(string fileName, IReadOnlyList<string> arguments)
    {
        CommandResult result = await RunAsync(fileName, arguments);
        if (result.ExitCode == 0)
        {
            return result;
        }

        string details = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
        if (string.IsNullOrWhiteSpace(details))
        {
            details = $"Command exited with code {result.ExitCode}.";
        }

        throw new ToolFailureException($"{FormatCommand(fileName, arguments)} failed: {details}", result.ExitCode == 0 ? 1 : result.ExitCode);
    }

    static async Task<byte[]> RunRequiredForBytesAsync(string fileName, IReadOnlyList<string> arguments)
    {
        CommandBytesResult result = await RunForBytesAsync(fileName, arguments);
        if (result.ExitCode == 0)
        {
            return result.StdOut;
        }

        string details = result.StdErr.Trim();
        if (string.IsNullOrWhiteSpace(details))
        {
            details = $"Command exited with code {result.ExitCode}.";
        }

        throw new ToolFailureException($"{FormatCommand(fileName, arguments)} failed: {details}", result.ExitCode == 0 ? 1 : result.ExitCode);
    }

    static async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new CommandResult(process.ExitCode, stdout, stderr);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new ToolFailureException($"Required command '{fileName}' was not found on PATH.", 127);
        }
    }

    static async Task<CommandBytesResult> RunForBytesAsync(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
            using MemoryStream stdout = new();
            Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            return new CommandBytesResult(process.ExitCode, stdout.ToArray(), stderrTask.Result);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new ToolFailureException($"Required command '{fileName}' was not found on PATH.", 127);
        }
    }

    static string FormatCommand(string fileName, IReadOnlyList<string> arguments)
    {
        return string.Join(" ", [fileName, .. arguments.Select(QuoteArgument)]);
    }

    static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;
    }
}

static class ResetOperationKindExtensions
{
    public static string ToCheckDisplayText(this ResetOperationKind kind)
    {
        return kind switch
        {
            ResetOperationKind.Remove => "would remove",
            ResetOperationKind.Restore => "would restore",
            _ => $"would {kind.ToString().ToLowerInvariant()}",
        };
    }

    public static string ToDisplayText(this ResetOperationKind kind)
    {
        return kind switch
        {
            ResetOperationKind.Remove => "removed",
            ResetOperationKind.Restore => "restored",
            _ => kind.ToString().ToLowerInvariant(),
        };
    }
}

sealed class ToolFailureException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

record PullRequestInfo(int Number, string BaseRefName, string BaseRefOid, string Url);

record ChangeEntry(char Kind, string? OldPath, string Path);

record ResetOperation(ResetOperationKind Kind, string Path, bool ExpectedHasBom);

record WorktreeChange(string Status, string? OldPath, string Path)
{
    public string ToDisplayText()
    {
        string changeKind = Status switch
        {
            "??" => "untracked",
            _ when Status.Contains('R') => "renamed",
            _ when Status.Contains('C') => "copied",
            _ when Status.Contains('U') => "unmerged",
            _ when Status.Contains('A') => "added",
            _ when Status.Contains('D') => "deleted",
            _ when Status.Contains('M') => "modified",
            _ when Status.Contains('T') => "typechanged",
            _ => "changed",
        };

        return OldPath is null ? $"{changeKind} {Path}" : $"{changeKind} {OldPath} -> {Path}";
    }
}

enum ResetOperationKind
{
    Remove,
    Restore,
}

enum ResetMode
{
    Check,
    Reset,
}

record CommandResult(int ExitCode, string StdOut, string StdErr);

record CommandBytesResult(int ExitCode, byte[] StdOut, string StdErr);
