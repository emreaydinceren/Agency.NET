namespace Agency.Mcp.Memory;

internal static class ToolDescription
{
    internal const string Text = """
Use MemoryTool as a scoped memory system with four core concepts:
•	Scope: ownership boundary for data.
•	UserId: required logical owner.
•	SessionId: optional conversation/task partition under that user.
•	Domain: high-level category of memory (e.g., Work, Home, Health).
•	Key: item identifier within a domain (e.g., Address, ExpensePolicy).
•	Tags: optional multi-label metadata for retrieval/filtering (e.g., ["taxes","family"]).
Storage model
•	Persisted storage key is composed as: "{domain}|{key}".
•	Metadata mirrors:
•	Domain (string)
•	Key (string)
•	Tags (string array, optional)
Tool behaviors
•	Memorize(record): validates Scope, Domain, Key, Value; stores value + metadata.
•	Recall(scope, domain?, key?, tags?):
•	If both Domain and Key are provided, uses exact composite key lookup.
•	If Tags are provided, filters by tags.
•	Returns JSON array of matching hits.
•	Forget(scope, domain, key): deletes by composite key.
•	ListGlobalKeys(scope):
•	Reads metadata entries for the given scope.
•	Returns JSON grouped by domain with distinct:
•	Keys
•	Tags
Guidance for LLM tool calls
•	Always provide correct Scope first.
•	Use stable, meaningful domain names.
•	Keep keys deterministic and human-readable.
•	Add tags for cross-domain retrieval.
•	For broad discovery, call ListGlobalKeys(scope) before Recall(...).
•	Treat returned JSON as source of truth; do not assume fixed metadata runtime types beyond string/string-array semantics.
""";
}
