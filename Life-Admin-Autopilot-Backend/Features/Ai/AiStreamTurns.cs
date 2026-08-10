using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.Ai;

/// <summary>
/// The two streamed turns, past the point of no return.
///
/// <para>
/// Split out of <see cref="AiStreamEndpoints"/> because everything in that file runs
/// BEFORE the headers flush and everything here runs after — the same line the
/// contract draws. Nothing in this file may throw out to the kernel error
/// middleware: by the time it runs, a 200 <c>text/event-stream</c> is already on the
/// wire and the only way to report a failure is an <c>error</c> frame.
/// </para>
/// </summary>
internal static class AiStreamTurns
{
    /// <summary>
    /// <c>POST /ai/ask</c>, from the flush onwards.
    ///
    /// <para>
    /// <b>The quota slot has already been consumed</b> by the caller's
    /// <c>AdmitAsync</c>, before the flush, so the 402 could land as a real JSON
    /// error. This owes exactly one release if the turn never reaches <c>done</c> —
    /// Node's <c>finally</c> does the same, and skipping it burns a user's whole day
    /// of quota on a crashed stream.
    /// </para>
    /// </summary>
    public static async Task RunAskAsync(
        HttpContext context,
        KernelUser caller,
        IAiProvider ai,
        AiQuotaService quota,
        string tier,
        AiAskRequest request,
        CancellationToken cancellationToken)
    {
        var sse = new AiSseWriter(context.Response);
        var reachedDone = false;

        try
        {
            await sse.OpenAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await foreach (var value in ai.AskAsync(request, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    await sse.SendAsync(value, cancellationToken).ConfigureAwait(false);

                    if (value.Kind != AiStreamEvents.DoneKind)
                    {
                        continue;
                    }

                    // The slot was consumed by AdmitAsync — do NOT increment again.
                    // This only republishes the counter so the UI can update.
                    reachedDone = true;
                    await SendQuotaAsync(sse, quota, caller.Id, tier, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await ReportAsync(sse, ex, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!reachedDone)
            {
                await RefundAsync(quota, caller.Id).ConfigureAwait(false);
            }

            await sse.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>POST /ai/tools/confirm/{callId}</c>, from the flush onwards.
    ///
    /// <para>
    /// <b>Decline is the short path</b>: mark the record, emit
    /// <c>tool_result{result:null,error:"declined"}</c> and <c>done{usage:{}}</c>, and
    /// stop. No <c>quota</c> frame — nothing was spent, and Node returns before the
    /// line that would send one.
    /// </para>
    ///
    /// <para>
    /// <b>Confirm runs the tool and then re-enters the agent</b> so it can react to
    /// the outcome and finish any remaining steps of the user's original request. A
    /// tool that throws is not fatal: the failure becomes the <c>error</c> member of
    /// the <c>tool_result</c> frame, the record is marked <c>failed</c>, and the agent
    /// is still resumed so it can explain what went wrong.
    /// </para>
    ///
    /// <para>
    /// <b>The continuation is ungated but counted.</b> It is the same logical turn the
    /// user already paid for, so it is never refused — but it is a fresh model round,
    /// so <c>RecordAsync</c> increments the visible meter before the <c>quota</c>
    /// frame.
    /// </para>
    /// </summary>
    public static async Task RunConfirmAsync(
        HttpContext context,
        KernelUser caller,
        IAiProvider ai,
        AiQuotaService quota,
        AiConversationService conversations,
        AiConfirmedToolRunner tools,
        string tier,
        string action,
        string callId,
        string toolName,
        IReadOnlyDictionary<string, object?> toolArgs,
        CancellationToken cancellationToken)
    {
        var sse = new AiSseWriter(context.Response);

        try
        {
            await sse.OpenAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (action == "decline")
                {
                    await conversations.DeclineToolCallAsync(caller.Id, callId, cancellationToken)
                        .ConfigureAwait(false);
                    await sse.SendAsync(
                            AiStreamEvents.ToolResult(callId, null, "declined"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await sse.SendAsync(AiStreamEvents.Done(), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var (result, error) = await RunToolAsync(
                        conversations, tools, caller.Id, callId, toolName, toolArgs, cancellationToken)
                    .ConfigureAwait(false);

                await sse.SendAsync(AiStreamEvents.ToolResult(callId, result, error), cancellationToken)
                    .ConfigureAwait(false);

                // Defensive, and it mirrors Node exactly: the destructive tool above
                // still ran, the user just gets no follow-up message.
                if (!ai.IsConfigured)
                {
                    await sse.SendAsync(AiStreamEvents.Done(), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var continuation = new AiContinuationRequest(
                    caller.IdString, callId, toolName, toolArgs, result, error, null);

                await foreach (var value in ai.ContinueAfterConfirmAsync(continuation, cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    await sse.SendAsync(value, cancellationToken).ConfigureAwait(false);

                    if (value.Kind != AiStreamEvents.DoneKind)
                    {
                        continue;
                    }

                    await quota.RecordAsync(caller.Id, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    await SendQuotaAsync(sse, quota, caller.Id, tier, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await ReportAsync(sse, ex, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await sse.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run the confirmed tool and record the outcome. Returns the pair that becomes
    /// the <c>tool_result</c> frame's two always-present members.
    /// </summary>
    private static async Task<(IReadOnlyDictionary<string, object?>? Result, string? Error)> RunToolAsync(
        AiConversationService conversations,
        AiConfirmedToolRunner tools,
        ObjectId userId,
        string callId,
        string toolName,
        IReadOnlyDictionary<string, object?> toolArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await tools.RunAsync(userId, toolName, toolArgs, cancellationToken).ConfigureAwait(false);
            await conversations.MarkToolCallExecutedAsync(userId, callId, result, cancellationToken)
                .ConfigureAwait(false);
            return (result, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = MessageOf(ex);
            await conversations.MarkToolCallFailedAsync(userId, callId, message, cancellationToken)
                .ConfigureAwait(false);
            return (null, message);
        }
    }

    /// <summary>
    /// The <c>quota</c> frame, synthesised by the route right after <c>done</c> so it
    /// is the last frame of the turn. No provider emits it.
    /// </summary>
    private static async Task SendQuotaAsync(
        AiSseWriter sse,
        AiQuotaService quota,
        ObjectId userId,
        string tier,
        CancellationToken cancellationToken)
    {
        var quotas = await quota.GetStatusAsync(userId, tier, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await sse.SendAsync(AiStreamEvents.Quota(tier, quotas), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turn a mid-stream exception into the error frame.
    ///
    /// <para>A client that hung up is NOT an error to report — there is nobody left
    /// to read the frame, and writing to a dead socket only throws again.</para>
    /// </summary>
    private static async Task ReportAsync(AiSseWriter sse, Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await sse.SendAsync(
                    AiStreamEvents.Error(CodeOf(ex), MessageOf(ex)),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The socket is gone. Nothing further can be reported to anyone.
        }
    }

    /// <summary>
    /// Node's release is <c>.catch(() =&gt; {})</c>, and it must run even when the
    /// request token is already cancelled — a client that hung up mid-answer still
    /// gets its slot back.
    /// </summary>
    private static async Task RefundAsync(AiQuotaService quota, ObjectId userId)
    {
        try
        {
            await quota.ReleaseAsync(userId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed refund must not replace the answer the user already has.
        }
    }

    /// <summary><c>err instanceof AppError ? err.code : 'internal_error'</c>.</summary>
    private static string CodeOf(Exception ex) =>
        ex is AppException app ? app.Code : "internal_error";

    /// <summary><c>AppError.message ?? Error.message ?? 'Unexpected error'</c>.</summary>
    private static string MessageOf(Exception ex) =>
        string.IsNullOrEmpty(ex.Message) ? "Unexpected error" : ex.Message;
}
