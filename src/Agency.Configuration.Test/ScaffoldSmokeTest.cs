namespace Agency.Configuration.Test;

public class ScaffoldSmokeTest
{
    [Fact]
    public void InternalsVisibleTo_ScaffoldMarker_IsAccessible()
    {
        Assert.Equal("Agency.Configuration", ScaffoldMarker.Name);
    }
}
