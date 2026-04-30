using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Summarizer;

/// <summary>
/// Tests for <see cref="SummarizationPromptBuilder"/>.
/// </summary>
public sealed class SummarizationPromptBuilderTests
{
    [Fact]
    public void BuildOneLinePrompt_RendersExpectedTemplate()
    {
        SummarizationPromptBuilder builder = new();

        string prompt = builder.BuildOneLinePrompt(CreateChunk()).ReplaceLineEndings("\n");

        Assert.Equal(
            """
            You are summarizing a source-code symbol.
            Write exactly one sentence that states this symbol's primary purpose. Do not mention line numbers or formatting.

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
        SummarizationPromptBuilder builder = new();

        string prompt = builder.BuildDetailedPrompt(CreateChunk()).ReplaceLineEndings("\n");

        Assert.Equal(
            """
            You are summarizing a source-code symbol.
            Write a detailed summary that covers responsibilities, inputs, outputs, side effects, and important collaborators or calls.

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
        SummarizationPromptBuilder builder = new();

        string prompt = builder.BuildDetailedForImplementationPrompt(
            CreateChunk(),
            ["IPaymentProcessor handles payment authorization and capture.", "PaymentProcessorBase centralizes telemetry and retries."])
            .ReplaceLineEndings("\n");

        Assert.Equal(
            """
            You are summarizing a source-code symbol.
            Write a detailed summary that explains how this implementation fulfills its parent contract. Cover responsibilities, inputs, outputs, side effects, and important collaborators or calls.

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

    private static Chunk CreateChunk() =>
        new(
            "chunk-1",
            @"src\Payments\StripePaymentProcessor.cs",
            Language.CSharp,
            ChunkGranularity.Type,
            "StripePaymentProcessor",
            "Payments.StripePaymentProcessor",
            "public sealed class StripePaymentProcessor : IPaymentProcessor",
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
