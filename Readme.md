# Pandatech.Analyzers

Company Roslyn analyzers for enforcing Pandatech coding rules.

Currently the package contains rules around async method conventions, and is intended to grow over time with additional
internal policies.

## PT0001 – Async methods must end with `Async`

**Definition of async:**

A method is considered async if its return type is one of:

- `System.Threading.Tasks.Task`
- `System.Threading.Tasks.Task<T>`
- `System.Threading.Tasks.ValueTask`
- `System.Threading.Tasks.ValueTask<T>`

**Rule:**

Such methods must have a name ending with `Async`.

Examples:

```csharp
Task<int> GetValueAsync(CancellationToken ct);     // OK
Task<int> GetValue(CancellationToken ct);          // PT0001
```

## PT0002 – Async methods must accept a `CancellationToken`

Async methods must include a `CancellationToken` parameter:

Examples:

```csharp
Task<int> GetValueAsync(int id, CancellationToken ct);    // OK
Task<int> GetValueAsync();                                // PT0002
```

## PT0003 – `CancellationToken` parameter must be named `ct`

If an async method declares a `CancellationToken`, it must be named `ct`.

Examples:

```csharp
Task<int> GetValueAsync(int id, CancellationToken ct);        // OK
Task<int> GetValueAsync(int id, CancellationToken token);     // PT0003
```

PT0004 – `CancellationToken` parameter must be last
If an async method declares a `CancellationToken`, it must be the last parameter.
Examples:

```csharp
Task<int> GetValueAsync(int id, CancellationToken ct);        // OK
Task<int> GetValueAsync(CancellationToken ct, int id);        // PT0004
Task<int> GetValueAsync(CancellationToken ct);                // OK
````

## Usage

Add the package to the projects you want analyzed:

```xml

<ItemGroup>
    <PackageReference Include="Pandatech.Analyzers" Version="1.0.0" PrivateAssets="all"/>
</ItemGroup>
```

Configure severities via `.editorconfig`:

```txt
dotnet_diagnostic.PT0001.severity = error   # Async suffix
dotnet_diagnostic.PT0002.severity = error   # CT missing
dotnet_diagnostic.PT0003.severity = error   # CT name
dotnet_diagnostic.PT0004.severity = error   # CT position
```