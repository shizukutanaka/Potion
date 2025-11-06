# Contributing to Potion

Thank you for your interest in contributing to Potion! This document provides guidelines and instructions for contributing.

## Getting Started

1. Fork the repository
2. Clone your fork: `git clone https://github.com/yourusername/Potion.git`
3. Create a feature branch: `git checkout -b feature/your-feature-name`

## Development Setup

```powershell
# Restore dependencies
dotnet restore

# Build the project
dotnet build Potion.sln -c Debug

# Run tests
dotnet test tests/Potion.Service.Tests/Potion.Service.Tests.csproj
```

## Code Standards

- **Language**: C# 12 (.NET 8.0)
- **Style**: Follow Microsoft C# coding conventions
- **Async**: Use async/await throughout
- **DI**: Constructor-based dependency injection
- **Logging**: Use Serilog for structured logging
- **Tests**: Unit tests with xUnit required for new features

## Commit Guidelines

- Use clear, descriptive commit messages
- Reference issues in commits: `Fix: Resolve #123`
- One logical change per commit
- Keep commits small and focused

## Pull Request Process

1. Update documentation for any API changes
2. Add tests for new functionality
3. Ensure all tests pass: `dotnet test`
4. Build in Release mode: `dotnet build -c Release`
5. Create PR with clear description of changes
6. Link related issues

## Testing

All code must include unit tests:

```powershell
# Run all tests
dotnet test Potion.sln

# Run specific test class
dotnet test Potion.sln -k "TestClassName"

# Generate coverage report
dotnet test /p:CollectCoverage=true
```

## Security

- Never commit secrets or credentials
- Security vulnerabilities should be reported privately
- Follow principle of least privilege in code design
- Validate all external inputs

## Questions?

- Open an issue for bugs or feature requests
- Use discussions for questions
- Check existing issues before opening new ones

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
