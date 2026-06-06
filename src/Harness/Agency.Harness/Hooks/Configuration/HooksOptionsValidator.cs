using Microsoft.Extensions.Configuration;

namespace Agency.Harness.Hooks.Configuration;

internal static class HooksOptionsValidator
{
#pragma warning disable IDE0060 // Remove unused parameter
    internal static void Validate(IConfigurationSection section, HooksOptions options)
#pragma warning restore IDE0060
    {
        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (!Enum.TryParse<HookEventName>(child.Key, ignoreCase: true, out HookEventName _))
            {
                throw new InvalidOperationException($"Unknown hook event '{child.Key}'.");
            }
        }
    }
}
