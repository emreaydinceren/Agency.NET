using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agency.Memory.Retrieval.Test")]
[assembly: InternalsVisibleTo("Agency.Memory.Distiller")]
// Required by Agency.Memory.Functional.Test to invoke RetrievalEngine and RetrievalGate
// directly for the G.1 end-to-end and G.3 latency tests (IQ-4).
[assembly: InternalsVisibleTo("Agency.Memory.Functional.Test")]
