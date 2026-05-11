using System.Text;
using Agency.GraphRAG.Code.Chunker;
using Microsoft.Extensions.Options;

namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Builds prompts for symbol summarization.
/// </summary>
public sealed class SummarizationPromptBuilder(IOptions<SummarizerOptions> options)
{
    private int MaxContentChars => options.Value.MaxContentChars;
    private int MaxParentContextChars => options.Value.MaxParentContextChars;

    /// <summary>
    /// Builds the prompt for a one-line purpose summary.
    /// </summary>
    /// <param name="chunk">The chunk to summarize.</param>
    /// <returns>The rendered prompt.</returns>
    public string BuildOneLinePrompt(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        return CreateBasePrompt(
            chunk,
            "Write exactly one sentence that states this symbol's primary purpose. Output only that sentence — no preamble, no explanation.");
    }

    /// <summary>
    /// Builds the prompt for a detailed symbol summary.
    /// </summary>
    /// <param name="chunk">The chunk to summarize.</param>
    /// <returns>The rendered prompt.</returns>
    public string BuildDetailedPrompt(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        return CreateBasePrompt(
            chunk,
            "Write a detailed summary that covers responsibilities, inputs, outputs, side effects, and important collaborators or calls. Respond directly — no preamble or explanation of your process.");
    }

    /// <summary>
    /// Builds the prompt for a detailed implementation summary with parent context.
    /// </summary>
    /// <param name="chunk">The implementation chunk to summarize.</param>
    /// <param name="parentSummaries">Interface or base-type summaries that should be injected as context.</param>
    /// <returns>The rendered prompt.</returns>
    public string BuildDetailedForImplementationPrompt(Chunk chunk, IReadOnlyList<string> parentSummaries)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(parentSummaries);

        StringBuilder builder = new();
        builder.AppendLine("You are summarizing a source-code symbol.");
        builder.AppendLine("Write a detailed summary that explains how this implementation fulfills its parent contract. Cover responsibilities, inputs, outputs, side effects, and important collaborators or calls. Respond directly — no preamble or explanation of your process.");
        builder.AppendLine();
        AppendMetadata(builder, chunk);
        builder.AppendLine();
        builder.AppendLine("Parent context:");

        for (int index = 0; index < parentSummaries.Count; index++)
        {
            builder.Append("- ");
            string summary = parentSummaries[index];
            if (summary.Length > MaxParentContextChars)
            {
                builder.AppendLine(summary[..MaxParentContextChars]);
            }
            else
            {
                builder.AppendLine(summary);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Source:");
        builder.AppendLine("```");
        AppendContent(builder, chunk.Content);
        builder.Append("```");
        return builder.ToString();
    }

    private string CreateBasePrompt(Chunk chunk, string instruction)
    {
        StringBuilder builder = new();
        builder.AppendLine("You are summarizing a source-code symbol.");
        builder.AppendLine(instruction);
        builder.AppendLine();
        AppendMetadata(builder, chunk);
        builder.AppendLine();
        builder.AppendLine("Source:");
        builder.AppendLine("```");
        AppendContent(builder, chunk.Content);
        builder.Append("```");
        return builder.ToString();
    }

    private static void AppendMetadata(StringBuilder builder, Chunk chunk)
    {
        builder.Append("Language: ");
        builder.AppendLine(chunk.Language.ToString());
        builder.Append("Path: ");
        builder.AppendLine(chunk.Path);
        builder.Append("Symbol kind: ");
        builder.AppendLine(chunk.SymbolKind.ToString());
        builder.Append("Name: ");
        builder.AppendLine(chunk.Name);
        builder.Append("Fully qualified name: ");
        builder.AppendLine(chunk.FullyQualifiedName);
        builder.Append("Signature: ");
        builder.AppendLine(chunk.Signature ?? "(none)");
        builder.Append("Inherits: ");
        builder.AppendLine(FormatList(chunk.Inherits));
        builder.Append("Implements: ");
        builder.AppendLine(FormatList(chunk.Implements));
        builder.Append("Imports in scope: ");
        builder.AppendLine(FormatImports(chunk.ImportsInScope));
    }

    private void AppendContent(StringBuilder builder, string content)
    {
        if (content.Length <= MaxContentChars)
        {
            builder.AppendLine(content);
            return;
        }

        builder.AppendLine(content[..MaxContentChars]);
        builder.AppendLine($"[... truncated: {content.Length - MaxContentChars} chars omitted ...]");
    }

    private static string FormatList(IReadOnlyList<string>? values) =>
        values is null || values.Count == 0 ? "(none)" : string.Join(", ", values);

    private static string FormatImports(IReadOnlyList<ImportReference> imports) =>
        imports.Count == 0 ? "(none)" : string.Join(", ", imports.Select(importReference => importReference.Source));
}
