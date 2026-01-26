# Pandatech.Analyzers

Roslyn analyzers enforcing Pandatech async coding conventions.

## Rules

| Rule | Description |
|------|-------------|
| PT0001 | Async methods must end with `Async` |
| PT0002 | Async methods must accept a `CancellationToken` |
| PT0003 | `CancellationToken` parameter must be named `ct` |
| PT0004 | `CancellationToken` must be the last parameter |

A method is **async** if it returns `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`.

### Examples

```csharp
// ✓ Correct
Task<int> GetValueAsync(int id, CancellationToken ct = default);

// ✗ PT0001: Missing 'Async' suffix
Task<int> GetValue(int id, CancellationToken ct);

// ✗ PT0002: Missing CancellationToken
Task<int> GetValueAsync(int id);

// ✗ PT0003: CancellationToken must be named 'ct'
Task<int> GetValueAsync(int id, CancellationToken token);

// ✗ PT0004: CancellationToken must be last
Task<int> GetValueAsync(CancellationToken ct, int id);
```

## Exclusions

The analyzer automatically skips:

| Context | Skipped Rules |
|---------|---------------|
| Interface implementations / overrides | PT0001, PT0002, PT0004 |
| Test methods (`[Fact]`, `[Test]`, etc.) | PT0002, PT0004 |
| ASP.NET Core middleware (`Invoke`/`InvokeAsync`) | PT0002, PT0003, PT0004 |
| SignalR hubs and hub interfaces | All rules |
| SignalR client interfaces (`Hub<TClient>`) | All rules |

## Code Fixes

All rules include automatic code fixes that handle:

- **Interface methods** → Updates interface and all implementations
- **Virtual/abstract methods** → Updates base and all overrides
- **Regular methods** → Updates the single method

## Installation

```xml
<ItemGroup>
    <PackageReference Include="Pandatech.Analyzers" Version="2.0.0" PrivateAssets="all" />
</ItemGroup>
```

## Configuration

Configure severity in `.editorconfig`:

```ini
dotnet_diagnostic.PT0001.severity = error
dotnet_diagnostic.PT0002.severity = error
dotnet_diagnostic.PT0003.severity = warning
dotnet_diagnostic.PT0004.severity = warning
```