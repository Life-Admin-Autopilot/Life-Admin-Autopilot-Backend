using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;
using Life_Admin_Autopilot.BLL.Kernel.Telemetry;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// <b>Fixtures are trimmed copies of a REAL Langflow 1.11.2 stream</b>, captured by
/// POSTing a turn at the live planning flow (6b0f1c2e…) on 2026-08-17 and keeping
/// the exact paths and key names off the wire. Only bulk was removed — no shape was
/// invented. That matters here more than anywhere: every previous adapter defect in
/// this codebase came from a synthetic frame that agreed with an assumption rather
/// than with Langflow.
/// </summary>
public class LangflowUsageTests
{
    /// <summary>The <c>end</c> frame, at the exact nesting the live run used.</summary>
    private const string RealEndFrame =
        """
        {"result":{"outputs":[{"outputs":[{"results":{"message":{"properties":{"usage":{"input_tokens":29395,"output_tokens":643,"total_tokens":30038},"state":"complete"},"text":"(trimmed)"}}}]}]}}
        """;

    /// <summary>A completed <c>add_message</c> row, as redelivered three times in the live run.</summary>
    private const string RealAddMessageFrame =
        """
        {"data":{"properties":{"usage":{"input_tokens":29395,"output_tokens":643,"total_tokens":30038},"state":"complete"},"text":"(trimmed)"}}
        """;

    private static LangflowFrame Frame(string eventName, string json) =>
        new(eventName, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Reads_usage_from_the_real_end_frame()
    {
        var read = LangflowUsage.TryRead(
            Frame(LangflowWireContract.EndEvent, RealEndFrame),
            out var usage);

        Assert.True(read);
        Assert.Equal(29395, usage.InputTokens);
        Assert.Equal(643, usage.OutputTokens);
        Assert.Equal(30038, usage.TotalTokens);
    }

    [Fact]
    public void Reads_usage_from_a_completed_add_message()
    {
        var read = LangflowUsage.TryRead(
            Frame(LangflowWireContract.AddMessageEvent, RealAddMessageFrame),
            out var usage);

        Assert.True(read);
        Assert.Equal(30038, usage.TotalTokens);
    }

    /// <summary>
    /// Langflow emits the message row as soon as it exists and rewrites it as the
    /// agent works. Only the completed rewrite carries real counts; reading a
    /// half-filled row would bank a partial turn as a whole one.
    /// </summary>
    [Fact]
    public void Ignores_an_add_message_that_is_not_complete()
    {
        const string partial =
            """
            {"data":{"properties":{"usage":{"input_tokens":10,"output_tokens":1,"total_tokens":11},"state":"partial"}}}
            """;

        Assert.False(LangflowUsage.TryRead(Frame(LangflowWireContract.AddMessageEvent, partial), out _));
    }

    /// <summary>
    /// <b>The redelivery guard.</b> The live run sent the same completed row three
    /// times with identical counts. A caller that accumulated would have charged this
    /// turn 90,114 tokens instead of 30,038 — a 3× cost overstatement that would look
    /// entirely plausible on a dashboard.
    /// </summary>
    [Fact]
    public void Redelivered_rows_are_last_wins_not_a_sum()
    {
        var frames = new[]
        {
            Frame(LangflowWireContract.AddMessageEvent, RealAddMessageFrame),
            Frame(LangflowWireContract.AddMessageEvent, RealAddMessageFrame),
            Frame(LangflowWireContract.AddMessageEvent, RealAddMessageFrame),
            Frame(LangflowWireContract.EndEvent, RealEndFrame),
        };

        // Exactly the shape LangflowAiProvider.RunTurnAsync uses.
        LangflowTokenUsage? observed = null;
        foreach (var frame in frames)
        {
            if (LangflowUsage.TryRead(frame, out var usage))
            {
                observed = usage;
            }
        }

        Assert.NotNull(observed);
        Assert.Equal(30038, observed!.Value.TotalTokens);
    }

    [Fact]
    public void Frames_that_carry_no_usage_are_not_an_error()
    {
        Assert.False(LangflowUsage.TryRead(
            Frame(LangflowWireContract.TokenEvent, """{"chunk":"Hel"}"""), out _));

        Assert.False(LangflowUsage.TryRead(
            Frame(LangflowWireContract.EndEvent, """{"result":{"outputs":[]}}"""), out _));

        Assert.False(LangflowUsage.TryRead(
            Frame(LangflowWireContract.LogEvent, """{"message":{"type":"tool_end"}}"""), out _));
    }

    /// <summary>
    /// A provider that reports a string where a count belongs should cost a data
    /// point, not a turn.
    /// </summary>
    [Fact]
    public void Non_numeric_counts_read_as_zero_rather_than_throwing()
    {
        const string malformed =
            """
            {"data":{"properties":{"usage":{"input_tokens":"lots","output_tokens":5,"total_tokens":null},"state":"complete"}}}
            """;

        Assert.True(LangflowUsage.TryRead(Frame(LangflowWireContract.AddMessageEvent, malformed), out var usage));
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);

        // total_tokens was unreadable, so it falls back to the split rather than to 0.
        Assert.Equal(5, usage.TotalTokens);
    }

