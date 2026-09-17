using dotacp.protocol;

namespace Agency.Acp.Errors;

/// <summary>
/// A domain error raised by a method handler that must surface as a specific JSON-RPC error code
/// rather than a generic <see cref="ErrorCode.InternalError"/>. <see cref="Agency.Acp.Dispatch.MethodDispatcher"/>
/// catches this alongside the framework's own <see cref="Newtonsoft.Json.JsonException"/> and maps it
/// straight to a JSON-RPC error response.
/// </summary>
// S3871 (exception types should be public): deliberately internal — a control-flow signal between
// this assembly's own handlers and MethodDispatcher, never a type an external caller catches by name.
#pragma warning disable S3871
internal sealed class AcpJsonRpcException : Exception
{
    /// <summary>Creates an exception that maps to the given JSON-RPC <paramref name="code"/>.</summary>
    public AcpJsonRpcException(ErrorCode code, string message)
        : base(message)
    {
        this.Code = code;
    }

    /// <summary>Gets the JSON-RPC error code to report to the client.</summary>
    public ErrorCode Code { get; }
}
#pragma warning restore S3871
