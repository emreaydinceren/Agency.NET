namespace Agency.Harness.Skills;

/// <summary>
/// Watches one or more skill root directories for changes to <c>SKILL.md</c> files and
/// triggers a debounced reload when additions, edits, removals, or renames are detected.
/// Only roots that exist on disk at construction time are watched; non-existent roots are
/// silently skipped (they may not yet exist and will be picked up on the next
/// <see cref="ReloadableSkillCatalog.Reload"/> if they appear between restarts).
/// </summary>
/// <remarks>
/// Editors typically fire multiple <see cref="FileSystemWatcher"/> events for a single
/// logical save. A short timer-based debounce window (default 300 ms) coalesces rapid
/// bursts into a single <see cref="ReloadableSkillCatalog.Reload"/> call.
/// </remarks>
internal sealed class SkillWatcher : IDisposable
{
    private const int DefaultDebounceMs = 300;

    private readonly Action _onReload;
    private readonly int _debounceMs;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Threading.Timer _debounce;
    private bool _disposed;

    /// <summary>
    /// Initialises the watcher over the given roots, calling <paramref name="onReload"/>
    /// after each debounced change burst. Roots that do not exist on disk are skipped.
    /// </summary>
    /// <param name="roots">The skill root directories to watch.</param>
    /// <param name="onReload">
    /// Action to invoke after the debounce window expires. Typically
    /// <see cref="ReloadableSkillCatalog.Reload"/>.
    /// </param>
    /// <param name="debounceMs">
    /// Milliseconds to wait after the last event before invoking <paramref name="onReload"/>.
    /// Defaults to 300 ms. Injected as a parameter for testability.
    /// </param>
    internal SkillWatcher(IReadOnlyList<string> roots, Action onReload, int debounceMs = DefaultDebounceMs)
    {
        this._onReload = onReload;
        this._debounceMs = debounceMs;
        // The timer starts un-armed (Timeout.Infinite) and is reset on each FS event.
        this._debounce = new System.Threading.Timer(
            static state => ((SkillWatcher)state!)._onReload(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);

        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            FileSystemWatcher watcher = new(root)
            {
                Filter = "SKILL.md",
                IncludeSubdirectories = true,
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            watcher.Changed += this.OnChange;
            watcher.Created += this.OnChange;
            watcher.Deleted += this.OnChange;
            watcher.Renamed += this.OnChange;

            this._watchers.Add(watcher);
        }
    }

    /// <summary>Resets the debounce timer on each filesystem event.</summary>
    private void OnChange(object sender, FileSystemEventArgs e)
    {
        // Reset the timer so the callback fires _debounceMs after the LAST event in a burst.
        this._debounce.Change(this._debounceMs, Timeout.Infinite);
    }

    /// <summary>
    /// Stops all watchers and disposes resources. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }
        this._disposed = true;

        this._debounce.Dispose();

        foreach (FileSystemWatcher watcher in this._watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        this._watchers.Clear();
    }
}