    /// <summary>
    /// The reported total wins over input+output. Vendors count cached and reasoning
    /// tokens that are not always in the split, and they are still billed.
    /// </summary>
    [Fact]
    public void Reported_total_wins_over_the_derived_sum()
    {
        const string withExtras =
            """
            {"data":{"properties":{"usage":{"input_tokens":100,"output_tokens":10,"total_tokens":250},"state":"complete"}}}
            """;

        Assert.True(LangflowUsage.TryRead(Frame(LangflowWireContract.AddMessageEvent, withExtras), out var usage));
        Assert.Equal(250, usage.TotalTokens);
    }
}

public class ModelPricingTests
{
    private static ModelPricing Pricing(params (string Key, string Value)[] settings) =>
        ModelPricing.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build());

    /// <summary>
    /// Vendors append dated and preview suffixes. An exact-match table would silently
    /// stop pricing the day one changed.
    /// </summary>
    [Fact]
    public void Matches_a_model_id_by_prefix()
    {
        var estimate = Pricing().Estimate("gemini-2.5-flash-preview-05-20", 1_000_000, 0);

        Assert.True(estimate.Priced);
        Assert.Equal(0.30m, estimate.Usd);
    }

    [Fact]
    public void Longest_prefix_wins()
    {
        // "gemini-2.5-flash-lite" and "gemini-2.5-flash" both prefix this id; the
        // more specific one has to win or every lite call is priced at flash rates.
        var estimate = Pricing().Estimate("gemini-2.5-flash-lite-001", 1_000_000, 0);

        Assert.Equal(0.10m, estimate.Usd);
    }

    /// <summary>
    /// The whole reason <c>Priced</c> exists: an unknown model must not silently
    /// contribute $0 to a total that is then presented as the spend.
    /// </summary>
    [Fact]
    public void An_unknown_model_is_reported_as_unpriced_rather_than_free()
    {
        var estimate = Pricing().Estimate("some-model-nobody-configured", 500_000, 10_000);

        Assert.False(estimate.Priced);
        Assert.Equal(0m, estimate.Usd);
    }

    [Fact]
    public void A_null_model_is_unpriced()
    {
        Assert.False(Pricing().Estimate(null, 1000, 100).Priced);
    }

    [Fact]
    public void Configuration_overrides_the_default_table()
    {
        var pricing = Pricing(
            ("Ai:Pricing:Models:gemini-2.5-flash:Input", "1.00"),
            ("Ai:Pricing:Models:gemini-2.5-flash:Output", "2.00"));

        var estimate = pricing.Estimate("gemini-2.5-flash", 1_000_000, 1_000_000);

        Assert.Equal(3.00m, estimate.Usd);
    }

    /// <summary>
    /// A half-configured entry is a typo. Honouring one side and defaulting the other
    /// produces a plausible, wrong number — worse than staying on a known default.
    /// </summary>
    [Fact]
    public void A_half_configured_price_is_ignored()
    {
        var pricing = Pricing(("Ai:Pricing:Models:gemini-2.5-flash:Input", "99.00"));

        Assert.Equal(0.30m, pricing.Estimate("gemini-2.5-flash", 1_000_000, 0).Usd);
    }

    /// <summary>
    /// The real measured turn, priced. Guards the arithmetic against a
    /// per-thousand/per-million slip, which is a 1000× error that still looks like a
    /// small number.
    /// </summary>
    [Fact]
    public void Prices_the_measured_turn_correctly()
    {
        var estimate = Pricing().Estimate("gemini-2.5-flash", 29_395, 643);

        // 29,395/1e6 × $0.30 + 643/1e6 × $2.50 = 0.0088185 + 0.0016075
        Assert.Equal(0.010426m, decimal.Round(estimate.Usd, 6));
    }

    [Fact]
    public void Negative_counts_are_clamped_not_rejected()
    {
        var estimate = Pricing().Estimate("gemini-2.5-flash", -5, -5);

        Assert.True(estimate.Priced);
        Assert.Equal(0m, estimate.Usd);
    }

    [Fact]
    public void Default_chat_model_is_null_until_configured()
    {
        Assert.Null(Pricing().DefaultChatModel);
        Assert.Equal("gemini-2.5-flash", Pricing(("Ai:Pricing:DefaultChatModel", "gemini-2.5-flash")).DefaultChatModel);
    }
}
