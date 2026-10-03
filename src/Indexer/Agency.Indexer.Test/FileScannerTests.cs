namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="FileScanner"/>.</summary>
public sealed class FileScannerTests
{
    /// <summary>Verifies extension and name selection, skipped folders and the size cap.</summary>
    [Fact]
    public void Scan_DefaultSelection_PicksDocumentationOnly()
    {
        using var dir = new TempDirectory();
        dir.Write("guide.md", "# Guide");
        dir.Write("notes/a.TXT", "text");
        dir.Write("site/index.html", "<p>hi</p>");
        dir.Write("README", "readme");
        dir.Write("config.yaml", "a: 1");
        dir.Write("node_modules/pkg/readme.md", "dependency");
        dir.Write(".git/HEAD.md", "git");
        dir.Write("bin/out.md", "build output");
        dir.Write("big.md", new string('x', 2048));

        ScanResult result = FileScanner.Scan(new ScanOptions(dir.Path, FileScanner.DefaultExtensions, FileScanner.DefaultNames, 1024));

        string[] relative = result.Files.Select(f => Path.GetRelativePath(dir.Path, f.Path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["README", "guide.md", "notes/a.TXT", "site/index.html"], relative);
        Assert.Equal([Path.Combine(dir.Path, "big.md")], result.SkippedTooLarge);
    }

    /// <summary>Verifies that extension lists are normalized to lower-case, dot-prefixed, distinct entries.</summary>
    [Fact]
    public void ParseExtensions_NormalizesEntries()
    {
        Assert.Equal([".md", ".html", ".txt"], FileScanner.ParseExtensions("md, .HTML ,.md,, txt"));
    }
}
