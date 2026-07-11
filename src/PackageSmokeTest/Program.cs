using System.Reflection;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Agency.PackageSmokeTest <assemblyName>");
    return 1;
}

var assemblyName = args[0];

try
{
    var assembly = Assembly.Load(assemblyName);
    var typeCount = assembly.GetTypes().Length;
    Console.WriteLine($"OK   {assemblyName} {assembly.GetName().Version} — {typeCount} types loaded.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL {assemblyName}: {ex}");
    return 1;
}
