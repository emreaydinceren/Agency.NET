namespace Agency.Harness.Hooks.Configuration.Handlers;

internal interface IHookHandler
{
    Task<HookHandlerOutput> InvokeAsync(HookPayload payload, CancellationToken ct);
}