# Changelog

All notable user-visible changes to the `bom` command-line tool and NuGet package will be documented in this file.

## Unreleased

- Fix `bom check pr` and `bom reset pr` in GitHub Actions pull request checkouts with shallow PR merge refs.

## v0.1.1 - 2026-05-14

- Support checking and resetting branch changes before a pull request is created.

## v0.1.0 - 2026-05-09

- Initial `bom` .NET tool for checking and resetting UTF-8 BOM changes in Git repositories.
- Support for `check` and `reset` commands against `pr` and `worktree` targets.
- Current-directory scoping for BOM checks and resets.
- GitHub CLI pull request detection.
