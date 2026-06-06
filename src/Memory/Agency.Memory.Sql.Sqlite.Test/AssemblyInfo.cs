// Each test uses a uniquely-named in-memory DB so parallel execution is safe.
// We leave parallelization enabled intentionally.
[assembly: CollectionBehavior(DisableTestParallelization = false)]