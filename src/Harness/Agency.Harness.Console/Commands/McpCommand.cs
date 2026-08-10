using Agency.Harness.Tools;
using Agency.Llm.Common.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Agency.Harness.Console.Commands;

/// <summary>
/// Pure, UI-free helpers backing <c>/mcp-list</c> and <c>/mcp-toggle</c>. Kept independent of
/// Spectre.Console and the console session so the logic is testable against plain dictionaries —
/// the MCP client pool has a private constructor and requires live servers to construct, so it can
/// never be built in a test.
/// </summary>
internal static class McpCommand
{
    /// <summary>The registered command text for <c>/mcp-list</c>.</summary>
    internal const string ListCommandText = "/mcp-list";

    /// <summary>The registered command text for <c>/mcp-toggle</c>.</summary>
    internal const string ToggleCommandText = "/mcp-toggle";

    /// <summary>
    /// The connection and tool-enablement status of one configured MCP server.
    /// </summary>
    /// <param name="Name">The server name as configured under <c>Mcp:Servers</c>.</param>
    /// <param name="EnabledTools">The number of the server's tools currently enabled in the registry; <c>0</c> if not connected.</param>
    /// <param name="TotalTools">The total number of tools the server contributed; <c>0</c> if not connected.</param>
    /// <param name="Error">The connection failure message; <see langword="null"/> if the server connected or is disabled in config.</param>
    /// <param name="Connected">Whether the server is currently connected.</param>
    internal sealed record ServerStatus(string Name, int EnabledTools, int TotalTools, string? Error, bool Connected)
    {
        /// <summary>Gets whether the server was attempted and failed to connect at startup.</summary>
        internal bool Failed => !this.Connected && this.Error is not null;

        /// <summary>Gets whether the server is disabled in config and was never attempted.</summary>
        internal bool Disabled => !this.Connected && this.Error is null;

        /// <summary>Gets whether at least one of the server's tools is currently enabled.</summary>
        internal bool On => this.Connected && this.EnabledTools > 0;
    }

    /// <summary>
    /// Builds a <see cref="ServerStatus"/> for every configured server: connected servers first, in
    /// <paramref name="toolNamesByServer"/>'s configured order, then any server present only in
    /// <paramref name="failedServers"/>, then any server in <paramref name="disabledServers"/>. A failed
    /// connection contributes no tools, so it is absent from <paramref name="toolNamesByServer"/> and would
    /// otherwise vanish from the listing; a disabled server is never attempted at all, so it appears in
    /// neither dictionary.
    /// </summary>
    /// <param name="toolNamesByServer">Tool names discovered from each connected server, keyed by server name.</param>
    /// <param name="failedServers">The failure message for each server that failed to connect, keyed by server name.</param>
    /// <param name="disabledServers">The names of servers disabled in config, never attempted, in configured order.</param>
    /// <param name="registry">The tool registry to read enabled state from.</param>
    /// <returns>One <see cref="ServerStatus"/> per configured server.</returns>
    internal static IReadOnlyList<ServerStatus> BuildStatuses(
        IReadOnlyDictionary<string, IReadOnlyList<string>> toolNamesByServer,
        IReadOnlyDictionary<string, string> failedServers,
        IReadOnlyList<string> disabledServers,
        IToolRegistry registry)
    {
        Dictionary<string, bool> enabledByToolName = registry.ListAllDefinitions()
            .ToDictionary(entry => entry.Definition.Name, entry => entry.Enabled);

        var statuses = new List<ServerStatus>(toolNamesByServer.Count + failedServers.Count + disabledServers.Count);

        foreach ((string server, IReadOnlyList<string> toolNames) in toolNamesByServer)
        {
            int enabledCount = toolNames.Count(name => enabledByToolName.TryGetValue(name, out bool enabled) && enabled);
            statuses.Add(new ServerStatus(server, enabledCount, toolNames.Count, Error: null, Connected: true));
        }

        foreach ((string server, string error) in failedServers)
        {
            if (toolNamesByServer.ContainsKey(server))
            {
                continue;
            }

            statuses.Add(new ServerStatus(server, EnabledTools: 0, TotalTools: 0, error, Connected: false));
        }

        foreach (string server in disabledServers)
        {
            statuses.Add(new ServerStatus(server, EnabledTools: 0, TotalTools: 0, Error: null, Connected: false));
        }

        return statuses;
    }

