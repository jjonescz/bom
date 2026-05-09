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

Check for BOM changes introduced by the current GitHub pull request:

```powershell
dnx bom check pr
```

Reset BOM changes introduced by the current GitHub pull request:

```powershell
dnx bom reset pr
```

The `pr` target uses the GitHub CLI to detect the current pull request. The `worktree` target uses Git status in the current repository.

## Behavior

`bom` scopes checks and resets to the directory where the command is run. Reset operations only change the leading UTF-8 BOM state of matching files; other content edits in the same file are preserved.

`check` exits with code `1` when BOM changes are found and `0` when none are found.
