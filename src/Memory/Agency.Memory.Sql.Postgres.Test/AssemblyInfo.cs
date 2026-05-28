using Xunit;

// Database tests must run sequentially to avoid table-truncation races between test classes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
