using System.Text.Json;

namespace Agency.Harness.Skills;

/// <summary>
/// Delegate that spawns a subagent with the given prompt and optional agent type,
/// and returns the subagent's final text output.
/// </summary>
/// <param name="prompt">The rendered skill body to use as the subagent's prompt.</param>
/// <param name="agentType">Optional agent type hint from the skill's <c>agent</c> frontmatter field.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>The subagent's final text result.</returns>
internal delegate Task<string> SkillForkRunner(string prompt, string? agentType, CancellationToken ct);

/// <summary>
/// A meta-tool that loads a named skill's instructions into the conversation on demand.
/// The model calls this tool with a skill name (and optional arguments string) to receive
/// the rendered skill body as a <see cref="ToolResult"/>.
/// </summary>
internal sealed class SkillTool : ITool
{
    /// <summary>The tool name registered with the tool registry and used to identify the skill meta-tool.</summary>
    internal const string ToolName = "skill";

    private readonly ISkillCatalog _catalog;
    private readonly string _sessionId;
    private readonly ISkillShellRunner? _shellRunner;
    private readonly bool _disableShellExecution;
    private readonly SkillForkRunner? _forkRunner;

    private static readonly JsonElement InputSchema = JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""name"": {
                ""type"": ""string"",
                ""description"": ""The canonical name of the skill to invoke (the directory name).""
            },
            ""arguments"": {
                ""type"": ""string"",
                ""description"": ""Optional arguments passed to the skill for placeholder substitution.""
            }
        },
        ""required"": [""name""]
    }").RootElement.Clone();

    /// <summary>
    /// Initialises the <see cref="SkillTool"/> with the given catalog, optional session-id, optional
    /// shell-expansion runner, and optional fork runner for <c>context: fork</c> skills.
    /// </summary>
    /// <param name="catalog">The skill catalog used to resolve skill names.</param>
    /// <param name="sessionId">
    /// The current session identifier, substituted for <c>${CLAUDE_SESSION_ID}</c> in skill bodies.
    /// Defaults to an empty string; the Console wiring supplies the real value.
    /// </param>
    /// <param name="shellRunner">
    /// Optional shell runner used for <c>!</c>-directive expansion after the pure render.
    /// When <see langword="null"/> (the default) shell expansion is skipped, preserving Phase-1 behaviour.
    /// </param>
    /// <param name="disableShellExecution">
    /// When <see langword="true"/>, shell execution is suppressed even if <paramref name="shellRunner"/>
    /// is provided — directives are left verbatim in the output. Corresponds to
    /// <c>Skills:DisableShellExecution</c> in <c>appsettings.json</c>.
    /// </param>
    /// <param name="forkRunner">
    /// Optional delegate that spawns a subagent for skills that declare <c>context: fork</c>.
    /// When <see langword="null"/> and a skill declares <c>context: fork</c>, the rendered body
    /// is returned inline (safe fallback — no exception is thrown).
    /// </param>
    internal SkillTool(
        ISkillCatalog catalog,
        string sessionId = "",
        ISkillShellRunner? shellRunner = null,
        bool disableShellExecution = false,
        SkillForkRunner? forkRunner = null)
    {
        this._catalog = catalog;
        this._sessionId = sessionId;
        this._shellRunner = shellRunner;
        this._disableShellExecution = disableShellExecution;
        this._forkRunner = forkRunner;
    }

    public ToolDefinition Definition => new(
        ToolName,
        "Invoke a skill by name; loads its instructions into the conversation. " +
        "Pass the canonical skill name in 'name' and an optional argument string in 'arguments'.",
        InputSchema);

    public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        if (!input.TryGetProperty("name", out JsonElement nameEl) ||
            nameEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(nameEl.GetString()))
        {
            return new ToolResult("Parameter 'name' is required.", IsError: true);
        }

        string name = nameEl.GetString()!;

        string? arguments = null;
        if (input.TryGetProperty("arguments", out JsonElement argsEl) &&
            argsEl.ValueKind == JsonValueKind.String)
        {
            arguments = argsEl.GetString();
        }

        Skill? skill = this._catalog.Find(name);

        if (skill is null)
        {
            string available = string.Join(", ", this._catalog.List()
                .Where(static s => !s.DisableModelInvocation)
                .Select(static s => s.Name));
            return new ToolResult(
                $"No skill named '{name}'. Available skills: {available}",
                IsError: true);
        }

        if (skill.DisableModelInvocation)
        {
            return new ToolResult(
                $"Skill '{name}' cannot be invoked by the model (disable-model-invocation is set).",
                IsError: true);
        }

        string rendered = SkillRenderer.Render(skill, arguments, this._sessionId);
        string body = await SkillRenderer.ExpandShellAsync(rendered, this._shellRunner, this._disableShellExecution, ct).ConfigureAwait(false);

        // context: fork — delegate to the subagent runner when one is wired.
        // If no runner is available, fall back to returning the body inline (safe degradation).
        if (string.Equals(skill.Context, "fork", StringComparison.OrdinalIgnoreCase) &&
            this._forkRunner is not null)
        {
            string forkResult = await this._forkRunner(body, skill.Agent, ct).ConfigureAwait(false);
            return new ToolResult(forkResult, IsError: false);
        }

        return new ToolResult(body, IsError: false);
    }
}
