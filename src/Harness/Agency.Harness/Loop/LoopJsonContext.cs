using System.Text.Json.Serialization;

using Agency.Harness;

namespace Agency.Harness.Loop;

/// <summary>
/// Source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> for all
/// Loop Kit domain types. Using this context keeps serialization AOT/trim-safe — no reflection
/// fallback at runtime.
/// </summary>
[JsonSerializable(typeof(GoalSpec))]
[JsonSerializable(typeof(Verdict))]
[JsonSerializable(typeof(Verdict.Continue))]
[JsonSerializable(typeof(Verdict.Done))]
[JsonSerializable(typeof(LoopOutcome))]
[JsonSerializable(typeof(GoalSetEvent))]
[JsonSerializable(typeof(TurnStartedEvent))]
[JsonSerializable(typeof(VerdictEvent))]
[JsonSerializable(typeof(LoopResultEvent))]
[JsonSerializable(typeof(LlmTokenUsage))]
internal sealed partial class LoopJsonContext : JsonSerializerContext;