    /// <summary>
    /// Enables or disables every tool in <paramref name="toolNames"/> on the user axis of
    /// <paramref name="registry"/>, masking or revealing them for the current session without
    /// affecting the underlying MCP connection.
    /// </summary>
    /// <param name="registry">The tool registry to toggle tools in.</param>
    /// <param name="toolNames">The tool names to toggle — typically one server's contributed tools.</param>
    /// <param name="enable"><see langword="true"/> to enable the tools; <see langword="false"/> to disable them.</param>
    internal static void ApplyToggle(IToolRegistry registry, IReadOnlyList<string> toolNames, bool enable)
    {
        foreach (string name in toolNames)
        {
            if (enable)
            {
                registry.EnableToolByUser(name);
            }
            else
            {
                registry.DisableToolByUser(name);
            }
        }
    }

    /// <summary>
    /// Resolves operator-supplied input against the set of known server names, matching
    /// case-insensitively.
    /// </summary>
    /// <param name="raw">The server name as typed by the user.</param>
    /// <param name="knownServers">The configured server names to match against.</param>
    /// <returns>The canonical stored server name, or <see langword="null"/> if none matches.</returns>
    internal static string? ResolveServer(string raw, IEnumerable<string> knownServers)
    {
        return knownServers.FirstOrDefault(server => server.Equals(raw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Prints a table of every configured MCP server with its connection status and tool count.
    /// Prints a friendly message instead if MCP is unconfigured or the pool never initialized
    /// (e.g. under the <c>Test</c> environment, whose <c>appsettings</c> has no <c>Mcp</c> section).
    /// </summary>
    /// <param name="session">The active console chat session.</param>
    /// <returns><see cref="CommandContinuation.Continue"/> always.</returns>
    public static Task<CommandContinuation> ListAsync(ConsoleChatSession session)
    {
        McpClientPool? pool = session.ServiceProvider.GetService<McpClientPool>();
        if (pool is null)
        {
            AnsiConsole.MarkupLine("[yellow]No MCP servers configured.[/]");
            return Task.FromResult(CommandContinuation.Continue);
        }

        IReadOnlyList<ServerStatus> statuses =
            BuildStatuses(pool.ToolNamesByServer, pool.FailedServers, pool.DisabledServers, session.Tools.Registry);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Server");
        table.AddColumn("Status");
        table.AddColumn("Tools");

        foreach (ServerStatus status in statuses)
        {
            string statusText = status.Failed
                ? "[red]✗ failed[/]"
                : status.On ? "[green]● on[/]" : "[grey]○ off[/]";
            string toolsText = status.Disabled
                ? "[grey]not connected[/]"
                : status.EnabledTools == status.TotalTools
                    ? $"{status.TotalTools}"
                    : $"{status.EnabledTools}/{status.TotalTools} enabled";

            table.AddRow(Markup.Escape(status.Name), statusText, toolsText);
        }

        AnsiConsole.Write(table);

        foreach (ServerStatus status in statuses.Where(status => status.Failed))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(status.Name)}:[/] {Markup.Escape(status.Error!)}");
        }

        return Task.FromResult(CommandContinuation.Continue);
    }

    /// <summary>
    /// Enables or disables one MCP server's tools for the current session. The server's
    /// subprocess keeps running — this only masks its tools in the registry, so a disabled
    /// server's tools drop off the model's advertised list on the next turn and are rejected
    /// immediately if invoked.
    /// </summary>
    /// <param name="input">The full command line, e.g. <c>/mcp-toggle github</c>.</param>
    /// <param name="session">The active console chat session.</param>
    /// <returns><see cref="CommandContinuation.Continue"/> always.</returns>
    public static Task<CommandContinuation> ToggleAsync(string input, ConsoleChatSession session)
    {
        McpClientPool? pool = session.ServiceProvider.GetService<McpClientPool>();
        if (pool is null)
        {
            AnsiConsole.MarkupLine("[yellow]No MCP servers configured.[/]");
            return Task.FromResult(CommandContinuation.Continue);
        }

        IReadOnlyList<ServerStatus> statuses =
            BuildStatuses(pool.ToolNamesByServer, pool.FailedServers, pool.DisabledServers, session.Tools.Registry);

        string raw = input[ToggleCommandText.Length..].Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (statuses.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No MCP servers to toggle.[/]");
                return Task.FromResult(CommandContinuation.Continue);
            }

            raw = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select MCP server to toggle:")
                    .AddChoices(statuses.Select(status => status.Name)));
        }

