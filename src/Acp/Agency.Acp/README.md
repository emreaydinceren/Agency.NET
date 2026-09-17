# Agency.Acp

Agent Client Protocol (ACP) host for the Agency AI Toolkit: exposes the agent harness as a process
that speaks newline-delimited JSON-RPC 2.0 over stdio, so any ACP client can drive an Agency agent
without linking against it.

## Install

```
dotnet add package AgencyDotNet.Acp
```

## Status

This package currently ships the protocol transport, method dispatcher, and session lifecycle:
`initialize` is fully implemented; unsupported v1 methods (`authenticate`, `logout`,
`session/load`, `session/list`, `session/resume`, `session/set_mode`) return a proper JSON-RPC
`MethodNotFound` error; and `session/new`, `session/close`, `session/delete` create and dispose the
per-session object graph (model catalogue resolution, MCP tool discovery, per-session context
window). A session is always disposed exactly once, whether via `session/close`, transport
disconnect, or process shutdown — the first real ACP client never sends `session/close`, so
disposal cannot depend on it. `session/prompt`, `session/cancel`, and `session/set_config_option`
are routed but not yet implemented.

## Guarantee

stdout carries JSON-RPC protocol bytes only. Every log sink writes to a file, never to the console,
and `Console.Out` is replaced with a no-op writer before the transport starts, so a stray write from
anywhere in the process cannot corrupt the protocol stream.

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
