using System.Text.Json;

namespace Agency.Agentic.Test;

/// <summary>
/// Tests for <see cref="ExecutePowershellTool"/>.
/// </summary>
public sealed class ExecutePowershellToolTests
{
    private static string Quote(string path) => path.Replace("'", "''");

    [Fact]
    public void Definition_ExposesExpectedMetadataAndSchema()
    {
        var tool = new ExecutePowershellTool();

        Assert.Equal("execute_powershell", tool.Definition.Name);
        Assert.Contains("Executes a PowerShell command", tool.Definition.Description);

        JsonElement schema = tool.Definition.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("command", schema.GetProperty("required")[0].GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("command").GetProperty("type").GetString());
    }

    [Fact]
    public async Task InvokeAsync_ReturnsError_WhenCommandIsMissing()
    {
        var tool = new ExecutePowershellTool();

        ToolResult result = await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Command is required", result.Content);
    }


    [Fact]
    public async Task InvokeAsync_UsesGetChildItem_ToList_SingleFile()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string filePath = Path.Combine(tempDir, "sample.txt");
        await File.WriteAllTextAsync(filePath, "hello", cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var tool = new ExecutePowershellTool();
            string script = $"Get-ChildItem -Path '{Quote(tempDir)}' | Select-Object Name, Length";

            ToolResult result = await tool.InvokeAsync(
                JsonSerializer.SerializeToElement(new { command = script }),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Contains("Name", result.Content);
            Assert.Contains("sample.txt", result.Content);
            Assert.Contains("Length", result.Content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_UsesGetChildItem_ToList_Files()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string filePath1 = Path.Combine(tempDir, "sample1.txt");
        string filePath2 = Path.Combine(tempDir, "sample2.txt");
        await File.WriteAllTextAsync(filePath1, "hello", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(filePath2, "hello", cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var tool = new ExecutePowershellTool();
            string script = $"Get-ChildItem -Path '{Quote(tempDir)}' | Select-Object Name, Length";

            ToolResult result = await tool.InvokeAsync(
                JsonSerializer.SerializeToElement(new { command = script }),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Contains("sample1.txt", result.Content);
            Assert.Contains("sample2.txt", result.Content);
            Assert.Contains("Length", result.Content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_UsesGetChildItem_ToList_SingleFileFullName()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string filePath = Path.Combine(tempDir, "sample.txt");
        await File.WriteAllTextAsync(filePath, "hello", cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var tool = new ExecutePowershellTool();
            string script = $"Get-ChildItem -Path '{Quote(tempDir)}' | Select-Object FullName";

            ToolResult result = await tool.InvokeAsync(
                JsonSerializer.SerializeToElement(new { command = script }),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Contains("FullName", result.Content);
            Assert.Contains(filePath, result.Content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_UsesGetChildItem_ToList_FullNames()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string filePath1 = Path.Combine(tempDir, "sample1.txt");
        string filePath2 = Path.Combine(tempDir, "sample2.txt");
        await File.WriteAllTextAsync(filePath1, "hello", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(filePath2, "hello", cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var tool = new ExecutePowershellTool();
            string script = $"Get-ChildItem -Path '{Quote(tempDir)}' | Select-Object FullName";

            ToolResult result = await tool.InvokeAsync(
                JsonSerializer.SerializeToElement(new { command = script }),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Contains("FullName", result.Content);
            Assert.Contains(filePath1, result.Content);
            Assert.Contains(filePath2, result.Content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_UsesGetContent_ToReadFileContents()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string filePath = Path.Combine(tempDir, "content.txt");
        string expectedLine = $"line-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(filePath, expectedLine, cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var tool = new ExecutePowershellTool();
            string script = "Get-Content -Path '" + Quote(filePath) + "' | Select-Object @{Name='Line';Expression={$_}}";
            ToolResult result = await tool.InvokeAsync(
                JsonSerializer.SerializeToElement(new { command = script }),
                CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Contains(expectedLine, result.Content);
            Assert.Contains("Line", result.Content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
