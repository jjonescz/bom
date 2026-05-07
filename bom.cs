#!/usr/bin/env dotnet
#:property Version=0.1.0
#:property Authors=Jan Jones
#:property Description=Manage BOM changes from the current pull request.
#:property PackageOutputPath=./nupkg

#:package System.CommandLine@2.0.0

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
        Argument<string> resetTargetArgument = CreateTargetArgument("reset");
        Argument<string> checkTargetArgument = CreateTargetArgument("check");

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

    static Argument<string> CreateTargetArgument(string commandName)
    {
        return new Argument<string>("target")
        {
            Description = $"What to {commandName}. Currently only 'pr' is supported.",
        };
    }

    static async Task<int> RunTargetCommandAsync(string? target, ResetMode mode)
    {
        if (target != "pr")
        {
            Console.Error.WriteLine($"Unsupported target '{target}'. Supported values: pr.");
            return 2;
        }

        try
        {
            return await ResetCurrentPrAsync(mode);
        }
        catch (ToolFailureException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
    }

    static async Task<int> ResetCurrentPrAsync(ResetMode mode)
    {
        string repositoryRoot = await GetRepositoryRootAsync();
        string currentPrefix = GetCurrentDirectoryPrefix(repositoryRoot);
        PullRequestInfo pullRequest = await GetCurrentPullRequestAsync();
        string baseCommit = await EnsureBaseCommitAvailableAsync(pullRequest);
        IReadOnlyList<ChangeEntry> changes = await GetPullRequestChangesAsync(baseCommit);
        List<ResetOperation> operations = BuildResetOperations(changes, currentPrefix);

        if (operations.Count == 0)
        {
            Console.WriteLine($"No PR changes found under {FormatScope(currentPrefix)} for PR #{pullRequest.Number}.");
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
            _ = operation.Kind switch
            {
                ResetOperationKind.Remove => await RunRequiredAsync("git", ["rm", "--force", "--ignore-unmatch", "--", operation.Path]),
                ResetOperationKind.Restore => await RunRequiredAsync("git", ["restore", "--source", baseCommit, "--staged", "--worktree", "--", operation.Path]),
                _ => throw new InvalidOperationException($"Unknown reset operation '{operation.Kind}'."),
            };

            Console.WriteLine($"{operation.Kind.ToDisplayText()} {operation.Path}");
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

    static List<ResetOperation> BuildResetOperations(IReadOnlyList<ChangeEntry> changes, string currentPrefix)
    {
        List<ResetOperation> operations = [];
        HashSet<ResetOperation> seenOperations = [];

        foreach (ChangeEntry change in changes)
        {
            AddOperationsForChange(change, currentPrefix, operations, seenOperations);
        }

        return operations;
    }

    static void AddOperationsForChange(ChangeEntry change, string currentPrefix, List<ResetOperation> operations, HashSet<ResetOperation> seenOperations)
    {
        switch (change.Kind)
        {
            case 'A':
                AddIfInScope(new ResetOperation(ResetOperationKind.Remove, change.Path), currentPrefix, operations, seenOperations);
                break;

            case 'R':
                if (change.OldPath is not null)
                {
                    AddIfInScope(new ResetOperation(ResetOperationKind.Restore, change.OldPath), currentPrefix, operations, seenOperations);
                }

                AddIfInScope(new ResetOperation(ResetOperationKind.Remove, change.Path), currentPrefix, operations, seenOperations);
                break;

            case 'C':
                AddIfInScope(new ResetOperation(ResetOperationKind.Remove, change.Path), currentPrefix, operations, seenOperations);
                break;

            default:
                AddIfInScope(new ResetOperation(ResetOperationKind.Restore, change.Path), currentPrefix, operations, seenOperations);
                break;
        }
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

record ResetOperation(ResetOperationKind Kind, string Path);

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
