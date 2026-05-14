# Project Guidelines

## Changelog

- Keep [CHANGELOG.md](../CHANGELOG.md) updated for every user-visible change to the `bom` command-line tool or NuGet package.
- Add new entries under `## [Unreleased]` before release.
- Mention command behavior, packaging, documentation, and compatibility changes that users should know about.
- Do not add entries for internal repository workflow changes, agent instructions, skills, tests, or refactorings unless they change behavior that `bom` users observe.

## Build And Test

- This is a single-file .NET file-based app in [bom.cs](../bom.cs).
- Prefer focused changes that preserve the existing command-line behavior and Git interactions.