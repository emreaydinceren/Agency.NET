using System.Text.Json;
using Agency.Harness.Permissions;

namespace Agency.Harness.Test.Permissions;

/// <summary>
/// Verifies <see cref="PermissionEvaluator"/> against spec §5 (algorithm), §4.3 (key extraction),
/// §4.4 (proposed rule construction), and §12 (test specification).
/// Covers: precedence (deny > allow > ask), unresolved behavior, kill switch, key-field
/// extraction (config map, built-in defaults, convention fallback, non-string skip, no-key),
/// RecordAlwaysAsync (session grant + file persistence), ctor seeding from local file,
/// malformed local file entries, and concurrency safety.
/// </summary>
public sealed class PermissionEvaluatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns a unique temp path that does not exist yet.</summary>
    private static string MakeTempPath() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

    /// <summary>
    /// Parses a JSON literal and returns its root element as a JsonElement.
    /// The returned element is backed by a live <see cref="JsonDocument"/> — valid for the
    /// lifetime of this call; callers that outlive this scope should clone it.
    /// </summary>
    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement;

    /// <summary>
    /// Builds a <see cref="PermissionEvaluator"/> with a temp local-rules path (so Load never
    /// hits a pre-existing file) and the supplied options overlay, then cleans up on dispose.
    /// </summary>
    private static (PermissionEvaluator Evaluator, string TempPath) MakeEvaluator(
        Action<PermissionsOptions>? configure = null)
    {
        string tempPath = MakeTempPath();
        PermissionsOptions options = new() { LocalRulesPath = tempPath };
        configure?.Invoke(options);
        return (new PermissionEvaluator(options), tempPath);
    }

    // ── Group 1: Precedence ───────────────────────────────────────────────────

    [Fact]
    public void Evaluate_DenyRuleBeatsAllowRule_ReturnsDeny()
    {
        // Both rules match the same call; deny must win.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.Allow = ["ExecutePowershell(git status)"];
            o.Deny = ["ExecutePowershell(git status)"];
        });

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Deny>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_DenyRuleBeatsAllowRule_DenyReasonContainsRawRuleText()
    {
        const string denyRule = "ExecutePowershell(git status)";

        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.Allow = [denyRule];
            o.Deny = [denyRule];
        });

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            PermissionDecision.Deny deny = Assert.IsType<PermissionDecision.Deny>(decision);
            Assert.Equal($"Permission rule '{denyRule}' denies this call.", deny.Reason);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_AllowRuleBeatsAsk_ReturnsAllow()
    {
        // An allow rule must prevent the call from reaching Ask.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.Allow = ["ExecutePowershell(git status*)"];
        });

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Allow>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_Unresolved_ReturnsAsk()
    {
        // No rules at all → unresolved with Ask behavior → Ask.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Ask>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_Unresolved_AskHasCorrectKeyValue()
    {
        // Built-in default maps ExecutePowershell → command field.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("git status", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_Unresolved_AskProposedRuleIsToolNameParenKeyValue()
    {
        // ProposedRule == "ToolName(exactKeyValue)" (spec §4.4).
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("ExecutePowershell(git status)", ask.ProposedRule);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_Unresolved_NoKeyValue_AskKeyValueIsNull()
    {
        // Tool with no recognized string fields → KeyValue is null.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            // UnmappedTool has no built-in key and no convention fields.
            JsonElement input = Json("""{"count":42}""");
            PermissionDecision decision = evaluator.Evaluate("UnmappedTool", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Null(ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_Unresolved_NoKeyValue_AskProposedRuleIsBareName()
    {
        // When no key value exists the proposed rule is just the bare tool name (spec §4.4).
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"count":42}""");
            PermissionDecision decision = evaluator.Evaluate("UnmappedTool", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("UnmappedTool", ask.ProposedRule);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_OnUnresolvedDeny_ReturnsDeny()
    {
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.OnUnresolved = UnresolvedBehavior.Deny;
        });

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Deny>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_OnUnresolvedDeny_DenyReasonIsNoRuleAllows()
    {
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.OnUnresolved = UnresolvedBehavior.Deny;
        });

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            PermissionDecision.Deny deny = Assert.IsType<PermissionDecision.Deny>(decision);
            Assert.Equal("No permission rule allows this call.", deny.Reason);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_EnabledFalse_ReturnsAllowEvenWithMatchingDenyRule()
    {
        // Kill switch (Enabled=false) → Allow before any rule evaluation (spec §5 step 1).
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.Enabled = false;
            o.Deny = ["ExecutePowershell(git status)"];
        });

        try
        {
            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Allow>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── Group 2: Key extraction (§4.3) ───────────────────────────────────────

    [Fact]
    public void Evaluate_ToolInputKeysMapWinsOverBuiltinDefault_MappedFieldUsedForMatch()
    {
        // Override ExecutePowershell → path (instead of built-in default → command).
        // Give input with both command and path fields.
        // An allow rule on path value must match, proving path was used.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.ToolInputKeys["ExecutePowershell"] = "path";
            o.Allow = ["ExecutePowershell(E:/tools/run.ps1)"];
        });

        try
        {
            // command has a different value; path has the value matching the allow rule.
            JsonElement input = Json("""{"command":"git status","path":"E:/tools/run.ps1"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Allow>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_ToolInputKeysMapWinsOverBuiltinDefault_AskKeyValueIsFromMappedField()
    {
        // Override ExecutePowershell → path; unresolved call → Ask.KeyValue must be path value.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.ToolInputKeys["ExecutePowershell"] = "path";
        });

        try
        {
            JsonElement input = Json("""{"command":"git status","path":"E:/tools/run.ps1"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("E:/tools/run.ps1", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_BuiltinDefault_ExecutePowershell_KeyIsCommand()
    {
        // No ToolInputKeys override; ExecutePowershell should use "command" by built-in default.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"command":"git log --oneline"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("git log --oneline", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_BuiltinDefault_ReadFile_KeyIsPath()
    {
        // No ToolInputKeys override; ReadFile should use "path" by built-in default.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"path":"E:/Repos/Agency/README.md"}""");
            PermissionDecision decision = evaluator.Evaluate("ReadFile", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("E:/Repos/Agency/README.md", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_BuiltinDefault_WriteFile_KeyIsPath()
    {
        // No ToolInputKeys override; WriteFile should use "path" by built-in default.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"path":"E:/Repos/Agency/output.txt"}""");
            PermissionDecision decision = evaluator.Evaluate("WriteFile", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("E:/Repos/Agency/output.txt", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // Convention fallback order: command > path > file_path > url (§4.3 step 2).

    [Fact]
    public void Evaluate_ConventionFallback_OnlyUrlPresent_UrlUsedAsKeyValue()
    {
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"url":"https://example.com/api"}""");
            PermissionDecision decision = evaluator.Evaluate("HttpGet", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("https://example.com/api", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_ConventionFallback_PathAndUrlPresent_PathWinsOverUrl()
    {
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"path":"E:/some/file.txt","url":"https://example.com"}""");
            PermissionDecision decision = evaluator.Evaluate("SomeTool", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("E:/some/file.txt", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_ConventionFallback_CommandAndPathPresent_CommandWinsOverPath()
    {
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"command":"run.sh","path":"E:/run.sh"}""");
            PermissionDecision decision = evaluator.Evaluate("SomeTool", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("run.sh", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_ConventionFallback_NonStringCommandField_SkipsToPath()
    {
        // command is a number (non-string) → skipped; path is the fallback.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            JsonElement input = Json("""{"command":42,"path":"E:/some/file.txt"}""");
            PermissionDecision decision = evaluator.Evaluate("SomeTool", input);

            PermissionDecision.Ask ask = Assert.IsType<PermissionDecision.Ask>(decision);
            Assert.Equal("E:/some/file.txt", ask.KeyValue);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_NoKeyFound_ParameterizedAllowRuleDoesNotMatch()
    {
        // No recognized string fields → keyValue = null → parameterized rule must NOT match.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.Allow = ["SomeTool(x*)"];
        });

        try
        {
            JsonElement input = Json("""{"count":5}""");
            PermissionDecision decision = evaluator.Evaluate("SomeTool", input);

            // Must NOT be Allow — parameterized rule cannot match a null key value.
            Assert.IsNotType<PermissionDecision.Allow>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Evaluate_NoKeyFound_BareAllowRuleMatches()
    {
        // A bare rule must match even when there is no key value.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.Allow = ["SomeTool"];
        });

        try
        {
            JsonElement input = Json("""{"count":5}""");
            PermissionDecision decision = evaluator.Evaluate("SomeTool", input);

            Assert.IsType<PermissionDecision.Allow>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── Group 3: RecordAlwaysAsync ────────────────────────────────────────────

    [Fact]
    public async Task RecordAlwaysAsync_AllowGrant_SubsequentEvaluateReturnsAllow()
    {
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            await evaluator.RecordAlwaysAsync(
                "ExecutePowershell(git status)",
                deny: false,
                TestContext.Current.CancellationToken);

            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Allow>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task RecordAlwaysAsync_AllowGrant_WritesRuleToLocalFile()
    {
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator();

        try
        {
            await evaluator.RecordAlwaysAsync(
                "ExecutePowershell(git status)",
                deny: false,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(tempPath), "Local rules file must be created by RecordAlwaysAsync.");

            string json = await File.ReadAllTextAsync(tempPath, TestContext.Current.CancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement allow = doc.RootElement.GetProperty("Allow");
            Assert.Contains(
                allow.EnumerateArray(),
                el => el.GetString() == "ExecutePowershell(git status)");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task RecordAlwaysAsync_DenyGrant_BeatsConfigAllowRule_ReturnsDeny()
    {
        // Config has an allow rule; a deny grant for the same call must override it.
        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.Allow = ["ExecutePowershell(git status*)"];
        });

        try
        {
            await evaluator.RecordAlwaysAsync(
                "ExecutePowershell(git status)",
                deny: true,
                TestContext.Current.CancellationToken);

            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Deny>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── Group 4: Ctor seeding from local file ─────────────────────────────────

    [Fact]
    public void Ctor_LocalFileWithAllowEntry_GrantIsLiveOnEvaluate()
    {
        string tempPath = MakeTempPath();

        try
        {
            // Pre-write a local rules file before constructing the evaluator.
            File.WriteAllText(
                tempPath,
                """{"Allow":["ExecutePowershell(git status)"],"Deny":[]}""");

            PermissionsOptions options = new() { LocalRulesPath = tempPath };
            PermissionEvaluator evaluator = new(options);

            JsonElement input = Json("""{"command":"git status"}""");
            PermissionDecision decision = evaluator.Evaluate("ExecutePowershell", input);

            Assert.IsType<PermissionDecision.Allow>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Ctor_LocalFileWithDenyEntry_GrantIsLiveOnEvaluate()
    {
        string tempPath = MakeTempPath();

        try
        {
            File.WriteAllText(
                tempPath,
                """{"Allow":[],"Deny":["WriteFile(E:/secrets/**)"] }""");

            PermissionsOptions options = new() { LocalRulesPath = tempPath };
            PermissionEvaluator evaluator = new(options);

            JsonElement input = Json("""{"path":"E:\\secrets\\api.key"}""");
            PermissionDecision decision = evaluator.Evaluate("WriteFile", input);

            Assert.IsType<PermissionDecision.Deny>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Ctor_LocalFileWithMalformedEntry_DoesNotThrow()
    {
        string tempPath = MakeTempPath();

        try
        {
            // Mix a valid and a malformed entry.
            File.WriteAllText(
                tempPath,
                """{"Allow":["ReadFile","BadRule("],"Deny":[]}""");

            PermissionsOptions options = new() { LocalRulesPath = tempPath };

            Exception? ex = Record.Exception(() => _ = new PermissionEvaluator(options));

            Assert.Null(ex);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Ctor_LocalFileWithMalformedEntry_ValidGrantsAreStillApplied()
    {
        string tempPath = MakeTempPath();

        try
        {
            // ReadFile is valid; BadRule( is malformed and must be skipped.
            File.WriteAllText(
                tempPath,
                """{"Allow":["ReadFile","BadRule("],"Deny":[]}""");

            PermissionsOptions options = new() { LocalRulesPath = tempPath };
            PermissionEvaluator evaluator = new(options);

            JsonElement input = Json("""{"path":"E:/Repos/Agency/src/file.cs"}""");
            PermissionDecision decision = evaluator.Evaluate("ReadFile", input);

            // Bare "ReadFile" allow rule → Allow (even though a bad entry was also present).
            Assert.IsType<PermissionDecision.Allow>(decision);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── Group 5: Concurrency ─────────────────────────────────────────────────

    [Fact]
    public void Evaluate_ParallelCalls_NoExceptionsAndDeterministicResults()
    {
        // Construct one evaluator with predictable config rules, then run many parallel
        // Evaluate calls mixing allowed, denied, and unresolved inputs.
        // Every result must be deterministic for its input and no exceptions must escape.

        (PermissionEvaluator evaluator, string tempPath) = MakeEvaluator(o =>
        {
            o.Allow = ["ReadFile"];
            o.Deny = ["WriteFile(E:/secrets/**)"];
        });

        try
        {
            const int Iterations = 100;
            Exception? caught = null;

            Parallel.For(0, Iterations, i =>
            {
                try
                {
                    int bucket = i % 3;

                    switch (bucket)
                    {
                        case 0:
                        {
                            // Should always Allow (bare ReadFile rule matches).
                            JsonElement input = Json("""{"path":"E:/docs/readme.txt"}""");
                            PermissionDecision d = evaluator.Evaluate("ReadFile", input);
                            if (d is not PermissionDecision.Allow)
                            {
                                throw new Exception($"Expected Allow at i={i}, got {d}");
                            }

                            break;
                        }

                        case 1:
                        {
                            // Should always Deny (WriteFile deny rule).
                            JsonElement input = Json("""{"path":"E:\\secrets\\api.key"}""");
                            PermissionDecision d = evaluator.Evaluate("WriteFile", input);
                            if (d is not PermissionDecision.Deny)
                            {
                                throw new Exception($"Expected Deny at i={i}, got {d}");
                            }

                            break;
                        }

                        case 2:
                        {
                            // Should always Ask (no matching rule for this tool).
                            JsonElement input = Json("""{"command":"git status"}""");
                            PermissionDecision d = evaluator.Evaluate("ExecutePowershell", input);
                            if (d is not PermissionDecision.Ask)
                            {
                                throw new Exception($"Expected Ask at i={i}, got {d}");
                            }

                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Capture first failure; Parallel.For swallows per-thread exceptions.
                    Interlocked.CompareExchange(ref caught, ex, null);
                }
            });

            Assert.Null(caught);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
