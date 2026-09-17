using Microsoft.Extensions.DependencyInjection;

namespace Agency.Acp.Test.Fakes;

/// <summary>
/// A minimal <see cref="IServiceScope"/> test double that records whether <see cref="Dispose"/>
/// was called, so tests can prove a <c>SessionState</c>'s DI scope was actually disposed rather
/// than merely that disposing it did not throw.
/// </summary>
internal sealed class FakeServiceScope : IServiceScope
{
    /// <summary>Gets whether <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc/>
    public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

    /// <inheritdoc/>
    public void Dispose() => this.IsDisposed = true;
}
