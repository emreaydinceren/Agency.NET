using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Walker;

/// <summary>
/// Tests for <see cref="LanguageDetector"/>.
/// </summary>
public sealed class LanguageDetectorTests
{
    [Theory]
    [InlineData("file.cs", Language.CSharp)]
    [InlineData("file.ts", Language.TypeScript)]
    [InlineData("file.tsx", Language.Tsx)]
    [InlineData("file.js", Language.JavaScript)]
    [InlineData("file.jsx", Language.Jsx)]
    [InlineData("file.py", Language.Python)]
    public void Detect_KnownExtension_ReturnsExpectedLanguage(string path, Language expected)
    {
        Language actual = LanguageDetector.Detect(path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Detect_UnknownExtension_WithPythonShebang_ReturnsPython()
    {
        const string source = "#!/usr/bin/env python3\nprint('hello')";

        Language actual = LanguageDetector.Detect("script", source);

        Assert.Equal(Language.Python, actual);
    }

    [Theory]
    [InlineData("#!/usr/bin/env node\nconsole.log('hello');", Language.JavaScript)]
    [InlineData("#!/usr/bin/env bun\nconsole.log('hello');", Language.JavaScript)]
    [InlineData("#!/usr/bin/env deno\nconsole.log('hello');", Language.JavaScript)]
    [InlineData("#!/usr/bin/env tsx\nconsole.log('hello');", Language.TypeScript)]
    public void Detect_UnknownExtension_WithSupportedShebang_ReturnsExpectedLanguage(string source, Language expected)
    {
        Language actual = LanguageDetector.Detect("script", source);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("README.md", null)]
    [InlineData("script", null)]
    [InlineData("script", "console.log('hello');")]
    [InlineData("file.go", "#!/usr/bin/env bash\necho hi")]
    public void Detect_UnsupportedInput_ReturnsUnknown(string path, string? source)
    {
        Language actual = LanguageDetector.Detect(path, source);

        Assert.Equal(Language.Unknown, actual);
    }
}
