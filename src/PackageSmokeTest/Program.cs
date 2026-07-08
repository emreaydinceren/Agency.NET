using System.Reflection;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Agency.PackageSmokeTest <packageId>");
    return 1;
}

var packageId = args[0];

try
{
    var assembly = Assembly.Load(packageId);
    var typeCount = assembly.GetTypes().Length;
    Console.WriteLine($"OK   {packageId} {assembly.GetName().Version} — {typeCount} types loaded.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL {packageId}: {ex}");
    return 1;
}
