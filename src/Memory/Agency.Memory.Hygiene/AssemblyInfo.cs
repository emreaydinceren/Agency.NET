using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agency.Memory.Hygiene.Test")]
// Required by Agency.Memory.Functional.Test to boot HygieneSweeperBackgroundService
// and substitute a FakeTimeProvider for time-controlled sweep tests (TI-2).
[assembly: InternalsVisibleTo("Agency.Memory.Functional.Test")]
