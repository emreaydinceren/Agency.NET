namespace Agency.Harness.Hooks.Configuration.Handlers;

internal interface IHookHandlerFactory
{
    IHookHandler Create(HookHandlerConfig config);
}