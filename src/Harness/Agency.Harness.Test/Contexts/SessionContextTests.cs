using Agency.Harness.Contexts;

namespace Agency.Harness.Test.Contexts;

/// <summary>
/// Unit tests for <see cref="SessionContext"/>.
/// </summary>
public sealed class SessionContextTests
{
    /// <summary>
    /// The <see cref="SessionContext.Empty"/> singleton has a <see langword="null"/>
    /// <see cref="SessionContext.Id"/>.
    /// </summary>
    [Fact]
    public void Empty_HasNullId()
    {
        Assert.Null(SessionContext.Empty.Id);
    }

    /// <summary>
    /// <see cref="SessionContext.Empty"/> always returns the same cached instance rather than a
    /// new one per access.
    /// </summary>
    [Fact]
    public void Empty_ReturnsSameInstance()
    {
        Assert.Same(SessionContext.Empty, SessionContext.Empty);
    }

    /// <summary>
    /// A value assigned to <see cref="SessionContext.Id"/> via object-initializer syntax round-trips
    /// unchanged through the property getter.
    /// </summary>
    [Fact]
    public void Id_SetViaInit_RoundTrips()
    {
        var ctx = new SessionContext { Id = "abc-123" };
        Assert.Equal("abc-123", ctx.Id);
    }
}
