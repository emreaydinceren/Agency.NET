namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="DeltaPlanner"/>.</summary>
public sealed class DeltaPlannerTests
{
    /// <summary>Verifies the four outcomes — added, changed by size, changed by mtime, removed — and the unchanged count.</summary>
    [Fact]
    public void Plan_ClassifiesEveryFile()
    {
        ScannedFile[] disk =
        [
            new("/r/same.md", 10, 100),
            new("/r/new.md", 5, 100),
            new("/r/size.md", 11, 100),
            new("/r/mtime.md", 10, 200),
        ];
        ManifestEntry[] manifest =
        [
            new("/r/same.md", 10, 100, 1),
            new("/r/size.md", 10, 100, 1),
            new("/r/mtime.md", 10, 100, 1),
            new("/r/gone.md", 10, 100, 1),
        ];

        IndexPlan plan = DeltaPlanner.Plan(disk, manifest);

        Assert.Equal(["/r/new.md"], plan.Added.Select(f => f.Path));
        Assert.Equal(["/r/size.md", "/r/mtime.md"], plan.Changed.Select(f => f.Path));
        Assert.Equal(["/r/gone.md"], plan.Removed);
        Assert.Equal(1, plan.Unchanged);
    }

    /// <summary>Verifies that an empty manifest makes every file an addition.</summary>
    [Fact]
    public void Plan_EmptyManifest_AddsEverything()
    {
        IndexPlan plan = DeltaPlanner.Plan([new("/r/a.md", 1, 1), new("/r/b.md", 1, 1)], []);

        Assert.Equal(2, plan.Added.Count);
        Assert.Empty(plan.Changed);
        Assert.Empty(plan.Removed);
    }
}
