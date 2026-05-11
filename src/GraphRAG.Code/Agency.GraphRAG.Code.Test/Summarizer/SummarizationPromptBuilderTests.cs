using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;
using Microsoft.Extensions.Options;

namespace Agency.GraphRAG.Code.Test.Summarizer;

/// <summary>
/// Tests for <see cref="SummarizationPromptBuilder"/>.
/// </summary>
public sealed class SummarizationPromptBuilderTests
{
    private static SummarizationPromptBuilder CreateBuilder(int maxContentChars = 8000) =>
        new(Options.Create(new SummarizerOptions { MaxContentChars = maxContentChars }));

    [Fact]
    public void BuildOneLinePrompt_RendersExpectedTemplate()
    {
        SummarizationPromptBuilder builder = CreateBuilder();

        string prompt = builder.BuildOneLinePrompt(CreateChunk()).ReplaceLineEndings("\n");

        Assert.Equal(
            """
            You are summarizing a source-code symbol.
            Write exactly one sentence that states this symbol's primary purpose. Output only that sentence — no preamble, no explanation.

            Language: CSharp
            Path: src\Payments\StripePaymentProcessor.cs
            Symbol kind: Class
            Name: StripePaymentProcessor
            Fully qualified name: Payments.StripePaymentProcessor
            Signature: public sealed class StripePaymentProcessor : IPaymentProcessor
            Inherits: PaymentProcessorBase
            Implements: IPaymentProcessor
            Imports in scope: System, Payments.Abstractions

            Source:
            ```
            public sealed class StripePaymentProcessor : IPaymentProcessor
            {
                public Task ChargeAsync(decimal amount) => Task.CompletedTask;
            }
            ```
            """.ReplaceLineEndings("\n"),
            prompt);
    }

    [Fact]
    public void BuildDetailedPrompt_RendersExpectedTemplate()
    {
        SummarizationPromptBuilder builder = CreateBuilder();

        string prompt = builder.BuildDetailedPrompt(CreateChunk()).ReplaceLineEndings("\n");

        Assert.Equal(
            """
            You are summarizing a source-code symbol.
            Write a detailed summary that covers responsibilities, inputs, outputs, side effects, and important collaborators or calls. Respond directly — no preamble or explanation of your process.

            Language: CSharp
            Path: src\Payments\StripePaymentProcessor.cs
            Symbol kind: Class
            Name: StripePaymentProcessor
            Fully qualified name: Payments.StripePaymentProcessor
            Signature: public sealed class StripePaymentProcessor : IPaymentProcessor
            Inherits: PaymentProcessorBase
            Implements: IPaymentProcessor
            Imports in scope: System, Payments.Abstractions

            Source:
            ```
            public sealed class StripePaymentProcessor : IPaymentProcessor
            {
                public Task ChargeAsync(decimal amount) => Task.CompletedTask;
            }
            ```
            """.ReplaceLineEndings("\n"),
            prompt);
    }

    [Fact]
    public void BuildDetailedForImplementationPrompt_IncludesParentContext()
    {
        SummarizationPromptBuilder builder = CreateBuilder();

        string prompt = builder.BuildDetailedForImplementationPrompt(
            CreateChunk(),
            ["IPaymentProcessor handles payment authorization and capture.", "PaymentProcessorBase centralizes telemetry and retries."])
            .ReplaceLineEndings("\n");

        Assert.Equal(
            """
            You are summarizing a source-code symbol.
            Write a detailed summary that explains how this implementation fulfills its parent contract. Cover responsibilities, inputs, outputs, side effects, and important collaborators or calls. Respond directly — no preamble or explanation of your process.

            Language: CSharp
            Path: src\Payments\StripePaymentProcessor.cs
            Symbol kind: Class
            Name: StripePaymentProcessor
            Fully qualified name: Payments.StripePaymentProcessor
            Signature: public sealed class StripePaymentProcessor : IPaymentProcessor
            Inherits: PaymentProcessorBase
            Implements: IPaymentProcessor
            Imports in scope: System, Payments.Abstractions

            Parent context:
            - IPaymentProcessor handles payment authorization and capture.
            - PaymentProcessorBase centralizes telemetry and retries.

            Source:
            ```
            public sealed class StripePaymentProcessor : IPaymentProcessor
            {
                public Task ChargeAsync(decimal amount) => Task.CompletedTask;
            }
            ```
            """.ReplaceLineEndings("\n"),
            prompt);
    }

    [Fact]
    public void BuildOneLinePrompt_TruncatesContentExceedingLimit()
    {
        SummarizationPromptBuilder builder = CreateBuilder(maxContentChars: 10);
        string longContent = new('x', 25);
        Chunk chunk = CreateChunk(content: longContent);

        string prompt = builder.BuildOneLinePrompt(chunk);

        Assert.Contains(new string('x', 10), prompt);
        Assert.Contains("[... truncated: 15 chars omitted ...]", prompt);
        Assert.DoesNotContain(new string('x', 11), prompt);
    }

    [Fact]
    public void BuildDetailedPrompt_TruncatesContentExceedingLimit()
    {
        SummarizationPromptBuilder builder = CreateBuilder(maxContentChars: 10);
        string longContent = new('x', 25);

        string prompt = builder.BuildDetailedPrompt(CreateChunk(content: longContent));

        Assert.Contains("[... truncated: 15 chars omitted ...]", prompt);
    }

    [Fact]
    public void BuildDetailedForImplementationPrompt_TruncatesContentExceedingLimit()
    {
        SummarizationPromptBuilder builder = CreateBuilder(maxContentChars: 10);
        string longContent = new('x', 25);

        string prompt = builder.BuildDetailedForImplementationPrompt(CreateChunk(content: longContent), ["parent summary"]);

        Assert.Contains("[... truncated: 15 chars omitted ...]", prompt);
    }

    [Fact]
    public void BuildDetailedForImplementationPrompt_TruncatesLongParentSummaries()
    {
        SummarizationPromptBuilder builder = new(Options.Create(new SummarizerOptions { MaxParentContextChars = 10 }));
        string longParent = new('p', 25);

        string prompt = builder.BuildDetailedForImplementationPrompt(CreateChunk(), [longParent]);

        Assert.Contains(new string('p', 10), prompt);
        Assert.DoesNotContain(new string('p', 11), prompt);
    }

    private static Chunk CreateChunk(string? content = null) =>
        new(
            "chunk-1",
            @"src\Payments\StripePaymentProcessor.cs",
            Language.CSharp,
            ChunkGranularity.Type,
            "StripePaymentProcessor",
            "Payments.StripePaymentProcessor",
            "public sealed class StripePaymentProcessor : IPaymentProcessor",
            content ??
            """
            public sealed class StripePaymentProcessor : IPaymentProcessor
            {
                public Task ChargeAsync(decimal amount) => Task.CompletedTask;
            }
            """,
            new ChunkSourceRange(1, 0, 4, 1),
            SymbolKind.Class,
            [new ImportReference("System", [], false), new ImportReference("Payments.Abstractions", [], false)],
            ParentId: null,
            Inherits: ["PaymentProcessorBase"],
            Implements: ["IPaymentProcessor"]);
}
