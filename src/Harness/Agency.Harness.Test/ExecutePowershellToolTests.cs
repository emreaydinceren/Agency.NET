using System.Text.Json;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for <see cref="ExecutePowershellTool"/>.
/// </summary>
public sealed class ExecutePowershellToolTests
{
    private static string Quote(string path) => path.Replace("'", "''");

    /// <summary>
    /// The tool's <c>Definition</c> exposes the expected name, a description mentioning that it
    /// executes a PowerShell command, and an input schema requiring a string <c>command</c>
    /// property.
    /// </summary>
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

    /// <summary>
    /// Invoking with an empty arguments object returns an error result naming the missing
    /// <c>command</c> argument.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ReturnsError_WhenCommandIsMissing()
    {
        var tool = new ExecutePowershellTool();

        ToolResult result = await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'command'", result.Content);
    }

    /// <summary>
    /// When exactly one string argument is supplied under any key (e.g. <c>path</c> instead of
    /// <c>command</c>), the tool treats it as unambiguous and executes it as the command.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AcceptsSingleMisnamedStringArg_AsCommand()
    {
        // Weak models often send the command under 'path' (borrowed from read_file/write_file) and
        // then fail to self-correct. A single string argument is unambiguous, so it must execute.
        var tool = new ExecutePowershellTool();
        string expected = $"misnamed-{Guid.NewGuid():N}";

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { path = $"Write-Output '{expected}'" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains(expected, result.Content);
    }

    /// <summary>
    /// When multiple string arguments are supplied and none is named <c>command</c>, the tool
    /// cannot disambiguate and returns an error listing all the received argument keys.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ReturnsError_EchoesReceivedKeys_WhenArgsAreAmbiguous()
    {
        // Multiple string arguments are ambiguous — fall back to the self-correcting error that
        // names the received keys rather than guessing which one is the command.
        var tool = new ExecutePowershellTool();

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { path = "Get-Process", script = "Get-Date" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'command'", result.Content);
        Assert.Contains("'path'", result.Content);
        Assert.Contains("'script'", result.Content);
    }


    /// <summary>
    /// Running <c>Get-ChildItem | Select-Object Name, Length</c> against a directory with one
    /// file renders both the file's name and length in the result.
    /// </summary>
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

    /// <summary>
    /// Running <c>Get-ChildItem | Select-Object Name, Length</c> against a directory with
    /// multiple files renders every file's name and length in the result.
    /// </summary>
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

    /// <summary>
    /// Running <c>Get-ChildItem | Select-Object FullName</c> against a directory with one file
    /// renders that file's full path in the result.
    /// </summary>
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

    /// <summary>
    /// Running <c>Get-ChildItem | Select-Object FullName</c> against a directory with multiple
    /// files renders every file's full path in the result.
    /// </summary>
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

    /// <summary>
    /// Selecting a single process's <c>ExitCode</c> property does not throw even though reading
    /// it on a running process raises internally — the renderer must skip the failing property
    /// and still emit the rest.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_GetProcess_SingleRunningProcess_RendersWithoutThrowing()
    {
        // Get-Process exposes ExitCode, which throws GetValueInvocationException when read on a
        // still-running process. The tool must skip that property and still render the rest.
        var tool = new ExecutePowershellTool();
        string script = "Get-Process -Id $PID | Select-Object Id, ProcessName, ExitCode";

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = script }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Id", result.Content);
        Assert.Contains("ProcessName", result.Content);
    }

    /// <summary>
    /// Rendering multiple processes as a markdown table tolerates the same throwing
    /// <c>ExitCode</c> property per-cell without failing the whole table.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_GetProcess_MultipleRunningProcesses_RendersTableWithoutThrowing()
    {
        // Multiple objects exercise the ToMarkdownTable path, where each row reads ExitCode and
        // must tolerate the per-cell exception without failing the whole table.
        var tool = new ExecutePowershellTool();
        string script = "Get-Process | Select-Object -First 5 Id, ProcessName, ExitCode";

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = script }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("ExitCode", result.Content);
        Assert.Contains("ProcessName", result.Content);
    }

    /// <summary>
    /// A bare <c>Get-Process</c> with no <c>Select-Object</c> projection keeps the type's default
    /// display property set, so the throwing <c>ExitTime</c>/<c>ExitCode</c> getters are never
    /// probed and excluded from the output.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_GetProcess_BareSingleObject_RendersDefaultDisplayColumns()
    {
        // A bare Get-Process (no Select-Object projection) keeps the Process type's
        // DefaultDisplayPropertySet — Id, Handles, CPU, SI, Name. Honoring it means the throwing
        // ExitTime/ExitCode getters are never probed, so the output is clean and console-native.
        var tool = new ExecutePowershellTool();

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = "Get-Process -Id $PID" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Handles", result.Content);
        Assert.Contains("Name", result.Content);
        Assert.DoesNotContain("ExitTime", result.Content);
    }

    /// <summary>
    /// <c>Select-Object -First</c> without a property list passes the original process objects
    /// through, so the table renderer also honours the default display set and omits
    /// <c>ExitTime</c>/<c>ExitCode</c>.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_GetProcess_BareMultipleObjects_RendersDefaultDisplayColumns()
    {
        // Select-Object -First (no property list) passes the original Process objects through, so the
        // table path also sees the default display set and omits ExitTime/ExitCode entirely.
        var tool = new ExecutePowershellTool();

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = "Get-Process | Select-Object -First 5" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Handles", result.Content);
        Assert.Contains("Name", result.Content);
        Assert.DoesNotContain("ExitTime", result.Content);
    }

    /// <summary>
    /// Bare strings emitted like native command output are rendered as their text content, never
    /// as a table with a spurious <c>Length</c> column.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_NativeCommandStrings_RendersTextNotLengthColumn()
    {
        // Native commands (tasklist, git, …) emit bare strings; Write-Output of several strings
        // reproduces that. The result must contain the text, never a "Length" property table.
        var tool = new ExecutePowershellTool();
        string script = "Write-Output 'alpha-line','beta-line','gamma-line'";

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = script }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("alpha-line", result.Content);
        Assert.Contains("beta-line", result.Content);
        Assert.Contains("gamma-line", result.Content);
        Assert.DoesNotContain("Length", result.Content);
    }

    /// <summary>
    /// A single bare string result is rendered as its text content, not as a bulleted list item
    /// exposing a <c>Length</c> property.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_SingleNativeCommandString_RendersTextNotLengthBullet()
    {
        var tool = new ExecutePowershellTool();
        string expected = $"single-{Guid.NewGuid():N}";
        string script = $"Write-Output '{expected}'";

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = script }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains(expected, result.Content);
        Assert.DoesNotContain("Length", result.Content);
    }

    /// <summary>
    /// Running <c>Get-Content</c> projected through a calculated property renders the file's
    /// contents and the projected column name in the result.
    /// </summary>
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
