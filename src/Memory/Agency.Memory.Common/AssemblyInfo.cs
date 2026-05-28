using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agency.Memory.Common.Test")]
[assembly: InternalsVisibleTo("Agency.Memory.Retrieval")]
[assembly: InternalsVisibleTo("Agency.Memory.Distiller")]
// Required by Agency.Memory.Functional.Test to instantiate InMemoryEventBus for test bus
// and to access internal infrastructure directly in G.4 crash-recovery tests.
[assembly: InternalsVisibleTo("Agency.Memory.Functional.Test")]
