using System.Text;
using System.Text.Json;
using Agency.Acp.Errors;
using Agency.Acp.Sessions;
using Agency.Harness;
using Agency.Harness.Permissions;
using dotacp.protocol;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Turns;

/// <summary>
/// Turns one <c>session/prompt</c> into exactly one terminal <see cref="PromptResponse"/>, whatever
/// the harness does in between — including parking one or more times on
/// <see cref="AgentResultStatus.AwaitingPermission"/> (spec §6.4, §8.2, P4). Implements the
/// algorithm at spec §8.2 verbatim:
/// <code>
/// 1. Interlocked guard          → busy ⇒ JSON-RPC error
/// 2. new CancellationTokenSource, store on SessionState
/// 3. await foreach (AgentEvent e in ChatSession.SendAsync(text, ct))
///       translate → session/update
///       if AgentResultEvent { AwaitingPermission } → goto 4
///       if AgentResultEvent { terminal }           → goto 5
/// 4. request_permission ×N → ResumeWithPermissionsAsync → back to 3   ⟲
/// 5. map status → respond once; clear guard and CTS
/// </code>
/// </summary>
/// <remarks>
/// The park loop (step 4 looping back to step 3) is a loop, not a single round-trip: one prompt may
/// park more than once, and each park may yield one or more <see cref="PermissionRequestedEvent"/>s.
/// In v1 this path is unreachable through <see cref="Agency.Acp.Permissions.PersonaPermissionEvaluator"/>
/// (spec §6.6) — it never returns <see cref="PermissionDecision.Ask"/> — but it is fully implemented
/// and tested regardless (spec §6.4), driven in tests by a test-only evaluator that does ask.
/// </remarks>
internal sealed class TurnDriver
{
    private static readonly PermissionOption[] PermissionOptions =
    [
        new PermissionOption { OptionId = "allow_once", Kind = PermissionOptionKind.AllowOnce, Name = "Allow once" },
        new PermissionOption { OptionId = "allow_always", Kind = PermissionOptionKind.AllowAlways, Name = "Allow always" },
        new PermissionOption { OptionId = "reject_once", Kind = PermissionOptionKind.RejectOnce, Name = "Reject once" },
        new PermissionOption { OptionId = "reject_always", Kind = PermissionOptionKind.RejectAlways, Name = "Reject always" },
    ];

    private readonly IAcpClientProxy _client;

    /// <param name="client">The client-facing seam used to send <c>session/update</c>s and run the <c>session/request_permission</c> round-trip.</param>
    public TurnDriver(IAcpClientProxy client)
    {
        this._client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Runs one <c>session/prompt</c> turn for <paramref name="session"/> to completion, per spec §8.2.
    /// </summary>
    /// <param name="session">The session to run the turn on.</param>
    /// <param name="prompt">The prompt's content blocks; only <see cref="TextContent"/> blocks contribute text.</param>
    /// <param name="ct">Cancellation token for the surrounding request dispatch (not the turn's own lifetime — see <see cref="SessionState.TurnCts"/>).</param>
    /// <exception cref="AcpJsonRpcException">
    /// A prompt is already in flight for <paramref name="session"/> (spec §6.4 Constraints, P6: this
    /// is a client defect and must fail loudly rather than queue), or the harness turn ended in
    /// <see cref="AgentResultStatus.Error"/>, or the turn's own timeout fired (spec §6.9 D-5).
    /// </exception>
    public async Task<PromptResponse> RunTurnAsync(SessionState session, ContentBlock[] prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(prompt);

        // Step 1 (spec §8.2): one prompt in flight per session; a second is a client defect.
        if (!session.TryEnterTurn())
        {
            throw new AcpJsonRpcException(
                ErrorCode.InvalidRequest,
                $"A prompt is already in flight for session '{session.SessionId}'. Overlapping prompts are a client defect.");
        }

        // Step 2: a fresh CancellationTokenSource, stored on SessionState so session/cancel can
        // reach it from any thread (spec §6.4).
        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.TurnCts = turnCts;

        try
        {
            string userMessage = ExtractText(prompt);
            IAsyncEnumerable<AgentEvent> stream = session.ChatSession.SendAsync(userMessage, turnCts.Token);
            var messageIds = new EventTranslator.MessageIds();

            // Steps 3-4 (spec §8.2): drive the event stream; loop back into a fresh ResumeWithPermissionsAsync
            // stream every time the turn parks, until a genuinely terminal event is reached.
            while (true)
            {
                List<PermissionRequestedEvent> pendingPermissions = [];
                AgentResultEvent? terminal = null;

                await foreach (AgentEvent evt in stream.WithCancellation(turnCts.Token).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case PermissionRequestedEvent permissionRequested:
                            pendingPermissions.Add(permissionRequested);
                            break;

                        case AgentResultEvent { Status: AgentResultStatus.AwaitingPermission }:
                            // Never forwarded to the client (spec §6.4): pendingPermissions holds
                            // every PermissionRequestedEvent that preceded it.
                            break;

                        case AgentResultEvent result:
                            terminal = result;
                            break;

                        default:
                            await this.SendUpdateAsync(session, evt, messageIds, turnCts.Token).ConfigureAwait(false);
                            break;
                    }
                }

                if (terminal is not null)
                {
                    // Step 5: map status → respond once. AgentResultStatus.Error throws from within
                    // MapTerminal (P6) instead of returning here.
                    StopReason stopReason = StopReasonMapper.MapTerminal(terminal);
                    return new PromptResponse { StopReason = stopReason };
                }

                // Step 4: resolve every pending permission request, then resume into a fresh stream.
                IReadOnlyList<PermissionResponse> responses =
                    await this.RequestPermissionsAsync(session, pendingPermissions, turnCts.Token).ConfigureAwait(false);
                stream = session.ChatSession.ResumeWithPermissionsAsync(responses, turnCts.Token);
            }
        }
        catch (TimeoutException ex)
        {
            // Spec §6.9 D-5: a turn timeout is distinguishable from a user cancel — it fails loudly
            // rather than returning a plain Cancelled stop reason.
            throw StopReasonMapper.ForTimeout(ex);
        }
        catch (OperationCanceledException)
        {
            // Spec §8.3: cancellation emits no terminal AgentResultEvent; the exception itself is
            // the signal, and the driver synthesises the stop reason. Whatever text was already
            // streamed via session/update before the cancel remains with the client — nothing here
            // retracts it.
            return new PromptResponse { StopReason = StopReason.Cancelled };
        }
        finally
        {
            session.TurnCts = null;
            turnCts.Dispose();
            session.ExitTurn();
        }
    }