        string? resolvedName = ResolveServer(raw, statuses.Select(status => status.Name));

        if (resolvedName is null)
        {
            string known = string.Join(", ", statuses.Select(status => status.Name));
            AnsiConsole.MarkupLine(
                $"[red]Unknown MCP server '{Markup.Escape(raw)}'.[/] Known servers: {Markup.Escape(known)}");
            return Task.FromResult(CommandContinuation.Continue);
        }

        ServerStatus target = statuses.First(status => status.Name == resolvedName);

        if (target.Failed)
        {
            PersistEnabled(session, resolvedName, enabled: false);
            AnsiConsole.MarkupLine(
                $"[yellow]MCP server '{Markup.Escape(resolvedName)}' never connected — {Markup.Escape(target.Error!)}. " +
                "Disabled in config; it will not be started next time.[/]");
            return Task.FromResult(CommandContinuation.Continue);
        }

        if (target.Disabled)
        {
            PersistEnabled(session, resolvedName, enabled: true);
            AnsiConsole.MarkupLine(
                $"[green]MCP server '{Markup.Escape(resolvedName)}' enabled in config. " +
                "It cannot connect mid-process — restart to start it on the next launch.[/]");
            return Task.FromResult(CommandContinuation.Continue);
        }

        bool enable = !target.On;
        IReadOnlyList<string> toolNames = pool.ToolNamesByServer[resolvedName];
        ApplyToggle(session.Tools.Registry, toolNames, enable);
        PersistEnabled(session, resolvedName, enable);

        AnsiConsole.MarkupLine(enable
            ? $"[green]MCP server '{Markup.Escape(resolvedName)}' enabled — {toolNames.Count} tool(s) restored. " +
              "Takes effect on the next turn and persists across restarts. Use /dump-context to verify.[/]"
            : $"[yellow]MCP server '{Markup.Escape(resolvedName)}' disabled — {toolNames.Count} tool(s) hidden. " +
              "Takes effect on the next turn and persists across restarts. Use /dump-context to verify.[/]");

        return Task.FromResult(CommandContinuation.Continue);
    }

    /// <summary>
    /// Persists <paramref name="enabled"/> for <paramref name="serverName"/> to <c>appsettings.json</c>,
    /// mirroring <see cref="ModelsCommand"/>'s persistence guard. Skipped under the <c>Test</c> environment,
    /// whose functional tests replay cached HTTP and must not write to the test appsettings.
    /// </summary>
    /// <param name="session">The active console chat session.</param>
    /// <param name="serverName">The MCP server name to persist the toggle for.</param>
    /// <param name="enabled">The new <c>Enabled</c> value to persist.</param>
    private static void PersistEnabled(ConsoleChatSession session, string serverName, bool enabled)
    {
        var environment = session.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (!environment.IsEnvironment("Test"))
        {
            string appSettingsPath = Path.Combine(environment.ContentRootPath, "appsettings.json");
            McpServerConfiguration.Persist(appSettingsPath, serverName, enabled);
        }
    }
}
