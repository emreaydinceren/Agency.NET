using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Hooks.Configuration.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Harness.Test.Hooks.Configuration.Handlers;

public sealed class HookHandlerFactoryTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static HookHandlerFactory MakeFactory()
    {
        var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        return new HookHandlerFactory(services.GetRequiredService<IHttpClientFactory>());
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_Command_CreatesCommandHandler()
    {
        HookHandlerFactory factory = MakeFactory();
        HookHandlerConfig config = new HookHandlerConfig
        {
            Type = HookHandlerKind.Command,
            Command = "pwsh"
        };

        IHookHandler handler = factory.Create(config);

        Assert.NotNull(handler);
        Assert.IsType<CommandHookHandler>(handler);
    }

    [Fact]
    public void Factory_Http_CreatesHttpHandler()
    {
        HookHandlerFactory factory = MakeFactory();
        HookHandlerConfig config = new HookHandlerConfig
        {
            Type = HookHandlerKind.Http,
            Url = "http://test/"
        };

        IHookHandler handler = factory.Create(config);

        Assert.NotNull(handler);
        Assert.IsType<HttpHookHandler>(handler);
    }

    [Theory]
    [InlineData(HookHandlerKind.McpTool)]
    [InlineData(HookHandlerKind.Prompt)]
    [InlineData(HookHandlerKind.Agent)]
    internal void Factory_ReservedKind_ThrowsNotSupported(HookHandlerKind kind)
    {
        HookHandlerFactory factory = MakeFactory();
        HookHandlerConfig config = new HookHandlerConfig { Type = kind };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => factory.Create(config));

        Assert.Contains(kind.ToString(), ex.Message);
    }
}
