# ADR: Tree-sitter sidecar

## Status

Accepted

## Decision

Use a vendored Node.js sidecar at `tools/treesitter-sidecar/` for tree-sitter parsing.

The sidecar runs as a single stdin/stdout process and exchanges one JSON object per line:

- Request: `{"file":"...","language":"csharp|typescript|javascript|python","source":"..."}`
- Success response: `{"ok":true,"file":"...","language":"...","ast":{...}}`
- Error response: `{"ok":false,"file":"...","language":"...","error":{"code":"...","message":"..."}}`

## Rationale

- Matches the implementation plan recommendation.
- Uses stable npm packages instead of introducing a separate Rust toolchain.
- Keeps all supported grammars in one long-lived process for the future .NET client.
- JSON Lines over stdin/stdout is simple to host from .NET and easy to debug from the terminal.
- Vendoring the script under `tools/treesitter-sidecar/` keeps the dependency surface explicit and repo-local.

## Consequences

- Requires Node.js and `npm install` before use.
- AST shape is defined by this sidecar and should remain backward-compatible for the client lane.
- Process management, request multiplexing, retries, and .NET tests remain in the client lane.
