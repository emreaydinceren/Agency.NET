namespace Agency.Configuration.Test;

/// <summary>
/// Smoke test verifying that internal members of <c>Agency.Configuration</c> are visible to this test
/// project via <c>InternalsVisibleTo</c>.
/// </summary>
public class ScaffoldSmokeTest
{
    /// <summary>
    /// Verifies that the internal <see cref="ScaffoldMarker"/> type is accessible from the test project.
    /// </summary>
    [Fact]
    public void InternalsVisibleTo_ScaffoldMarker_IsAccessible()
    {
        Assert.Equal("Agency.Configuration", ScaffoldMarker.Name);
    }
}
