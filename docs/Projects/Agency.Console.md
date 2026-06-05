# Agency.Console
#console #dotnet #entrypoint

## What It Is

Agency.Console is the console executable project that writes Hello, World! and exits.

**Namespace:** None (top-level statements in Program.cs)

## API Surface

Agency.Console exposes no public types, interfaces, or extension methods. Its source contains a single top-level statement in Program.cs.

## How It Works

Runtime flow:

1. The process starts in top-level statements.
2. It writes Hello, World! to standard output.
3. It exits.

```csharp
using System;

Console.WriteLine("Hello, World!");
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Harness.Console]] | Provides another console entry point in the same solution with a different runtime purpose. |

## Design Notes

- Top-level statements keep the executable entry point minimal and readable for a single-line behavior.
- The project currently keeps its contract intentionally empty by exposing no public API surface.
