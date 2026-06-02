using Agency.Harness.Contexts;

namespace Agency.Harness.Test.Contexts;

/// <summary>
/// Unit tests for <see cref="SessionContext"/>.
/// </summary>
public sealed class SessionContextTests
{
    [Fact]
    public void Empty_HasNullId()
    {
        Assert.Null(SessionContext.Empty.Id);
    }

    [Fact]
    public void Empty_ReturnsSameInstance()
    {
        Assert.Same(SessionContext.Empty, SessionContext.Empty);
    }

    [Fact]
    public void Id_SetViaInit_RoundTrips()
    {
        var ctx = new SessionContext { Id = "abc-123" };
        Assert.Equal("abc-123", ctx.Id);
    }
}
