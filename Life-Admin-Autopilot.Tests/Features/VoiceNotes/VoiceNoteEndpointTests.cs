using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.VoiceNotes;

/// <summary>
/// Its own Mongo database and its own storage root, so a parallel slice's test
/// run cannot see these rows or these files.
/// </summary>
public sealed class VoiceNoteWebApplicationFactory : KernelWebApplicationFactory
{
    public const string VoiceDatabase = "kitto_parity_dotnet_l_tests";

    public string StorageRoot { get; } =
        Path.Combine(Path.GetTempPath(), $"kitto-voice-tests-{Guid.NewGuid():N}");

    public VoiceNoteWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", VoiceDatabase);
        With("VoiceNotes:StorageDirectory", StorageRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless; failing a suite over it is not.
        }
    }
}

/// <summary>
/// The five voice-note routes, against behaviour captured from the live Node
/// reference on ports 4100 and 4200.
///
/// <para>
/// Mongo-backed cases skip (rather than fail) when the parity instance is not
/// running, following <c>UsageQuotaTests.TryCreateStore</c>. The auth and binding
/// cases need no database and always run.
/// </para>
/// </summary>
public sealed class VoiceNoteEndpointTests : IClassFixture<VoiceNoteWebApplicationFactory>
{
    private const string CapturedAt = "2026-08-09T10:00:00.000Z";
    private const string UnknownId = "6a78c437aa461ae1dc64ffff";

    private static readonly byte[] Audio = Encoding.UTF8.GetBytes("kitto-parity-fake-audio");

    private readonly VoiceNoteWebApplicationFactory _factory;

