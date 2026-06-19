using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Tools;
using Agency.Llm.Common.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Agency.Harness.Console.Commands;

internal static class DumpContextCommand
{
    internal static CommandContinuation Run(ConsoleChatSession session)
    {
        IChatOutput output = session.ServiceProvider.GetRequiredService<IChatOutput>();

        ChatSession? chat = session.CurrentSession;
        if (chat is null)
        {
            output.WriteLine("No active session.");
            return CommandContinuation.Continue;
        }

        // Live context once a turn has started; otherwise a preview of what the next turn would send
        // (system prompt, tools, environment, user) with an empty conversation.
        Context ctx = chat.PreviewContext();

        // === SYSTEM PROMPT ===
        output.WriteLineMarkup("[bold yellow]══ SYSTEM PROMPT ══[/]");
        output.WriteLine(SystemPromptBuilder.Build(ctx));
        output.WriteLine();

        // === MESSAGES ===
        IReadOnlyList<ChatMessage> messages = ctx.Conversation.Messages;
        output.WriteLineMarkup($"[bold yellow]══ MESSAGES ({messages.Count}) ══[/]");
        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            output.WriteLineMarkup($"[bold]{i + 1}.[/] [blue]{message.Role}[/]");
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent tc:
                        output.WriteLine($"  {tc.Text}");
                        break;
                    case FunctionCallContent fcc:
                        output.WriteLineMarkup(
                            $"  [grey]tool-call[/] [cyan]{Markup.Escape(fcc.Name)}[/] [grey]{Markup.Escape(JsonSerializer.Serialize(fcc.Arguments))}[/]");
                        break;
                    case FunctionResultContent frc:
                        output.WriteLineMarkup(
                            $"  [grey]tool-result[/] [dim]{Markup.Escape(frc.CallId)}[/] {Markup.Escape(frc.Result?.ToString() ?? string.Empty)}");
                        break;
                    default:
                        output.WriteLineMarkup($"  [dim]({content.GetType().Name})[/]");
                        break;
                }
            }
        }

        output.WriteLine();

        // === TOOLS ===
        IReadOnlyList<ToolDefinition> tools = ctx.Tools.Registry.ListDefinitions();
        output.WriteLineMarkup($"[bold yellow]══ TOOLS ({tools.Count}) ══[/]");

        // Attribute each tool to its origin: an MCP server (by name) or the built-in set.
        // The MCP pool is the only place that knows which server contributed which tool.
        var mcpPool = session.ServiceProvider.GetService<McpClientPool>();
        var serverByTool = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mcpPool is not null)
        {
            foreach ((string server, IReadOnlyList<string> names) in mcpPool.ToolNamesByServer)
            {
                foreach (string name in names)
                {
                    serverByTool[name] = server;
                }
            }
        }

        const string builtIn = "Built-in";

        // Group order: built-in first, then each MCP server in configured order.
        var groupOrder = new List<string> { builtIn };
        if (mcpPool is not null)
        {
            groupOrder.AddRange(mcpPool.ToolNamesByServer.Keys);
        }

        var byGroup = tools
            .GroupBy(t => serverByTool.TryGetValue(t.Name, out string? s) ? s : builtIn)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (string group in groupOrder)
        {
            if (!byGroup.TryGetValue(group, out List<ToolDefinition>? groupTools))
            {
                continue;
            }

            string label = group == builtIn ? builtIn : $"{group} · MCP";
            output.WriteLineMarkup($"[bold yellow]{Markup.Escape(label)} ({groupTools.Count})[/]");

            foreach (ToolDefinition tool in groupTools)
            {
                string[] descLines = string.IsNullOrWhiteSpace(tool.Description)
                    ? []
                    : tool.Description.Split('\n');

                // Single-line description renders inline as "name: description"; multi-line stays stacked.
                if (descLines.Length <= 1)
                {
                    string desc = descLines.Length == 1 ? descLines[0].TrimEnd('\r').Trim() : string.Empty;
                    output.WriteLineMarkup(desc.Length > 0
                        ? $"  [bold cyan]{Markup.Escape(tool.Name)}[/][grey]: {Markup.Escape(desc)}[/]"
                        : $"  [bold cyan]{Markup.Escape(tool.Name)}[/]");
                }
                else
                {
                    output.WriteLineMarkup($"  [bold cyan]{Markup.Escape(tool.Name)}[/]");
                    foreach (string line in descLines)
                    {
                        output.WriteLineMarkup($"    [grey]{Markup.Escape(line.TrimEnd('\r'))}[/]");
                    }
                }

                // Suppress the deferred placeholder schema; render only real schemas (e.g. tool_help).
                if (!IsPlaceholderSchema(tool.InputSchema))
                {
                    output.WriteMarkup(Indent(RenderJson(tool.InputSchema), 4));
                    output.WriteLine();
                }
            }

            output.WriteLine();
        }

        return CommandContinuation.Continue;
    }

    /// <summary>Left-pads every line of an already-rendered markup block by <paramref name="spaces"/> spaces.</summary>
    private static string Indent(string markup, int spaces)
    {
        string pad = new(' ', spaces);
        return pad + markup.Replace("\n", "\n" + pad);
    }

    /// <summary>
    /// True when the schema is the progressive-discovery placeholder — an object whose only member
    /// is <c>"type": "object"</c>. These are identical across deferred tools and add only noise.
    /// </summary>
    private static bool IsPlaceholderSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool hasObjectType = false;
        foreach (JsonProperty prop in schema.EnumerateObject())
        {
            if (prop.NameEquals("type") && prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() == "object")
            {
                hasObjectType = true;
                continue;
            }

            return false;
        }

        return hasObjectType;
    }

    /// <summary>Renders <paramref name="element"/> as indented, syntax-coloured Spectre markup.</summary>
    internal static string RenderJson(JsonElement element)
    {
        var sb = new StringBuilder();
        WriteJson(sb, element, 1);
        return sb.ToString();
    }

    /// <summary>
    /// Recursively renders a <see cref="JsonElement"/> as indented, syntax-coloured Spectre markup.
    /// Working from the parsed element (rather than re-tokenising a string) keeps escaping correct:
    /// every literal value is passed through <see cref="Markup.Escape"/> before colour tags are added.
    /// </summary>
    private static void WriteJson(StringBuilder sb, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append("[grey50]{[/]");
                bool firstProp = true;
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    sb.Append(firstProp ? "\n" : "[grey50],[/]\n");
                    firstProp = false;
                    Pad(sb, depth);
                    sb.Append("[blue]");
                    sb.Append(Markup.Escape(JsonSerializer.Serialize(prop.Name)));
                    sb.Append("[/][grey50]:[/] ");
                    WriteJson(sb, prop.Value, depth + 1);
                }

                if (!firstProp)
                {
                    sb.Append('\n');
                    Pad(sb, depth - 1);
                }

                sb.Append("[grey50]}[/]");
                break;

            case JsonValueKind.Array:
                sb.Append("[grey50][[[/]");
                bool firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    sb.Append(firstItem ? "\n" : "[grey50],[/]\n");
                    firstItem = false;
                    Pad(sb, depth);
                    WriteJson(sb, item, depth + 1);
                }

                if (!firstItem)
                {
                    sb.Append('\n');
                    Pad(sb, depth - 1);
                }

                sb.Append("[grey50]]][/]");
                break;

            case JsonValueKind.String:
                sb.Append("[green]");
                sb.Append(Markup.Escape(element.GetRawText()));
                sb.Append("[/]");
                break;

            case JsonValueKind.Number:
                sb.Append("[aqua]");
                sb.Append(Markup.Escape(element.GetRawText()));
                sb.Append("[/]");
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                sb.Append("[yellow]");
                sb.Append(element.GetRawText());
                sb.Append("[/]");
                break;

            case JsonValueKind.Null:
                sb.Append("[red]null[/]");
                break;

            default:
                sb.Append(Markup.Escape(element.GetRawText()));
                break;
        }
    }

    private static void Pad(StringBuilder sb, int depth)
    {
        for (int i = 0; i < depth; i++)
        {
            sb.Append("  ");
        }
    }
}
