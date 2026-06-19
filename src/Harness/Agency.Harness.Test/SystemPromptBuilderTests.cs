
using Agency.Harness.Contexts;
using Agency.Harness.Skills;
using Agency.Harness.Tools;

namespace Agency.Harness.Test;
/// <summary>
/// Unit tests for <see cref="SystemPromptBuilder"/>.
/// The builder is a pure function — the same context always produces the same prompt string,
/// making it fully deterministic and trivial to assert on.
/// </summary>
public sealed class SystemPromptBuilderTests
{
    private static Context MinimalContext(string prompt = "What is the capital of France?") =>
        new()
        {
            Query = new QueryContext { Prompt = prompt },
        };

    // ── Baseline content ──────────────────────────────────────────────────────

    [Fact]
    public void Build_AlwaysContainsAgentIdentityLine()
    {
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.Contains("autonomous agent", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_AlwaysContainsReActInstruction()
    {
        // D11: explicit ReAct reasoning instruction to encourage chain-of-thought before tool use.
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.Contains("reasoning", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── LongTermMemory ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesLongTermMemory_WhenProvided()
    {
        var ctx = MinimalContext();
        ctx = ctx with
        {
            Memory = new MemoryContext { LongTermMemory = ["User prefers concise answers.", "User is a C# expert."] },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("User prefers concise answers.", result);
        Assert.Contains("User is a C# expert.", result);
    }

    [Fact]
    public void Build_OmitsLongTermMemorySection_WhenEmpty()
    {
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.DoesNotContain("Long-term memory", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── TemporalContext ────────────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesCurrentDate_WhenTemporalContextProvided()
    {
        var ctx = MinimalContext();
        ctx = ctx with
        {
            Temporal = new TemporalContext { CurrentDateUtc = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero) },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("2026", result);
    }

    // ── EnvironmentalContext ───────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesOsInfo_WhenEnvironmentalContextProvided()
    {
        var ctx = MinimalContext();
        ctx = ctx with
        {
            Environment = new EnvironmentalContext { OperatingSystem = "Windows 11" },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("Windows 11", result);
    }

    // ── UserSpecificContext ────────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesUserName_WhenUserContextProvided()
    {
        var ctx = MinimalContext();
        ctx = ctx with
        {
            User = new UserSpecificContext { Name = "Emre" },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("Emre", result);
    }

    // ── KnowledgeContext ───────────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesKnowledge_WhenKnowledgeContextProvided()
    {
        var ctx = MinimalContext();
        ctx = ctx with
        {
            Knowledge = new KnowledgeContext { Facts = ["Paris is the capital of France."] },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("Paris is the capital of France.", result);
    }

    // ── ContextWindowSize ─────────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesContextWindowSize_WhenSet()
    {
        var ctx = MinimalContext();
        ctx = ctx with
        {
            Environment = new EnvironmentalContext { ContextWindowSize = 4096 },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("4,096", result);
    }

    [Fact]
    public void Build_IncludesRemainingEstimate_WhenContextWindowSetAndPriorUsageKnown()
    {
        var ctx = MinimalContext();
        ctx = ctx with { Environment = new EnvironmentalContext { ContextWindowSize = 4096 } };
        ctx.TotalUsage = new LlmTokenUsage(511, 360);

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("3,585", result);  // 4096 - 511
        Assert.Contains("511", result);
    }

    [Fact]
    public void Build_OmitsContextWindowSection_WhenNotSet()
    {
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.DoesNotContain("Context window", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── Idempotency (D3 re-injection) ─────────────────────────────────────────

    [Fact]
    public void Build_IsIdempotent_GivenSameContext()
    {
        var ctx = MinimalContext();

        string first = SystemPromptBuilder.Build(ctx);
        string second = SystemPromptBuilder.Build(ctx);

        Assert.Equal(first, second);
    }

    // ── Progressive tool discovery ────────────────────────────────────────────

    [Fact]
    public void Build_IncludesToolHelpInstruction_WhenRegistryIsProgressive()
    {
        var ctx = MinimalContext() with
        {
            Tools = new ToolContext { Registry = new ProgressiveDiscoveryToolRegistry(new ToolRegistry(), new HashSet<string>()) },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("tool_help", result);
    }

    [Fact]
    public void Build_OmitsToolHelpInstruction_WhenRegistryIsPlain()
    {
        var ctx = MinimalContext() with
        {
            Tools = new ToolContext { Registry = new ToolRegistry() },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.DoesNotContain("tool_help", result);
    }

    // ── SkillContext ───────────────────────────────────────────────────────────

    private static Skill MakeSkill(string name, string description, string? whenToUse = null, bool disableModelInvocation = false) =>
        new()
        {
            Name = name,
            Description = description,
            WhenToUse = whenToUse,
            Body = "# Skill body",
            SkillDir = $"/skills/{name}",
            DisableModelInvocation = disableModelInvocation,
        };

    [Fact]
    public void Build_IncludesSkillsSection_WhenModelInvocableSkillsPresent()
    {
        Skill skill = MakeSkill("my-skill", "Does something useful", whenToUse: "Use it when you need X");
        var ctx = MinimalContext() with
        {
            Skills = new SkillContext { Catalog = new SkillCatalog([skill]) },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("## Skills", result);
        Assert.Contains("**my-skill**", result);
        Assert.Contains("Does something useful", result);
        Assert.Contains("Use it when you need X", result);
        Assert.Contains("`skill`", result);
    }

    [Fact]
    public void Build_SkillsSection_OmitsWhenToUse_WhenAbsent()
    {
        Skill skill = MakeSkill("bare-skill", "A bare description");
        var ctx = MinimalContext() with
        {
            Skills = new SkillContext { Catalog = new SkillCatalog([skill]) },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("**bare-skill** — A bare description", result);
        // No trailing parenthetical when WhenToUse is null.
        Assert.DoesNotContain("bare-skill** — A bare description (", result);
    }

    [Fact]
    public void Build_OmitsSkillsSection_WhenCatalogIsEmpty()
    {
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.DoesNotContain("## Skills", result);
    }

    [Fact]
    public void Build_OmitsSkillsSection_WhenAllSkillsAreDisabled()
    {
        Skill disabled = MakeSkill("hidden-skill", "Not for the model", disableModelInvocation: true);
        var ctx = MinimalContext() with
        {
            Skills = new SkillContext { Catalog = new SkillCatalog([disabled]) },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.DoesNotContain("## Skills", result);
        Assert.DoesNotContain("hidden-skill", result);
    }

    [Fact]
    public void Build_ExcludesDisabledSkills_WhenMixedCatalog()
    {
        Skill enabled = MakeSkill("enabled-skill", "Visible to model");
        Skill disabled = MakeSkill("disabled-skill", "Hidden from model", disableModelInvocation: true);
        var ctx = MinimalContext() with
        {
            Skills = new SkillContext { Catalog = new SkillCatalog([enabled, disabled]) },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("## Skills", result);
        Assert.Contains("enabled-skill", result);
        Assert.DoesNotContain("disabled-skill", result);
    }
}