    public VoiceNoteEndpointTests(VoiceNoteWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- Auth. No database needed. ----------------------------------------

    [Theory]
    [InlineData("POST", "/me/voice-notes")]
    [InlineData("GET", "/me/voice-notes")]
    [InlineData("GET", "/me/voice-notes/6a78c437aa461ae1dc64ffff")]
    [InlineData("POST", "/me/voice-notes/6a78c437aa461ae1dc64ffff/extract-tasks")]
    [InlineData("POST", "/me/voice-notes/6a78c437aa461ae1dc64ffff/review")]
    public async Task refuses_every_route_without_a_token(string method, string path)
    {
        var response = await _factory.CreateApiClient()
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var json = await ReadJsonAsync(response);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- Upload binding. No database reached before the failure. ----------

    [Fact]
    public async Task reports_a_foreign_content_type_as_empty_body()
    {
        // express.raw({type: [...audio]}) never populates the body for text/plain,
        // so the handler's FIRST check fires. There is no unsupported_media_type on
        // this route at all — reproducing that ordering is the point.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Encoding.UTF8.GetBytes("hello there"),
            "text/plain",
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, CapturedAt));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "empty_body", "No audio payload received.");
    }

    [Theory]
    [InlineData("audio/m4a")]
    [InlineData("audio/mp4")]
    [InlineData("audio/aac")]
    [InlineData("application/octet-stream")]
    public async Task accepts_all_four_audio_content_types(string contentType)
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            contentType,
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, CapturedAt));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var note = (await ReadJsonAsync(response)).GetProperty("voiceNote");
        Assert.Equal(contentType, note.GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task still_matches_a_content_type_carrying_parameters()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        // Node splits on the first ';', so the charset is dropped before matching AND
        // before the value is stored as mimeType.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            "audio/m4a; charset=binary",
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, CapturedAt));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            "audio/m4a",
            (await ReadJsonAsync(response)).GetProperty("voiceNote").GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task rejects_an_empty_body()
    {
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Array.Empty<byte>(),
            "audio/m4a",
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, CapturedAt));

        await AssertErrorAsync(response, "empty_body", "No audio payload received.");
    }

    [Fact]
    public async Task rejects_a_body_over_the_friendly_cap_with_the_5MB_message()
    {
        // 6 MiB sits BETWEEN the friendly cap (5 MiB) and the transport ceiling
        // (10 MiB), which is the window the two-ceiling trick exists to serve. A
        // body over the DOUBLED ceiling is a 500 instead, not a 413.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            new byte[6 * 1024 * 1024],
            "audio/m4a",
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, CapturedAt));

        await AssertErrorAsync(response, "payload_too_large", "Voice note exceeds 5MB.");
    }

    [Fact]
    public async Task reports_a_missing_duration_header_as_nan_not_required()
    {
        // THE header trap. `z.coerce.number()` runs Number(undefined) -> NaN, and the
        // type check then reports the coerced value. "Required" would be the natural
        // guess and it is wrong; the contract records this explicitly.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            "audio/m4a",
            (Headers.CapturedAt, CapturedAt));

        var error = await AssertErrorAsync(
            response,
            "invalid_metadata",
            "Missing or invalid x-voice-note-* headers.");

        var fieldErrors = error.GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal("Expected number, received nan", fieldErrors.GetProperty("durationMs")[0].GetString());
        Assert.Empty(error.GetProperty("details").GetProperty("formErrors").EnumerateArray());
    }

    [Fact]
    public async Task reports_a_missing_captured_at_header_as_required()
    {
        // The OTHER half of the same trap: capturedAt is a plain z.string(), with no
        // coercion, so an absent header IS "Required".
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            "audio/m4a",
            (Headers.Duration, "1200"));

        var error = await AssertErrorAsync(
            response,
            "invalid_metadata",
            "Missing or invalid x-voice-note-* headers.");

        Assert.Equal(
            "Required",
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("capturedAt")[0].GetString());
    }

    [Fact]
    public async Task reports_every_bad_header_in_one_response_in_schema_order()
    {
        // zod parses the whole object and reports all of it; stopping at the first
        // failure would make the client fix four headers one round trip at a time.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            "audio/m4a",
            (Headers.Duration, "banana"),
            (Headers.CapturedAt, "nope"),
            (Headers.Source, "megaphone"),
            (Headers.Timezone, new string('z', 65)));

        var error = await AssertErrorAsync(
            response,
            "invalid_metadata",
            "Missing or invalid x-voice-note-* headers.");

        var fieldErrors = error.GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal(
            new[] { "durationMs", "capturedAt", "source", "timezone" },
            fieldErrors.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Expected number, received nan", fieldErrors.GetProperty("durationMs")[0].GetString());
        Assert.Equal("Invalid datetime", fieldErrors.GetProperty("capturedAt")[0].GetString());
        Assert.Equal(
            "Invalid enum value. Expected 'app' | 'widget' | 'dynamic_island' | 'lock_screen', received 'megaphone'",
            fieldErrors.GetProperty("source")[0].GetString());
        Assert.Equal(
            "String must contain at most 64 character(s)",
            fieldErrors.GetProperty("timezone")[0].GetString());
    }

    [Theory]
    [InlineData("-1", "Number must be greater than or equal to 0")]
    [InlineData("600001", "Number must be less than or equal to 600000")]
    [InlineData("12.5", "Expected integer, received float")]
    public async Task enforces_the_duration_range_and_integer_check(string duration, string expected)
    {
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            "audio/m4a",
            (Headers.Duration, duration),
            (Headers.CapturedAt, CapturedAt));

        var error = await AssertErrorAsync(
            response,
            "invalid_metadata",
            "Missing or invalid x-voice-note-* headers.");

        Assert.Contains(
            expected,
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("durationMs")
                .EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task accepts_an_unvalidated_timezone_string_on_the_upload_route()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        // A REAL asymmetry with the document-scan upload and with this slice's own
        // extract-tasks body, both of which DO check the zone is IANA. Here any
        // 1..64 character string is stored verbatim, so rejecting it would refuse
        // uploads the reference server accepts.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            "audio/m4a",
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, CapturedAt),
            (Headers.Timezone, "Not/AZone"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            "Not/AZone",
            (await ReadJsonAsync(response)).GetProperty("voiceNote").GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task rejects_a_captured_at_without_a_trailing_Z()
    {
        // zod's .datetime() defaults to offset:false, so an offset form is refused
        // even though it is a perfectly good RFC 3339 timestamp.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            "audio/m4a",
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, "2026-08-09T10:00:00+02:00"));

        var error = await AssertErrorAsync(
            response,
            "invalid_metadata",
            "Missing or invalid x-voice-note-* headers.");

        Assert.Equal(
            "Invalid datetime",
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("capturedAt")[0].GetString());
    }

    // ---- Lookup semantics. No database row needed for the miss cases. -----

    [Fact]
    public async Task maps_a_malformed_id_to_the_global_cast_error_404()
    {
        // A DIFFERENT body from the route's own voice_note_not_found, and both are
        // contract.
        var response = await SendAsync(HttpMethod.Get, "/me/voice-notes/not-an-object-id", ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "not_found", "Not found");
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "/extract-tasks")]
    [InlineData("POST", "/review")]
    public async Task reports_an_unknown_id_with_the_route_specific_404(string method, string suffix)
    {
        var response = await SendAsync(
            new HttpMethod(method),
            $"/me/voice-notes/{UnknownId}{suffix}",
            ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "voice_note_not_found", "Voice note no longer exists.");
    }

    // ---- Body-validation ORDER. The two write routes disagree, on purpose. -

    [Fact]
    public async Task validates_the_extract_body_before_looking_the_note_up()
    {
        // Deliberate in Node: a bad timezone reaching the extractor used to crash
        // Intl.DateTimeFormat with a RangeError and surface as a 500. So a malformed
        // payload against an UNKNOWN note is a 400, not a 404.
        var response = await SendJsonAsync(
            $"/me/voice-notes/{UnknownId}/extract-tasks",
            ObjectId.GenerateNewId(),
            """{"timezone":"Not/AZone"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await AssertErrorAsync(response, "invalid_body", "Invalid extract payload.");
        Assert.Equal(
            "must be a valid IANA timezone",
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("timezone")[0].GetString());
    }

    [Fact]
    public async Task looks_the_note_up_before_validating_the_review_body()
    {
        // The OPPOSITE order, and observable: the same malformed-payload probe that
        // 400s on extract-tasks is a 404 here.
        var response = await SendJsonAsync(
            $"/me/voice-notes/{UnknownId}/review",
            ObjectId.GenerateNewId(),
            """{"discards":[""]}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "voice_note_not_found", "Voice note no longer exists.");
    }

    [Theory]
    [InlineData("/extract-tasks")]
    [InlineData("/review")]
    public async Task answers_malformed_json_with_500_even_before_the_note_lookup(string suffix)
    {
        // express.json() is GLOBAL middleware: it has already parsed — and already
        // thrown — before the route's first line runs. So malformed JSON beats even
        // the 404, on BOTH write routes, however their own body/lookup order differs.
        // VERIFIED LIVE against :4200. Reading the body inside the handler is what
        // makes this easy to get wrong: the review route answered 404 here until the
        // parse was split out ahead of the lookup.
        var response = await SendJsonAsync(
            $"/me/voice-notes/{UnknownId}{suffix}",
            ObjectId.GenerateNewId(),
            "{ not json");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertErrorAsync(response, "internal_error", "Internal server error");
    }

    // ---- Full lifecycle. Mongo required. ----------------------------------

    [Fact]
    public async Task accepts_an_upload_with_202_and_holds_it_pending()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();

        var response = await UploadAsync(
            userId,
            Audio,
            "audio/m4a",
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, CapturedAt),
            (Headers.Source, "lock_screen"),
            (Headers.Timezone, "Africa/Cairo"));

        // 202, not 201: transcription has NOT run.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var note = (await ReadJsonAsync(response)).GetProperty("voiceNote");
        Assert.Equal("pending", note.GetProperty("status").GetString());
        Assert.Equal("lock_screen", note.GetProperty("source").GetString());
        Assert.Equal(1200, note.GetProperty("durationMs").GetInt32());
        Assert.Equal(Audio.Length, note.GetProperty("byteSize").GetInt32());
        Assert.Equal("Africa/Cairo", note.GetProperty("timezone").GetString());
        Assert.Equal(userId.ToString(), note.GetProperty("userId").GetString());
        Assert.Empty(note.GetProperty("extractedTasks").EnumerateArray());
        Assert.Empty(note.GetProperty("reviewItems").EnumerateArray());
        Assert.False(note.TryGetProperty("storageKey", out _));
        Assert.False(note.TryGetProperty("clarifyItems", out _));
    }

    [Fact]
    public async Task defaults_the_source_to_app_when_the_header_is_absent()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Audio,
            "audio/m4a",
            (Headers.Duration, "0"),
            (Headers.CapturedAt, CapturedAt));

        var note = (await ReadJsonAsync(response)).GetProperty("voiceNote");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("app", note.GetProperty("source").GetString());
        Assert.Equal(0, note.GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public async Task writes_the_audio_under_an_m4a_key_before_the_row_exists()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);

        // The root is read back out of the RUNNING app rather than assumed to be
        // this fixture's StorageRoot. `KernelWebApplicationFactory.With` does not
        // reach the IConfiguration that IEndpointModule.AddServices is handed, so
        // the override silently loses and the slice falls back to
        // <cwd>/uploads/voice-notes. That is a test-infra quirk in a Kernel/ file,
        // not this slice's to fix — and asserting against DI keeps this test honest
        // whichever way it resolves.
        var root = _factory.Services
            .GetRequiredService<Life_Admin_Autopilot.BLL.Features.VoiceNotes.VoiceNoteOptions>()
            .ResolveStorageRoot();

        // Bytes first, row second: a processing failure must never cost the user
        // their capture. The extension is always .m4a even for audio/aac.
        var path = Path.Combine(root, userId.ToString(), $"{noteId}.m4a");
        Assert.True(File.Exists(path), $"expected the audio at {path}");
        Assert.Equal(Audio, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task lists_only_the_callers_notes_newest_first()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        var mine = ObjectId.GenerateNewId();
        var theirs = ObjectId.GenerateNewId();
        var first = await UploadAndReadIdAsync(mine);
        var second = await UploadAndReadIdAsync(mine);
        await UploadAndReadIdAsync(theirs);

        var response = await SendAsync(HttpMethod.Get, "/me/voice-notes", mine);
        var ids = (await ReadJsonAsync(response)).GetProperty("voiceNotes")
            .EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { second, first }, ids);
    }

    [Fact]
    public async Task hides_another_users_note_behind_the_same_404_as_a_missing_one()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        // Owner-scoped lookup, so a stranger's id is indistinguishable from a
        // deleted one. That is the anti-enumeration choice, not an oversight.
        var owner = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(owner);

        var response = await SendAsync(HttpMethod.Get, $"/me/voice-notes/{noteId}", ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "voice_note_not_found", "Voice note no longer exists.");
    }

    [Fact]
    public async Task refuses_extraction_while_the_note_has_no_transcript()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        // With no AI key the worker can never store a transcript, so this is where
        // extract-tasks always stops on the parity target.
        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);

        var response = await SendJsonAsync($"/me/voice-notes/{noteId}/extract-tasks", userId, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "voice_note_not_ready", "Voice note transcript is not ready yet.");
    }

    [Fact]
    public async Task treats_a_silent_transcript_as_not_ready_too()
    {
        var notes = TryGetCollection(MongoCollections.VoiceNotes);
        if (notes is null)
        {
            return;
        }

        // Node tests `!note.transcript`, a FALSY check — so a note whose
        // transcription came back as silence ('') is "not ready", not "ready with
        // nothing in it".
        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);
        await SetAsync(notes, noteId, Builders<BsonDocument>.Update.Set("transcript", string.Empty));

        var response = await SendJsonAsync($"/me/voice-notes/{noteId}/extract-tasks", userId, "{}");

        await AssertErrorAsync(response, "voice_note_not_ready", "Voice note transcript is not ready yet.");
    }

    [Fact]
    public async Task marks_a_note_ready_and_stamps_reviewedAt_on_an_empty_review()
    {
        if (TryGetCollection(MongoCollections.VoiceNotes) is null)
        {
            return;
        }

        // There is NO status precondition here, unlike the document-scan review
        // which demands ready_for_review. VERIFIED LIVE on a `pending` note: an
        // empty body clears the (empty) review lane and closes the note.
        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);

        var response = await SendJsonAsync($"/me/voice-notes/{noteId}/review", userId, "{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.Empty(body.GetProperty("tasks").EnumerateArray());

        var note = body.GetProperty("voiceNote");
        Assert.Equal("ready", note.GetProperty("status").GetString());
        Assert.True(note.TryGetProperty("reviewedAt", out _));
    }

    [Fact]
    public async Task accepts_a_held_item_into_a_task_and_clears_the_review_lane()
    {
        var notes = TryGetCollection(MongoCollections.VoiceNotes);
        if (notes is null)
        {
            return;
        }

        // Arrange — drive the row to the state the worker would leave behind.
        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);
        await MarkNeedsReviewAsync(notes, noteId, "held-a", "held-b");

        // Act — accept one with a title override, discard the other.
        var response = await SendJsonAsync(
            $"/me/voice-notes/{noteId}/review",
            userId,
            """{"accepts":[{"key":"held-a","title":"Renew the car tax"}],"discards":["held-b"]}""");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        var task = Assert.Single(body.GetProperty("tasks").EnumerateArray().ToArray());
        Assert.Equal("Renew the car tax", task.GetProperty("title").GetString());
        Assert.Equal(noteId, task.GetProperty("sourceVoiceNoteId").GetString());

        var note = body.GetProperty("voiceNote");
        Assert.Empty(note.GetProperty("reviewItems").EnumerateArray());
        Assert.Equal("ready", note.GetProperty("status").GetString());

        // The accepted record is APPENDED to the audit list, forced to high/clear,
        // and back-linked to the Task it produced. The discarded one leaves no trace.
        var record = Assert.Single(note.GetProperty("extractedTasks").EnumerateArray().ToArray());
        Assert.Equal("held-a", record.GetProperty("key").GetString());
        Assert.Equal("high", record.GetProperty("confidence").GetString());
        Assert.Equal("clear", record.GetProperty("reviewReason").GetString());
        Assert.Equal(task.GetProperty("id").GetString(), record.GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task ignores_an_unknown_accept_key_instead_of_rejecting_it()
    {
        var notes = TryGetCollection(MongoCollections.VoiceNotes);
        if (notes is null)
        {
            return;
        }

        // Stale or already-handled keys are dropped idempotently — the review card
        // can be committed twice, and the second commit must not error.
        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);
        await MarkNeedsReviewAsync(notes, noteId, "held-a");

        var response = await SendJsonAsync(
            $"/me/voice-notes/{noteId}/review",
            userId,
            """{"accepts":[{"key":"nothing-like-this"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.Empty(body.GetProperty("tasks").EnumerateArray());

        // The genuinely-held item is untouched, so the note stays in review.
        var note = body.GetProperty("voiceNote");
        Assert.Single(note.GetProperty("reviewItems").EnumerateArray().ToArray());
        Assert.Equal("needs_review", note.GetProperty("status").GetString());
        Assert.False(note.TryGetProperty("reviewedAt", out _));
    }

    [Fact]
    public async Task creates_the_same_task_twice_only_once()
    {
        var notes = TryGetCollection(MongoCollections.VoiceNotes);
        if (notes is null)
        {
            return;
        }

        // The partial unique index on {userId, sourceVoiceNoteId, sourceTaskKey} is
        // what makes the second accept a no-op rather than a duplicate Task.
        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);
        await MarkNeedsReviewAsync(notes, noteId, "held-a");

        const string Body = """{"accepts":[{"key":"held-a"}]}""";
        var first = await SendJsonAsync($"/me/voice-notes/{noteId}/review", userId, Body);

        await MarkNeedsReviewAsync(notes, noteId, "held-a");
        var second = await SendJsonAsync($"/me/voice-notes/{noteId}/review", userId, Body);

        var firstTask = (await ReadJsonAsync(first)).GetProperty("tasks")[0].GetProperty("id").GetString();
        var secondTask = (await ReadJsonAsync(second)).GetProperty("tasks")[0].GetProperty("id").GetString();

        Assert.Equal(firstTask, secondTask);
    }

    [Fact]
    public async Task rejects_a_review_payload_with_an_empty_discard_key()
    {
        var notes = TryGetCollection(MongoCollections.VoiceNotes);
        if (notes is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);

        var response = await SendJsonAsync(
            $"/me/voice-notes/{noteId}/review",
            userId,
            """{"discards":[""]}""");

        var error = await AssertErrorAsync(response, "invalid_review", "Invalid review payload.");
        Assert.Equal(
            "String must contain at least 1 character(s)",
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("discards")[0].GetString());
    }

    [Fact]
    public async Task ignores_a_review_body_whose_content_type_is_not_json()
    {
        var notes = TryGetCollection(MongoCollections.VoiceNotes);
        if (notes is null)
        {
            return;
        }

        // express.json() parses ONLY application/json. A text/plain body — even a
        // syntactically invalid one — leaves req.body as {}, so this is a 200 that
        // simply accepts and discards nothing. A hand-read body that skips the
        // content-type gate would 500 here instead.
        var userId = ObjectId.GenerateNewId();
        var noteId = await UploadAndReadIdAsync(userId);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/me/voice-notes/{noteId}/review")
        {
            Content = new StringContent("{ this is not json", Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        var response = await _factory.CreateApiClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "ready",
            (await ReadJsonAsync(response)).GetProperty("voiceNote").GetProperty("status").GetString());
    }

    // ---- Helpers ----------------------------------------------------------

    private static class Headers
    {
        public const string Duration = "x-voice-note-duration-ms";
        public const string CapturedAt = "x-voice-note-captured-at";
        public const string Source = "x-voice-note-source";
        public const string Timezone = "x-voice-note-timezone";
    }

    private Task<HttpResponseMessage> UploadAsync(
        ObjectId userId,
        byte[] bytes,
        string contentType,
        params (string Name, string Value)[] headers)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        var request = new HttpRequestMessage(HttpMethod.Post, "/me/voice-notes") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return _factory.CreateApiClient().SendAsync(request);
    }

    private async Task<string> UploadAndReadIdAsync(ObjectId userId)
    {
        var response = await UploadAsync(
            userId,
            Audio,
            "audio/m4a",
            (Headers.Duration, "1200"),
            (Headers.CapturedAt, CapturedAt));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("voiceNote").GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, ObjectId userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));
        return _factory.CreateApiClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> SendJsonAsync(string path, ObjectId userId, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));
        return _factory.CreateApiClient().SendAsync(request);
    }

    private static string TokenFor(ObjectId userId) =>
        KernelPipelineTests.NodeShapedToken(userId.ToString(), "voice@probe.com");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> AssertErrorAsync(
        HttpResponseMessage response,
        string code,
        string message)
    {
        var error = (await ReadJsonAsync(response)).GetProperty("error");

        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(message, error.GetProperty("message").GetString());
        return error;
    }

    private static Task SetAsync(
        IMongoCollection<BsonDocument> notes,
        string noteId,
        UpdateDefinition<BsonDocument> update) =>
        notes.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(noteId)), update);

    private static Task MarkNeedsReviewAsync(
        IMongoCollection<BsonDocument> notes,
        string noteId,
        params string[] keys)
    {
        var items = new BsonArray(keys.Select(key => new BsonDocument
        {
            ["key"] = key,
            ["title"] = $"held {key}",
            ["domain"] = "finance",
            ["priority"] = "normal",
            ["confidence"] = "medium",
            ["reviewReason"] = "ambiguous_intent",
            ["reasons"] = new BsonArray(),
        }));

        return SetAsync(
            notes,
            noteId,
            Builders<BsonDocument>.Update
                .Set("status", "needs_review")
                .Set("transcript", "renew the car tax and call the vet")
                .Set("reviewItems", items));
    }

    /// <summary>
    /// Null when the parity Mongo instance is not running, so the suite stays green
    /// on a machine without it — same posture as <c>UsageQuotaTests.TryCreateStore</c>.
    /// </summary>
    private static IMongoCollection<BsonDocument>? TryGetCollection(string name)
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings)
                .GetDatabase(VoiceNoteWebApplicationFactory.VoiceDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database.GetCollection<BsonDocument>(name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
