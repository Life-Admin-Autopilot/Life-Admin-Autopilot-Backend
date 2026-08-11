using System.Text;

namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// Turns the planning agent's JSON envelope back into the prose the chat expects,
/// <b>incrementally</b>, as the tokens arrive.
///
/// <para>
/// <b>Why this exists.</b> The flow's contract is an envelope —
/// <c>{"mode":…,"reply":…,"tasks":[…],"clarifications":[…],"pendingConfirmations":[…]}</c>
/// — because the structured half carries what the turn actually DID. But the SSE
/// contract the shipped chat renders is prose: it prints every <c>token</c> frame
/// verbatim. Wire the two together untranslated and the user reads raw JSON in the
/// message bubble, which is exactly what happened.
/// </para>
///
/// <para>
/// <b>Why incrementally rather than buffer-then-parse.</b> Buffering until the
/// envelope is complete would hold the whole answer back and repaint it at once,
/// which is the UX regression the streaming run endpoint exists to avoid. So this
/// walks the character stream and emits the contents of <c>reply</c> as they are
/// decoded, forwarding nothing else.
/// </para>
///
/// <para>
/// <b>It refuses to eat prose.</b> If the output does not begin with <c>{</c> it
/// passes everything through untouched — a flow that answers in plain text, an
/// older flow, or a model that ignored its instructions still reaches the user.
/// The failure mode of guessing wrong is a blank chat, so the ambiguous case is
/// resolved in favour of showing too much rather than too little.
/// </para>
/// </summary>
public sealed class PlanningEnvelopeReader
{
    private const string ReplyKey = "\"reply\"";

    private enum State
    {
        /// <summary>Waiting for the first non-whitespace character to decide.</summary>
        Undecided,

        /// <summary>Not an envelope. Everything goes to the user, verbatim.</summary>
        Passthrough,

        /// <summary>Inside the envelope, hunting for the reply key.</summary>
        SeekingKey,

        /// <summary>Key found; waiting for the colon and the opening quote.</summary>
        SeekingValue,

        /// <summary>Inside the reply string, decoding and emitting.</summary>
        InReply,

        /// <summary>Reply closed. The rest of the envelope is structure, not speech.</summary>
        Finished,
    }

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _spoken = new();
    private readonly StringBuilder _envelope = new();
    private State _state = State.Undecided;

    /// <summary>The reply as decoded so far — what belongs in the persisted turn.</summary>
    public string Spoken => _spoken.ToString();

    /// <summary>
    /// The envelope exactly as the model wrote it, reassembled from the chunks.
    ///
    /// <para>
    /// <b>The reply streams; the structured half cannot.</b> <c>tasks</c> and
    /// <c>clarifications</c> are what the turn claims it DID, and a claim is only
    /// checkable once the array that carries it is closed — so those are kept whole
    /// and read at the end (<see cref="PlanningEnvelopeClaims"/>), while the prose
    /// continues to be emitted character by character. Nothing about the streaming
    /// path changes; this is a copy, not a buffer in front of the user.
    /// </para>
    ///
    /// <para>
    /// Empty once the output has been recognised as plain prose — there is no envelope
    /// to keep, and copying an arbitrarily long passthrough answer would be pure cost.
    /// </para>
    /// </summary>
    public string Envelope => _envelope.ToString();

    /// <summary>True once the envelope was recognised, so callers can report honestly.</summary>
    public bool SawEnvelope => _state != State.Undecided && _state != State.Passthrough;

    /// <summary>
    /// Feed one streamed chunk; returns the text to forward, which is very often
    /// empty (envelope scaffolding) and never partial-escape garbage.
    /// </summary>
    public string Push(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return string.Empty;
        }

        // Kept until the output proves to be prose, at which point the copy is
        // dropped — see Envelope. Appended BEFORE Drain, because Drain is what
        // decides which of the two this is.
        if (_state != State.Passthrough)
        {
            _envelope.Append(chunk);
        }

        _pending.Append(chunk);
        var spoken = Drain();

        if (_state == State.Passthrough)
        {
            _envelope.Clear();
        }

