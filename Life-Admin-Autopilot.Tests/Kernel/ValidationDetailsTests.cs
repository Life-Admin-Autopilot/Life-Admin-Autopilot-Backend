using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Validation;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The three <c>details</c> shapes, and the field rules whose order of operations
/// is visible on the wire. Expected values copied from live Node responses.
/// </summary>
public sealed class ValidationDetailsTests
{
    [Fact]
    public void path_message_array_joins_the_path_with_dots()
    {
        // Arrange
        var issues = new[] { ValidationIssue.At("draft.title", "Required") };

        // Act
        var details = ValidationDetails.AsPathMessageArray(issues);

        // Assert — the validation_error lane uses a STRING path, not an array.
        Assert.Equal("draft.title", details[0].Path);
        Assert.Equal("Required", details[0].Message);
    }

    [Fact]
    public void flatten_keys_nested_issues_under_the_top_level_field()
    {
        // Arrange
        var issues = new[]
        {
            ValidationIssue.At("theme", "Invalid enum value. Expected 'system' | 'light' | 'dark', received 'nope'"),
            ValidationIssue.At("mic.quality", "Invalid enum value. Expected 'standard' | 'high', received 'ultra'"),
        };

        // Act
        var details = ValidationDetails.AsFlattened(issues);

        // Assert — verified live: the key is "mic", never "mic.quality".
        Assert.Empty(details.FormErrors);
        Assert.True(details.FieldErrors.ContainsKey("mic"));
        Assert.False(details.FieldErrors.ContainsKey("mic.quality"));
        Assert.Equal(2, details.FieldErrors.Count);
    }

    [Fact]
    public void flatten_groups_repeated_issues_on_one_field()
    {
        // Arrange
        var issues = new[]
        {
            ValidationIssue.At("status", "expected one of open|done|snoozed, got \"a\""),
            ValidationIssue.At("status", "expected one of open|done|snoozed, got \"b\""),
        };

        // Act
        var details = ValidationDetails.AsFlattened(issues);

        // Assert
        Assert.Equal(2, details.FieldErrors["status"].Count);
    }

    [Fact]
    public void flatten_puts_path_less_issues_in_form_errors()
    {
        // Act
        var details = ValidationDetails.AsFlattened(new[] { ValidationDetails.UnrecognizedKeys(new[] { "bogus" }) });

        // Assert
        Assert.Equal("Unrecognized key(s) in object: 'bogus'", details.FormErrors[0]);
        Assert.Empty(details.FieldErrors);
    }

    [Fact]
    public void raw_issues_serialize_with_an_array_path()
    {
        // Arrange — the invalid_feed lane keeps zod's own issue objects.
        var issues = ValidationDetails.AsRawIssues(new[]
        {
            new ValidationIssue
            {
                Code = "invalid_type",
                Expected = "string",
                Received = "undefined",
                Path = new[] { "label" },
                Message = "Required",
            },
        });

        // Act
        var json = JsonSerializer.Serialize(issues);

        // Assert
        Assert.Contains("\"path\":[\"label\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"expected\":\"string\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void unrecognized_keys_quotes_and_comma_joins()
    {
        // Assert
        Assert.Equal(
            "Unrecognized key(s) in object: 'a', 'b'",
            ValidationDetails.UnrecognizedKeys(new[] { "a", "b" }).Message);
    }

    // ---- Order-of-operations rules ----------------------------------------

    [Fact]
    public void email_is_validated_before_it_is_trimmed()
    {
        // Assert — verified live: a padded address is REJECTED, because zod runs
        // .email() before .trim().
        Assert.Null(NodeFieldRules.NormalizeEmail("  a@b.com  "));
    }

    [Fact]
    public void email_is_lowercased_when_it_is_valid()
    {
        // Assert — verified live: signup with KerNeL@Probe.COM stores kernel@probe.com.
        Assert.Equal("kernel@probe.com", NodeFieldRules.NormalizeEmail("KerNeL@Probe.COM"));
    }

    [Fact]
    public void six_digit_code_is_trimmed_before_the_regex()
    {
        // Assert — the opposite order to email: .trim() comes first, so a padded code
        // is ACCEPTED.
        Assert.Equal("424242", NodeFieldRules.NormalizeSixDigitCode(" 424242 "));
        Assert.Null(NodeFieldRules.NormalizeSixDigitCode("42424"));
        Assert.Null(NodeFieldRules.NormalizeSixDigitCode("4242a2"));
    }

    [Fact]
    public void whitespace_display_name_passes_min_length_then_stores_empty()
    {
        // Act — .min(1) runs on the UNTRIMMED value, .trim() afterwards.
        var ok = NodeFieldRules.TryNormalizeDisplayName("   ", out var stored);

        // Assert
        Assert.True(ok);
        Assert.Equal(string.Empty, stored);
    }

    [Fact]
    public void empty_display_name_fails_min_length()
    {
        // Assert
        Assert.False(NodeFieldRules.TryNormalizeDisplayName(string.Empty, out _));
    }
}
