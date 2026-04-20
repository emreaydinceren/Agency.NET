using System.Text.Json.Serialization;

namespace Agency.Agentic;

[JsonSerializable(typeof(Dictionary<string, object?>))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
