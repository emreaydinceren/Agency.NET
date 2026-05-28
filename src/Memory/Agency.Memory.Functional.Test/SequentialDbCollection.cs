namespace Agency.Memory.Functional.Test;

/// <summary>
/// xUnit collection marker that forces all tests in the collection to run sequentially,
/// preventing concurrent Postgres schema resets that would cause
/// "relation records does not exist" or pgvector duplicate-type errors when tests
/// run in parallel and share the same database.
/// </summary>
[CollectionDefinition("memory-db", DisableParallelization = true)]
public sealed class SequentialDbCollection;