    private async Task SendUpdateAsync(SessionState session, AgentEvent evt, EventTranslator.MessageIds ids, CancellationToken ct)
    {
        SessionUpdate? update = EventTranslator.Translate(evt, session.Options.ContextWindowSize, ids);
        if (update is null)
        {
            return;
        }

        var notification = new SessionNotification { SessionId = session.SessionId, Update = update };
        await this._client.SendUpdateAsync(notification, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PermissionResponse>> RequestPermissionsAsync(
        SessionState session, List<PermissionRequestedEvent> pending, CancellationToken ct)
    {
        var responses = new List<PermissionResponse>(pending.Count);
        foreach (PermissionRequestedEvent request in pending)
        {
            RequestPermissionResponse response = await this._client
                .RequestPermissionAsync(BuildPermissionRequest(session, request), ct)
                .ConfigureAwait(false);

            if (response.Outcome is RequestPermissionOutcomeCancelled)
            {
                // Spec: a client that received session/cancel MUST answer every pending
                // session/request_permission with the Cancelled outcome. Treat it exactly like a
                // user cancel — TurnDriver's own catch below turns this into StopReason.Cancelled.
                throw new OperationCanceledException("The client answered a pending permission request with the Cancelled outcome.");
            }

            if (response.Outcome is not SelectedPermissionOutcome selected)
            {
                throw new AcpJsonRpcException(ErrorCode.InternalError, "Client returned an unrecognized session/request_permission outcome.");
            }

            responses.Add(new PermissionResponse(request.RequestId, MapOptionId(selected.OptionId)));
        }

        return responses;
    }

    private static RequestPermissionRequest BuildPermissionRequest(SessionState session, PermissionRequestedEvent request) =>
        new()
        {
            SessionId = session.SessionId,
            ToolCall = new ToolCallUpdate
            {
                ToolCallId = request.RequestId.ToString("n"),
                Title = request.ToolName,
                Status = ToolCallStatus.Pending,
                RawInput = ToRawInput(request.Input),
            },
            Options = PermissionOptions,
        };

    private static PermissionResponseKind MapOptionId(PermissionOptionId optionId)
    {
        string value = optionId;
        return value switch
        {
            "allow_once" => PermissionResponseKind.AllowOnce,
            "allow_always" => PermissionResponseKind.AllowAlways,
            "reject_once" => PermissionResponseKind.DenyOnce,
            "reject_always" => PermissionResponseKind.DenyAlways,
            _ => throw new AcpJsonRpcException(ErrorCode.InvalidParams, $"Unknown permission option id '{value}'."),
        };
    }

    private static JToken? ToRawInput(JsonElement input) =>
        input.ValueKind == JsonValueKind.Undefined ? null : JToken.Parse(input.GetRawText());

    private static string ExtractText(ContentBlock[] prompt)
    {
        var builder = new StringBuilder();
        foreach (ContentBlock block in prompt)
        {
            if (block is TextContent text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }
}
