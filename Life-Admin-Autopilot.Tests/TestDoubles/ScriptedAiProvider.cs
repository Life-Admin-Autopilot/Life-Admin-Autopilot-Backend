using System.Runtime.CompilerServices;
using Life_Admin_Autopilot.BLL.Features.Ai;

namespace Life_Admin_Autopilot.Tests.TestDoubles;

/// <summary>
/// An <see cref="IAiProvider"/> that yields a fixed script.
///
/// <para>
/// It stands in for Langflow in the endpoint tests so those tests are about the ROUTE
/// — the header block, the frame sequence, the quota settlement, and the
/// before/after-flush rule — rather than about anything a model did. The Langflow
/// half is covered separately by <c>LangflowTranslationTests</c> and
/// <c>LangflowProviderTests</c>.
/// </para>
/// </summary>
public sealed class ScriptedAiProvider : IAiProvider
{
    private readonly IReadOnlyList<AiStreamEvent> _script;
    private readonly Exception? _throwAtEnd;
    private readonly int _throwAfter;

    public ScriptedAiProvider(
        IReadOnlyList<AiStreamEvent> script,
        Exception? throwAtEnd = null,
        int throwAfter = int.MaxValue)
    {
        _script = script;
        _throwAtEnd = throwAtEnd;
        _throwAfter = throwAfter;
    }

    public bool IsConfigured { get; init; } = true;

    /// <summary>Every ask this provider was handed, so a test can assert what the route passed down.</summary>
    public List<AiAskRequest> Asks { get; } = new();

    /// <summary>Every continuation, likewise.</summary>
    public List<AiContinuationRequest> Continuations { get; } = new();

    public Task<string> TranscribeAsync(
        AiTranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("transcribed");

    public IAsyncEnumerable<AiStreamEvent> AskAsync(
        AiAskRequest request,
        CancellationToken cancellationToken = default)
    {
        Asks.Add(request);
        return PlayAsync(cancellationToken);
    }

    public IAsyncEnumerable<AiStreamEvent> ContinueAfterConfirmAsync(
        AiContinuationRequest request,
        CancellationToken cancellationToken = default)
    {
        Continuations.Add(request);
        return PlayAsync(cancellationToken);
    }

    private async IAsyncEnumerable<AiStreamEvent> PlayAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var emitted = 0;

        foreach (var value in _script)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (emitted++ == _throwAfter && _throwAtEnd is not null)
            {
                throw _throwAtEnd;
            }

            yield return value;
            await Task.Yield();
        }

        if (_throwAtEnd is not null && _script.Count <= _throwAfter)
        {
            throw _throwAtEnd;
        }
    }

    /// <summary>A well-formed turn: sources, two tokens, done. The route appends the quota frame.</summary>
    public static IReadOnlyList<AiStreamEvent> HappyTurn() => new[]
    {
        AiStreamEvents.Sources(Array.Empty<AiStreamSource>()),
        AiStreamEvents.Token("All "),
        AiStreamEvents.Token("set."),
        AiStreamEvents.Done(),
    };
}
