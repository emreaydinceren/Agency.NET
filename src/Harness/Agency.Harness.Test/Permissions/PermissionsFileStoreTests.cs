using System.Text.Json;
using Agency.Harness.Permissions;

namespace Agency.Harness.Test.Permissions;

/// <summary>
/// Verifies <see cref="PermissionsFileStore"/> Load/Append semantics: missing-file tolerance,
/// file creation, deny routing, duplicate skip, round-trip, exclusive-lock retry/backoff,
/// malformed-entry tolerance, and malformed-JSON tolerance.
/// </summary>
public sealed class PermissionsFileStoreTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns a unique temp path that does not exist yet and cleans it up after use.</summary>
    private static string MakeTempPath() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

    // ── 1. Load missing file ──────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsEmptyAllowList()
    {
        string path = MakeTempPath();
        // deliberately do not create the file

        PermissionsFileStore store = new(path);
        (List<PermissionRule> allow, List<PermissionRule> _) = store.Load();

        Assert.Empty(allow);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyDenyList()
    {
        string path = MakeTempPath();

        PermissionsFileStore store = new(path);
        (List<PermissionRule> _, List<PermissionRule> deny) = store.Load();

        Assert.Empty(deny);
    }

    [Fact]
    public void Load_MissingFile_DoesNotThrow()
    {
        string path = MakeTempPath();

        PermissionsFileStore store = new(path);

        Exception? ex = Record.Exception((Action)(() => { store.Load(); }));

        Assert.Null(ex);
    }

    [Fact]
    public void Load_MissingFile_DoesNotCreateFile()
    {
        string path = MakeTempPath();

        PermissionsFileStore store = new(path);
        store.Load();

        Assert.False(File.Exists(path));
    }

    // ── 2. Append creates file ────────────────────────────────────────────────

    [Fact]
    public void Append_DenyFalse_CreatesFileWhenMissing()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("ExecutePowershell(git status)", deny: false);

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Append_DenyFalse_WritesRuleToAllowArray()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("ExecutePowershell(git status)", deny: false);

            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement allow = doc.RootElement.GetProperty("Allow");
            Assert.Equal(JsonValueKind.Array, allow.ValueKind);
            Assert.Contains(
                allow.EnumerateArray(),
                el => el.GetString() == "ExecutePowershell(git status)");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Append_DenyFalse_DocumentHasDenyProperty()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("ExecutePowershell(git status)", deny: false);

            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            Assert.True(doc.RootElement.TryGetProperty("Deny", out JsonElement deny));
            Assert.Equal(JsonValueKind.Array, deny.ValueKind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Append_DenyFalse_DenyArrayIsEmpty()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("ExecutePowershell(git status)", deny: false);

            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement deny = doc.RootElement.GetProperty("Deny");
            Assert.Equal(0, deny.GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 3. Append deny:true routes to Deny array ──────────────────────────────

    [Fact]
    public void Append_DenyTrue_WritesRuleToDenyArray()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("WriteFile(E:/secrets/**)", deny: true);

            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement deny = doc.RootElement.GetProperty("Deny");
            Assert.Contains(
                deny.EnumerateArray(),
                el => el.GetString() == "WriteFile(E:/secrets/**)");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Append_DenyTrue_AllowArrayIsEmpty()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("WriteFile(E:/secrets/**)", deny: true);

            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement allow = doc.RootElement.GetProperty("Allow");
            Assert.Equal(0, allow.GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 4. Duplicate append is a no-op ────────────────────────────────────────

    [Fact]
    public void Append_DuplicateAllowRule_AllowArrayHasExactlyOneEntry()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("ReadFile", deny: false);
            store.Append("ReadFile", deny: false);

            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement allow = doc.RootElement.GetProperty("Allow");
            Assert.Equal(1, allow.GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Append_DuplicateDenyRule_DenyArrayHasExactlyOneEntry()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("WriteFile(E:/secrets/**)", deny: true);
            store.Append("WriteFile(E:/secrets/**)", deny: true);

            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement deny = doc.RootElement.GetProperty("Deny");
            Assert.Equal(1, deny.GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 5. Load round-trip ────────────────────────────────────────────────────

    [Fact]
    public void Load_AfterAppendAllow_ReturnsMatchingRawValue()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("ExecutePowershell(git status)", deny: false);

            (List<PermissionRule> allow, List<PermissionRule> _) = store.Load();

            Assert.Single(allow);
            Assert.Equal("ExecutePowershell(git status)", allow[0].Raw);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_AfterAppendDeny_ReturnsMatchingRawValue()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("WriteFile(E:/secrets/**)", deny: true);

            (List<PermissionRule> _, List<PermissionRule> deny) = store.Load();

            Assert.Single(deny);
            Assert.Equal("WriteFile(E:/secrets/**)", deny[0].Raw);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_AfterMultipleAppends_ReturnsAllRules()
    {
        string path = MakeTempPath();
        try
        {
            PermissionsFileStore store = new(path);
            store.Append("ReadFile", deny: false);
            store.Append("ExecutePowershell(git status)", deny: false);
            store.Append("WriteFile(E:/secrets/**)", deny: true);

            (List<PermissionRule> allow, List<PermissionRule> deny) = store.Load();

            Assert.Equal(2, allow.Count);
            Assert.Single(deny);
            Assert.Contains(allow, r => r.Raw == "ReadFile");
            Assert.Contains(allow, r => r.Raw == "ExecutePowershell(git status)");
            Assert.Contains(deny, r => r.Raw == "WriteFile(E:/secrets/**)");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 6. Contention: backoff/retry succeeds after exclusive handle released ──

    [Fact]
    public async Task Append_WhileExclusiveHandleHeld_SucceedsAfterHandleReleased()
    {
        // Strategy: hold an exclusive FileStream for ~175 ms, then release it.
        // Append runs concurrently on a background task.  The spec allows ~10 attempts
        // at ~50 ms backoff each (~500 ms window), so a 5 s Task timeout is generous
        // and the test stays fast since the handle is released well before any retry
        // budget is exhausted.

        string path = MakeTempPath();

        // Create the file so OpenOrCreate doesn't race with a truly-absent path.
        await File.WriteAllTextAsync(
            path,
            """{"Allow":[],"Deny":[]}""",
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // Open an exclusive handle.
            using FileStream exclusiveHandle = new(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            PermissionsFileStore store = new(path);

            // Start Append on a background thread while the handle is held.
            Task appendTask = Task.Run(() => store.Append("ReadFile", deny: false));

            // Hold the exclusive handle for ~175 ms to force at least one retry.
            await Task.Delay(175, TestContext.Current.CancellationToken);

            // Release the handle.
            exclusiveHandle.Dispose();

            // Append must complete within 5 s of handle release.
            bool completed = await Task
                .WhenAny(appendTask, Task.Delay(5_000, TestContext.Current.CancellationToken))
                == appendTask;

            Assert.True(completed, "Append did not complete within 5 s after exclusive handle was released.");

            // Propagate any exception thrown by Append.
            await appendTask;

            // Verify the file content is correct.
            string json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement allow = doc.RootElement.GetProperty("Allow");
            Assert.Contains(
                allow.EnumerateArray(),
                el => el.GetString() == "ReadFile");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 7. Malformed entries skipped on Load ──────────────────────────────────

    [Fact]
    public void Load_MalformedEntry_DoesNotThrow()
    {
        string path = MakeTempPath();
        try
        {
            // Write a file containing one valid rule and one malformed rule.
            File.WriteAllText(path, """{"Allow":["ReadFile","Tool("],"Deny":[]}""");

            PermissionsFileStore store = new(path);

            Exception? ex = Record.Exception((Action)(() => { store.Load(); }));

            Assert.Null(ex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MalformedEntry_ReturnsOnlyValidRules()
    {
        string path = MakeTempPath();
        try
        {
            // "Tool(" is malformed per PermissionRule (unclosed paren).
            File.WriteAllText(path, """{"Allow":["ReadFile","Tool("],"Deny":[]}""");

            PermissionsFileStore store = new(path);
            (List<PermissionRule> allow, List<PermissionRule> _) = store.Load();

            // Only "ReadFile" is valid; "Tool(" must be silently skipped.
            Assert.Single(allow);
            Assert.Equal("ReadFile", allow[0].Raw);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MalformedDenyEntry_ReturnsOnlyValidDenyRules()
    {
        string path = MakeTempPath();
        try
        {
            // Mix one valid deny rule and one malformed one.
            File.WriteAllText(path, """{"Allow":[],"Deny":["WriteFile(E:/secrets/**)","Bad("]}""");

            PermissionsFileStore store = new(path);
            (List<PermissionRule> _, List<PermissionRule> deny) = store.Load();

            Assert.Single(deny);
            Assert.Equal("WriteFile(E:/secrets/**)", deny[0].Raw);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── 8. Malformed JSON document ────────────────────────────────────────────

    [Fact]
    public void Load_MalformedJsonDocument_DoesNotThrow()
    {
        string path = MakeTempPath();
        try
        {
            File.WriteAllText(path, "not json");

            PermissionsFileStore store = new(path);

            Exception? ex = Record.Exception((Action)(() => { store.Load(); }));

            Assert.Null(ex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MalformedJsonDocument_ReturnsEmptyLists()
    {
        string path = MakeTempPath();
        try
        {
            File.WriteAllText(path, "not json");

            PermissionsFileStore store = new(path);
            (List<PermissionRule> allow, List<PermissionRule> deny) = store.Load();

            Assert.Empty(allow);
            Assert.Empty(deny);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyFile_DoesNotThrow()
    {
        string path = MakeTempPath();
        try
        {
            // An empty file is not valid JSON — also covered by the malformed-JSON guarantee.
            File.WriteAllText(path, "");

            PermissionsFileStore store = new(path);

            Exception? ex = Record.Exception((Action)(() => { store.Load(); }));

            Assert.Null(ex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyLists()
    {
        string path = MakeTempPath();
        try
        {
            File.WriteAllText(path, "");

            PermissionsFileStore store = new(path);
            (List<PermissionRule> allow, List<PermissionRule> deny) = store.Load();

            Assert.Empty(allow);
            Assert.Empty(deny);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
