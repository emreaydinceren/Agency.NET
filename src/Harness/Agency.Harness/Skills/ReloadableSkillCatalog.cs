namespace Agency.Harness.Skills;

/// <summary>
/// An <see cref="ISkillCatalog"/> whose contents can be refreshed from disk at any time via
/// <see cref="Reload"/>. The inner catalog is swapped atomically so callers that hold a reference
/// to this wrapper (e.g. <c>SkillContext</c> and <c>SkillTool</c>) automatically see changes on
/// the next call — no <c>Context</c> rebuild is required.
/// </summary>
internal sealed class ReloadableSkillCatalog : ISkillCatalog
{
    private readonly IReadOnlyList<string> _roots;

    /// <summary>
    /// The currently-active inner catalog. Accessed from multiple threads (watcher fires on a
    /// background thread while the agent loop reads on the request thread), so marked
    /// <see langword="volatile"/> to guarantee the most-recent write is always visible.
    /// </summary>
    private volatile ISkillCatalog _current;

    /// <summary>
    /// Initialises the catalog from the given roots and performs an initial load.
    /// Roots that do not exist on disk are silently skipped (consistent with
    /// <see cref="SkillLoader"/> behaviour).
    /// </summary>
    /// <param name="roots">
    /// Ordered root directories to scan. The caller is responsible for precedence ordering
    /// (project-first, then personal, etc.) — the first occurrence of a skill name wins.
    /// </param>
    internal ReloadableSkillCatalog(IReadOnlyList<string> roots)
    {
        this._roots = roots;
        this._current = SkillLoader.Load(this._roots);
    }

    /// <inheritdoc/>
    public ISkillCatalog Current => this._current;

    /// <inheritdoc/>
    public IReadOnlyList<Skill> List() => this._current.List();

    /// <inheritdoc/>
    public Skill? Find(string name) => this._current.Find(name);

    /// <summary>
    /// Re-scans the skill roots and atomically replaces the inner catalog.
    /// This method is intentionally idempotent and resilient: if the scan throws,
    /// the existing catalog is kept unchanged — no corruption occurs.
    /// Safe to call from any thread.
    /// </summary>
    internal void Reload()
    {
        try
        {
            ISkillCatalog refreshed = SkillLoader.Load(this._roots);
            this._current = refreshed;
        }
        catch (Exception)
        {
            // Keep the existing catalog intact — a failed scan must not corrupt state.
        }
    }
}
