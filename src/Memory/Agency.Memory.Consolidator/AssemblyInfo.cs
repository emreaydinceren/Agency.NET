using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agency.Memory.Consolidator.Test")]
// Required by Agency.Memory.Functional.Test to invoke ConsolidatorSubAgentFactory.CreateRunner
// directly for G.5 contradiction-merge test.
[assembly: InternalsVisibleTo("Agency.Memory.Functional.Test")]
