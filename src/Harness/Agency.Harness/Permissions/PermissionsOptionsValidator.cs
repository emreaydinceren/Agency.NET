namespace Agency.Harness.Permissions;

/// <summary>
/// Fail-fast validator for <see cref="PermissionsOptions"/> rule strings.
/// Called at startup (e.g. from <c>AddAgencyPermissions</c>) so malformed config is caught
/// before the agent processes any tool call.
/// </summary>
internal static class PermissionsOptionsValidator
{
    /// <summary>
    /// Validates every entry in <see cref="PermissionsOptions.Allow"/> and
    /// <see cref="PermissionsOptions.Deny"/> by attempting to parse each as a
    /// <see cref="PermissionRule"/>.
    /// </summary>
    /// <param name="options">The bound options to validate.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown on the first malformed rule, with a message that includes the offending rule string.
    /// </exception>
    internal static void Validate(PermissionsOptions options)
    {
        foreach (string rule in options.Allow)
        {
            if (!PermissionRule.TryParse(rule, out _))
            {
                throw new InvalidOperationException($"Invalid permission rule in Allow: '{rule}'.");
            }
        }

        foreach (string rule in options.Deny)
        {
            if (!PermissionRule.TryParse(rule, out _))
            {
                throw new InvalidOperationException($"Invalid permission rule in Deny: '{rule}'.");
            }
        }
    }
}
