using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// Unwrapping the planning agent's envelope, one streamed chunk at a time.
///
/// <para>
/// The shipped chat prints every <c>token</c> frame verbatim, so before this the
/// user read <c>{ "mode": "chat", "reply": "Hi!", … }</c> in the message bubble.
/// The cases below are mostly about CHUNK BOUNDARIES, because that is the only
/// thing an incremental reader can get wrong that a whole-string parser cannot:
/// the model's chunks split wherever the tokeniser happened to split them, which
/// is routinely mid-key, mid-escape, and mid-word.
/// </para>
/// </summary>
public sealed class PlanningEnvelopeReaderTests
{
    private const string Envelope =
        """{"mode":"chat","reply":"Filed. Next Tuesday at 10am.","tasks":[],"clarifications":[],"pendingConfirmations":[]}""";

    /// <summary>Feed the text one character at a time — the worst case for any scanner.</summary>
    private static string ReadCharByChar(string text)
    {
        var reader = new PlanningEnvelopeReader();
        var seen = new System.Text.StringBuilder();

        foreach (var c in text)
        {
            seen.Append(reader.Push(c.ToString()));
        }

        seen.Append(reader.Flush());
        return seen.ToString();
    }

    [Fact]
    public void emits_only_the_reply_when_the_whole_envelope_arrives_at_once()
    {
        Assert.Equal("Filed. Next Tuesday at 10am.", PlanningEnvelopeReader.ExtractReply(Envelope));
    }

    [Fact]
    public void emits_the_same_reply_however_the_chunks_fall()
    {
        // One character at a time splits `"reply"`, the colon, and every escape.
        Assert.Equal("Filed. Next Tuesday at 10am.", ReadCharByChar(Envelope));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(29)]
    public void is_independent_of_chunk_size(int size)
    {
        var reader = new PlanningEnvelopeReader();
        var seen = new System.Text.StringBuilder();

        for (var i = 0; i < Envelope.Length; i += size)
        {
            seen.Append(reader.Push(Envelope.Substring(i, Math.Min(size, Envelope.Length - i))));
        }

        seen.Append(reader.Flush());
        Assert.Equal("Filed. Next Tuesday at 10am.", seen.ToString());
    }

    [Fact]
    public void decodes_escapes_including_ones_split_across_chunks()
    {
        var json = """{"reply":"Line one\nLine \"two\"—done","tasks":[]}""";

        Assert.Equal("Line one\nLine \"two\"—done", PlanningEnvelopeReader.ExtractReply(json));
        Assert.Equal("Line one\nLine \"two\"—done", ReadCharByChar(json));
    }

    [Fact]
    public void a_fenced_envelope_speaks_only_its_reply()
    {
        // gemini-3.5-flash wraps some turns in a markdown fence. The first backtick
        // used to send the WHOLE envelope down the passthrough path, and the user
        // read raw JSON in the bubble — fences included.
        var fenced = "```json\n" + Envelope + "\n```";

        Assert.Equal("Filed. Next Tuesday at 10am.", PlanningEnvelopeReader.ExtractReply(fenced));
        Assert.Equal("Filed. Next Tuesday at 10am.", ReadCharByChar(fenced));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(29)]
    public void a_fenced_envelope_is_independent_of_chunk_size(int size)
    {
        // The fence itself splits across chunks: "``" + "`js" + "on\n{" is the same
        // stream as one piece, and the scanner must wait rather than guess.
        var fenced = "```json\n" + Envelope + "\n```";
        var reader = new PlanningEnvelopeReader();
        var seen = new System.Text.StringBuilder();

        for (var i = 0; i < fenced.Length; i += size)
        {
            seen.Append(reader.Push(fenced.Substring(i, Math.Min(size, fenced.Length - i))));
        }

        seen.Append(reader.Flush());
        Assert.Equal("Filed. Next Tuesday at 10am.", seen.ToString());
    }

    [Fact]
    public void a_fenced_envelope_still_yields_the_bare_json_for_claims()
    {
        // Fences on the envelope copy would fail JsonDocument.Parse and the turn's
        // claims — the fabricated-action check — would silently go unchecked.
        var reader = new PlanningEnvelopeReader();
        reader.Push("```json\n" + Envelope + "\n```");
        reader.Flush();

        Assert.Equal(Envelope, reader.Envelope);
    }

    [Fact]
    public void prose_that_merely_starts_with_inline_code_passes_through()
    {
        // A backtick is only a fence when a `{` follows it; this one is speech.
        const string prose = "`amount` is required — try again.";

        Assert.Equal(prose, PlanningEnvelopeReader.ExtractReply(prose));
        Assert.Equal(prose, ReadCharByChar(prose));
    }

    [Fact]
    public void a_fence_the_stream_never_resolves_is_passthrough()
    {
        // The model emitted backticks and died. Show them: too much beats a blank chat.
        var reader = new PlanningEnvelopeReader();
        var seen = reader.Push("```");

        Assert.Equal("```", seen + reader.Flush());
    }

