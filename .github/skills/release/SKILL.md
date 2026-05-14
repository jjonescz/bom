---
name: release
description: 'Release bom to NuGet. Use when asked to release, publish, tag a version, bump the package version, update the changelog for a release, pack the nupkg, or push to nuget.org.'
argument-hint: '<version>'
---

# Release

Use this skill to publish a new `bom` package release.

## Inputs

- Optional release version, with or without a leading `v`.
- If no version is provided, infer the next patch version from [bom.cs](../../../bom.cs).

## Procedure

1. Confirm `git status --short` is clean before starting. Stop if unrelated changes are present.
2. Determine the release version:
   - If the user provided a version, normalize it.
   - If the user did not provide a version, infer the next patch version from `#:property Version=` in [bom.cs](../../../bom.cs).
   - Tell the user the inferred version and continue with it. Only ask for a different version if the user requested a non-patch release, the inferred version is ambiguous, or the inferred tag already exists.
   - `packageVersion`: version without a leading `v`, for example `0.1.1`.
   - `tagName`: version with a leading `v`, for example `v0.1.1`.
   ```powershell
   $requestedVersion = "<version>"
   $currentVersion = (Select-String -Path bom.cs -Pattern '^#:property Version=(.+)$').Matches[0].Groups[1].Value.Trim()
   $parsedVersion = [version]$currentVersion
   $packageVersion = if ([string]::IsNullOrWhiteSpace($requestedVersion)) {
       [version]::new($parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build + 1).ToString()
   } else {
       $requestedVersion.TrimStart("v")
   }
   $tagName = "v$packageVersion"
   $today = Get-Date -Format yyyy-MM-dd
   ```
3. Confirm the release tag does not already exist:
   ```powershell
   git rev-parse --verify --quiet "refs/tags/$tagName"
   ```
   Stop if the tag exists.
4. Update [bom.cs](../../../bom.cs) so `#:property Version=` matches `$packageVersion`.
5. Update [CHANGELOG.md](../../../CHANGELOG.md):
   - Change `## Unreleased` to `## $tagName - $today`.
   - Add a new empty `## Unreleased` section above the release entry.
   - Ensure the release entry includes only user-visible `bom` command-line tool or NuGet package changes being released.
6. Commit the release bump:
   ```powershell
   git add bom.cs CHANGELOG.md
   git commit -m "Release $tagName"
   ```
7. Tag the new `HEAD` with the version:
   ```powershell
   git tag $tagName
   ```
8. Push `main` and the version tag:
   ```powershell
   git push origin main
   git push origin $tagName
   ```
9. Pack the NuGet package:
   ```powershell
   dotnet pack bom.cs --output artifacts/nupkg
   ```
10. Push the package to nuget.org:
   ```powershell
   dotnet nuget push "artifacts/nupkg/bom.$packageVersion.nupkg" --source https://api.nuget.org/v3/index.json
   ```
   Treat this command as the NuGet authentication check. If it fails because no nuget.org API key is configured, stop and tell the user to run the one-time setup:
   ```powershell
   winget install microsoft.nuget
   nuget setapikey '<api key here>' -source https://api.nuget.org/v3/index.json
   ```

## Notes

- Do not create or push a tag if the release commit fails.
- Do not push to NuGet unless the package was packed from the tagged release commit.
- If any command fails, stop and report the failure before trying another release step.