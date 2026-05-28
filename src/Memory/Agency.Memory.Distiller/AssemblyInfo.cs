using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agency.Memory.Distiller.Test")]
// Required by Agency.Memory.Functional.Test to implement ILlmClientAdapter stubs
// and wire stub/real LLM clients into the AddAgencyMemory DI registration (IQ-3).
[assembly: InternalsVisibleTo("Agency.Memory.Functional.Test")]
