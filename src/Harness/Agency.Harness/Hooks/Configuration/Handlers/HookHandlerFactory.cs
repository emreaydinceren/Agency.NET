using Microsoft.Extensions.Logging;

namespace Agency.Harness.Hooks.Configuration.Handlers;

internal sealed class HookHandlerFactory : IHookHandlerFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory? _loggerFactory;

    internal HookHandlerFactory(IHttpClientFactory httpFactory, ILoggerFactory? loggerFactory = null)
    {
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
    }

    public IHookHandler Create(HookHandlerConfig config) => config.Type switch
    {
        HookHandlerKind.Command => new CommandHookHandler(
            config,
            _loggerFactory?.CreateLogger<CommandHookHandler>()),
        HookHandlerKind.Http => new HttpHookHandler(
            config,
            _httpFactory.CreateClient(nameof(HttpHookHandler)),
            _loggerFactory?.CreateLogger<HttpHookHandler>()),
        _ => throw new NotSupportedException(
            $"Hook handler kind '{config.Type}' is not supported in V1.")
    };
}
