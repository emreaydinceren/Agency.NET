using System.Text.Json.Serialization;

namespace Agency.Harness;

[JsonSerializable(typeof(Dictionary<string, object?>))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
