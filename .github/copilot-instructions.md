# Project Guidelines

## Changelog

- Keep [CHANGELOG.md](../CHANGELOG.md) updated for every user-visible change.
- Add new entries under `## [Unreleased]` before release.
- Mention command behavior, packaging, documentation, and compatibility changes that users should know about.

## Build And Test

- This is a single-file .NET file-based app in [bom.cs](../bom.cs).
- Prefer focused changes that preserve the existing command-line behavior and Git interactions.