        return spoken;
    }

    /// <summary>
    /// End of stream. A truncated envelope — the model stopped mid-reply, or was
    /// cut off by a rate limit — still yields whatever prose had been decoded,
    /// rather than discarding a half-finished sentence the user was already
    /// reading.
    /// </summary>
    public string Flush()
    {
        if (_state == State.Undecided && _pending.Length > 0)
        {
            // Never resolved to an envelope: it was whitespace, or nothing at all.
            _state = State.Passthrough;
            return Take();
        }

        return _state == State.Passthrough ? Take() : string.Empty;
    }

    /// <summary>
    /// The whole-string form, for the non-streamed path: an <c>end</c> frame's
    /// text, which repeats the same envelope in one piece.
    /// </summary>
    public static string ExtractReply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var reader = new PlanningEnvelopeReader();
        var body = reader.Push(text);
        return body + reader.Flush();
    }

    private string Take()
    {
        var all = _pending.ToString();
        _pending.Clear();
        _spoken.Append(all);
        return all;
    }

    private string Drain()
    {
        var output = new StringBuilder();

        while (true)
        {
            switch (_state)
            {
                case State.Undecided:
                {
                    var text = _pending.ToString();
                    var i = 0;
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }

                    if (i == text.Length)
                    {
                        return output.ToString();   // still only whitespace; wait
                    }

                    if (text[i] == '{')
                    {
                        _state = State.SeekingKey;
                        continue;
                    }

                    _state = State.Passthrough;
                    continue;
                }

                case State.Passthrough:
                    output.Append(Take());
                    return output.ToString();

                case State.SeekingKey:
                {
                    var text = _pending.ToString();
                    var at = text.IndexOf(ReplyKey, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        // Keep only enough to match a key split across chunks.
                        Trim(ReplyKey.Length);
                        return output.ToString();
                    }

                    _pending.Remove(0, at + ReplyKey.Length);
                    _state = State.SeekingValue;
                    continue;
                }

                case State.SeekingValue:
                {
                    var text = _pending.ToString();
                    var quote = -1;
                    for (var i = 0; i < text.Length; i++)
                    {
                        if (text[i] == '"') { quote = i; break; }

                        // Anything other than the colon or whitespace means `reply`
                        // was not a string — null, or a key inside a nested object
                        // that merely shares the name. Give up rather than emit junk.
                        if (text[i] != ':' && !char.IsWhiteSpace(text[i]))
                        {
                            _state = State.Finished;
                            _pending.Clear();
                            return output.ToString();
                        }
                    }

                    if (quote < 0)
                    {
                        return output.ToString();
                    }

                    _pending.Remove(0, quote + 1);
                    _state = State.InReply;
                    continue;
                }

                case State.InReply:
                {
                    var text = _pending.ToString();
                    var consumed = 0;
                    var closed = false;

                    while (consumed < text.Length)
                    {
                        var c = text[consumed];

                        if (c == '"')
                        {
                            consumed++;
                            closed = true;
                            break;
                        }

                        if (c == '\\')
                        {
                            // An escape may straddle a chunk boundary; \uXXXX needs six.
                            if (consumed + 1 >= text.Length) { break; }

                            var esc = text[consumed + 1];
                            if (esc == 'u')
                            {
                                if (consumed + 5 >= text.Length) { break; }

                                var hex = text.Substring(consumed + 2, 4);
                                if (ushort.TryParse(
                                        hex,
                                        System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        out var code))
                                {
                                    output.Append((char)code);
                                    _spoken.Append((char)code);
                                }

                                consumed += 6;
                                continue;
                            }

                            var decoded = esc switch
                            {
                                'n' => '\n',
                                't' => '\t',
                                'r' => '\r',
                                'b' => '\b',
                                'f' => '\f',
                                _ => esc,           // \" \\ \/ and anything else, literally
                            };

                            output.Append(decoded);
                            _spoken.Append(decoded);
                            consumed += 2;
                            continue;
                        }

                        output.Append(c);
                        _spoken.Append(c);
                        consumed++;
                    }

                    _pending.Remove(0, consumed);

                    if (closed)
                    {
                        _state = State.Finished;
                        continue;
                    }

                    return output.ToString();
                }

                case State.Finished:
                default:
                    _pending.Clear();
                    return output.ToString();
            }
        }
    }

    /// <summary>Drop everything that can no longer contribute to a split key match.</summary>
    private void Trim(int keep)
    {
        if (_pending.Length > keep)
        {
            _pending.Remove(0, _pending.Length - keep);
        }
    }
}
