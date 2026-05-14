# bom

`bom` is a small .NET tool for finding and resetting UTF-8 byte order mark (BOM) changes in Git repositories.

## Usage

Check for BOM changes in the current Git working tree:

```powershell
dnx bom check worktree
```

Reset BOM changes in the current Git working tree:

```powershell
dnx bom reset worktree
```

Check for BOM changes introduced by the current GitHub pull request or branch:

```powershell
dnx bom check pr
```

Reset BOM changes introduced by the current GitHub pull request or branch:

```powershell
dnx bom reset pr
```

The `pr` target uses the GitHub CLI to detect the current pull request. If no pull request exists yet, it falls back to comparing the current branch against an inferred base branch. The base branch is inferred from `branch.<branch>.gh-merge-base`, the remote default branch, or common default branch names. The `worktree` target uses Git status in the current repository.

## Behavior

`bom` scopes checks and resets to the directory where the command is run. Reset operations only change the leading UTF-8 BOM state of matching files; other content edits in the same file are preserved.

`check` exits with code `1` when BOM changes are found and `0` when none are found.
