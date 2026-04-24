# Contributing to Bigtable.InMemoryEmulator

Thank you for your interest in contributing!

## Getting Started

1. Fork the repository
2. Clone your fork locally
3. Create a feature branch: `git checkout -b my-feature`
4. Make your changes following the guidelines below
5. Push and open a Pull Request

## Development Requirements

- .NET 8.0 SDK (or later)
- PowerShell (for test scripts)
- Docker (for Go emulator parity testing)

## Building

```bash
dotnet build Bigtable.InMemoryEmulator.sln
```

## Running Tests

```powershell
# Unit tests only
dotnet test tests/Bigtable.InMemoryEmulator.Tests.Unit

# Integration tests (in-memory)
dotnet test tests/Bigtable.InMemoryEmulator.Tests.Integration

# Full parity validation (requires Docker)
./scripts/validate-parity.ps1
```

## Guidelines

- **TDD**: Write a failing test first, then implement the minimum code to make it pass, then refactor.
- **Integration-first**: Write integration tests by default. Unit tests are only for code that genuinely requires `InternalsVisibleTo` access.
- **Test Classification**: Unit tests go in `Tests.Unit`, integration tests in `Tests.Integration`. See [AGENTS.md](AGENTS.md) for classification rules.
- **No breaking changes** without discussion in an issue first.
- **Keep PRs focused** — one feature or fix per PR.
- **Behavioral sources**: All behavioral logic must cite an approved source. See [AGENTS.md](AGENTS.md) for the approved sources list.

## Reporting Issues

- Use the [bug report template](.github/ISSUE_TEMPLATE/bug_report.md) for bugs.
- Use the [feature request template](.github/ISSUE_TEMPLATE/feature_request.md) for new ideas.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
