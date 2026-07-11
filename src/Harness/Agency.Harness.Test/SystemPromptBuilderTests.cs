
using Agency.Harness.Contexts;
using Agency.Harness.Skills;

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

    /// <summary>
    /// The built prompt always identifies the assistant as an autonomous agent.
    /// </summary>
    [Fact]
    public void Build_AlwaysContainsAgentIdentityLine()
    {
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.Contains("autonomous agent", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The built prompt always contains the explicit ReAct reasoning instruction that encourages
    /// chain-of-thought before tool use.
    /// </summary>
    [Fact]
    public void Build_AlwaysContainsReActInstruction()
    {
        // D11: explicit ReAct reasoning instruction to encourage chain-of-thought before tool use.
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.Contains("reasoning", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── LongTermMemory ─────────────────────────────────────────────────────────

    /// <summary>
    /// When <see cref="MemoryContext.LongTermMemory"/> entries are provided, every entry's text
    /// appears in the built prompt.
    /// </summary>
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

    /// <summary>
    /// When no long-term memory context is supplied, the built prompt has no "Long-term memory"
    /// section.
    /// </summary>
    [Fact]
    public void Build_OmitsLongTermMemorySection_WhenEmpty()
    {
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.DoesNotContain("Long-term memory", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── TemporalContext ────────────────────────────────────────────────────────

    /// <summary>
    /// When <see cref="TemporalContext.CurrentDateUtc"/> is set, the year from that date appears
    /// in the built prompt.
    /// </summary>
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

    /// <summary>
    /// When <see cref="EnvironmentalContext.OperatingSystem"/> is set, its value appears in the
    /// built prompt.
    /// </summary>
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

    /// <summary>
    /// When <see cref="UserSpecificContext.Name"/> is set, the user's name appears in the built
    /// prompt.
    /// </summary>
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

    /// <summary>
    /// When <see cref="KnowledgeContext.Facts"/> are provided, each fact's text appears in the
    /// built prompt.
    /// </summary>
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

    /// <summary>
    /// When <see cref="EnvironmentalContext.ContextWindowSize"/> is set, the built prompt renders
    /// it as a thousands-separated number.
    /// </summary>
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

    /// <summary>
    /// When both the context window size and prior token usage are known, the built prompt shows
    /// both the tokens used so far and the remaining budget (window size minus input tokens
    /// used).
    /// </summary>
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

    /// <summary>
    /// When <see cref="EnvironmentalContext.ContextWindowSize"/> is not set, the built prompt has
    /// no "Context window" section.
    /// </summary>
    [Fact]
    public void Build_OmitsContextWindowSection_WhenNotSet()
    {
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.DoesNotContain("Context window", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── Idempotency (D3 re-injection) ─────────────────────────────────────────

    /// <summary>
    /// Building the prompt twice from the same, unmodified context produces byte-identical
    /// output — required so re-injecting the system prompt every turn does not perturb the
    /// conversation.
    /// </summary>
    [Fact]
    public void Build_IsIdempotent_GivenSameContext()
    {
        var ctx = MinimalContext();

        string first = SystemPromptBuilder.Build(ctx);
        string second = SystemPromptBuilder.Build(ctx);

        Assert.Equal(first, second);
    }

    // ── Progressive tool discovery ────────────────────────────────────────────

    /// <summary>
    /// When the tool registry implements progressive discovery, the built prompt instructs the
    /// model to use <c>tool_help</c> to fetch withheld schemas.
    /// </summary>
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

    /// <summary>
    /// When the tool registry is a plain <see cref="ToolRegistry"/> (no progressive discovery),
    /// the built prompt has no <c>tool_help</c> instruction.
    /// </summary>
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

    /// <summary>
    /// When the skill catalog has a model-invocable skill, the built prompt includes a "## Skills"
    /// section listing the skill's name, description, and <c>WhenToUse</c> guidance, plus the
    /// invocation syntax.
    /// </summary>
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

    /// <summary>
    /// A skill with no <c>WhenToUse</c> guidance is listed with just its name and description —
    /// no trailing parenthetical is added.
    /// </summary>
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

    /// <summary>
    /// When the skill catalog is empty, the built prompt has no "## Skills" section.
    /// </summary>
    [Fact]
    public void Build_OmitsSkillsSection_WhenCatalogIsEmpty()
    {
        string result = SystemPromptBuilder.Build(MinimalContext());

        Assert.DoesNotContain("## Skills", result);
    }

    /// <summary>
    /// When every skill in the catalog has <c>DisableModelInvocation</c> set, the built prompt
    /// omits the "## Skills" section entirely and never mentions the disabled skill by name.
    /// </summary>
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

    /// <summary>
    /// When the catalog mixes enabled and model-disabled skills, the "## Skills" section lists
    /// only the enabled skill and excludes the disabled one by name.
    /// </summary>
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