    [Fact]
    public void passes_plain_prose_straight_through()
    {
        // A flow that answers in text, or a model that ignored its instructions.
        // Swallowing this would leave the chat blank, which is worse than showing
        // something unexpected — so the ambiguous case resolves toward the user.
        const string prose = "Sure — I've added that for Tuesday.";

        Assert.Equal(prose, PlanningEnvelopeReader.ExtractReply(prose));
        Assert.Equal(prose, ReadCharByChar(prose));
    }

    [Fact]
    public void keeps_what_it_decoded_when_the_envelope_is_cut_off_mid_reply()
    {
        // Mistral's free tier 429s mid-turn. The half sentence the user was already
        // reading should survive rather than vanish on the error frame.
        var reader = new PlanningEnvelopeReader();
        var seen = reader.Push("""{"mode":"chat","reply":"Filed. Next Tue""");

        Assert.Equal("Filed. Next Tue", seen + reader.Flush());
    }

    [Fact]
    public void emits_nothing_for_an_empty_reply()
    {
        // The agent did the work through tools and said nothing. The pills carry the
        // turn; an empty bubble is correct.
        Assert.Equal(string.Empty, PlanningEnvelopeReader.ExtractReply(
            """{"mode":"chat","reply":"","tasks":[{"id":"1"}]}"""));
    }

    [Fact]
    public void ignores_a_reply_key_that_is_not_a_string()
    {
        Assert.Equal(string.Empty, PlanningEnvelopeReader.ExtractReply("""{"reply":null,"tasks":[]}"""));
    }

    [Fact]
    public void does_not_mistake_a_later_key_for_the_reply()
    {
        // `tasks` comes first here; the reader must not start emitting at the first
        // string it meets.
        Assert.Equal("done", PlanningEnvelopeReader.ExtractReply(
            """{"tasks":[{"title":"Renew insurance"}],"reply":"done"}"""));
    }
}

/// <summary>The same unwrapping, observed through the translator the route uses.</summary>
public sealed class LangflowEnvelopeTranslationTests
{
    [Fact]
    public void the_chat_receives_prose_rather_than_the_envelope()
    {
        var translator = new LangflowEventTranslator();

        // Chunked the way Mistral actually streams it — mid-key and mid-word.
        var chunks = new[]
        {
            "{\n", "  \"mode\":", " \"chat\",\n ", " \"reply\": \"Hi!", " What can I",
            " do for you?\",\n", "  \"tasks\": [],\n", "  \"clarifications\": []\n}",
        };

        var text = string.Concat(chunks
            .SelectMany(c => translator.Accept(Frame(c)))
            .Where(e => e.Kind == AiStreamEvents.TokenKind)
            .Select(e => (string)e.Payload["text"]!));

        Assert.Equal("Hi! What can I do for you?", text);
        Assert.Equal("Hi! What can I do for you?", translator.AssistantText);
    }

    [Fact]
    public void a_non_streaming_flow_also_gets_prose_from_its_end_frame()
    {
        var translator = new LangflowEventTranslator();
        translator.Accept(new LangflowFrame("end", System.Text.Json.JsonDocument.Parse(
            """{"result":{"outputs":[{"outputs":[{"results":{"message":{"text":"{\"mode\":\"chat\",\"reply\":\"All set.\",\"tasks\":[]}"}}}]}]}}""")
            .RootElement.Clone())).ToList();

        var tail = translator.Complete().ToList();

        Assert.Equal(AiStreamEvents.TokenKind, tail[0].Kind);
        Assert.Equal("All set.", tail[0].Payload["text"]);
    }

    [Fact]
    public void completing_a_turn_drains_whatever_the_reader_is_still_holding()
    {
        var translator = new LangflowEventTranslator();

        // The reader holds a chunk back while it is still undecided: leading
        // whitespace is the run-up to an envelope and the run-up to prose alike, so
        // it cannot be classified until a real character arrives. Mid-stream that
        // resolves itself on the next chunk. At END of stream — Langflow dropped the
        // connection, no `end` frame ever came — nobody was completing the reader's
        // two-call protocol, and what it was holding went in the bin.
        translator.Accept(Frame("  ")).ToList();

        var tail = translator.Complete().ToList();

        Assert.Equal(AiStreamEvents.TokenKind, tail[0].Kind);
        Assert.Equal("  ", tail[0].Payload["text"]);
        Assert.Equal(AiStreamEvents.DoneKind, tail[1].Kind);
    }

    [Fact]
    public void the_drain_does_not_make_a_finished_envelope_speak_twice()
    {
        var translator = new LangflowEventTranslator();
        translator.Accept(Frame("""{"mode":"chat","reply":"All set.","tasks":[]}""")).ToList();

        // Everything inside `reply` already streamed, and the rest of the envelope is
        // structure rather than speech. The drain must add nothing, and — because it
        // runs before the "did anything stream?" test — must not let the fallback copy
        // print the same sentence a second time either.
        var tail = translator.Complete().ToList();

        Assert.Single(tail);
        Assert.Equal(AiStreamEvents.DoneKind, tail[0].Kind);
        Assert.Equal("All set.", translator.AssistantText);
    }

    private static LangflowFrame Frame(string chunk) =>
        new("token", System.Text.Json.JsonDocument
            .Parse(System.Text.Json.JsonSerializer.Serialize(new { chunk }))
            .RootElement.Clone());
}
