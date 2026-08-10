using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.DocumentScans;

/// <summary>
/// Its own Mongo database and its own storage root, so a parallel slice's test
/// run cannot see these rows or these files.
/// </summary>
public sealed class DocumentScanWebApplicationFactory : KernelWebApplicationFactory
{
    public const string ScanDatabase = "kitto_parity_dotnet_e_tests";

    public string StorageRoot { get; } =
        Path.Combine(Path.GetTempPath(), $"kitto-scan-tests-{Guid.NewGuid():N}");

    public DocumentScanWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", ScanDatabase);
        With("DocumentScans:StorageDirectory", StorageRoot);
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
/// The eight document-scan routes, against behaviour captured from the live Node
/// reference on ports 4100 and 4200.
///
/// <para>
/// Mongo-backed cases skip (rather than fail) when the parity instance is not
/// running, following <c>UsageQuotaTests.TryCreateStore</c>. The auth and
/// binding cases need no database and always run.
/// </para>
/// </summary>
public sealed class DocumentScanEndpointTests : IClassFixture<DocumentScanWebApplicationFactory>
{
    private const string CapturedAt = "2026-08-09T10:00:00.000Z";

    private static readonly byte[] MinimalPdf = Encoding.Latin1.GetBytes(
        "%PDF-1.4\n" +
        "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
        "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\n" +
        "trailer<</Root 1 0 R>>\n" +
        "%%EOF\n");

    private readonly DocumentScanWebApplicationFactory _factory;

    public DocumentScanEndpointTests(DocumentScanWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- Auth. No database needed. ----------------------------------------

    [Theory]
    [InlineData("GET", "/me/document-scans")]
    [InlineData("GET", "/me/document-scans/quota")]
    [InlineData("GET", "/me/document-scans/6a78c437aa461ae1dc64ffff")]
    [InlineData("GET", "/me/document-scans/6a78c437aa461ae1dc64ffff/file")]
    [InlineData("POST", "/me/document-scans/6a78c437aa461ae1dc64ffff/reprocess")]
    [InlineData("POST", "/me/document-scans/6a78c437aa461ae1dc64ffff/review")]
    [InlineData("DELETE", "/me/document-scans/6a78c437aa461ae1dc64ffff")]
    public async Task refuses_every_route_without_a_token(string method, string path)
    {
        // Act
        var response = await _factory.CreateApiClient()
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var json = await ReadJsonAsync(response);
        Assert.Equal("missing_token", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- Upload binding. No database reached before the failure. ----------

    [Fact]
    public async Task reports_a_foreign_content_type_as_empty_body_not_unsupported_media_type()
    {
        // VERIFIED LIVE: express.raw({type: ALLOWED_MIME}) never populates the body
        // for text/plain, so the handler's FIRST check fires. `unsupported_media_type`
        // is unreachable, and reproducing that ordering is the point of this test.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Encoding.UTF8.GetBytes("hello there"),
            "text/plain",
            (ScanHeaders.CapturedAt, CapturedAt));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "empty_body", "No document payload received.");
    }

    [Fact]
    public async Task rejects_an_empty_body()
    {
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Array.Empty<byte>(),
            "application/pdf",
            (ScanHeaders.CapturedAt, CapturedAt));

        await AssertErrorAsync(response, "empty_body", "No document payload received.");
    }

    [Fact]
    public async Task rejects_a_body_over_the_friendly_cap_with_the_15MB_message()
    {
        // 20 MiB sits BETWEEN the friendly cap (15 MiB) and the transport ceiling
        // (30 MiB), which is the window the two-ceiling trick exists to serve.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            new byte[20 * 1024 * 1024],
            "application/pdf",
            (ScanHeaders.CapturedAt, CapturedAt));

        await AssertErrorAsync(response, "payload_too_large", "Document exceeds 15MB.");
    }

    [Fact]
    public async Task reports_a_missing_captured_at_header_under_its_schema_field_name()
    {
        // fieldErrors is keyed on the ZOD field (capturedAt), not the header name.
        var response = await UploadAsync(ObjectId.GenerateNewId(), MinimalPdf, "application/pdf");

        var error = await AssertErrorAsync(
            response,
            "invalid_metadata",
            "Missing or invalid x-document-scan-* headers.");

        var fieldErrors = error.GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal("Required", fieldErrors.GetProperty("capturedAt")[0].GetString());
        Assert.Empty(error.GetProperty("details").GetProperty("formErrors").EnumerateArray());
    }

    [Fact]
    public async Task reports_every_bad_header_in_one_response_in_schema_order()
    {
        // zod parses the whole object and reports all of it; stopping at the first
        // failure would make the client fix three headers one round trip at a time.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            MinimalPdf,
            "application/pdf",
            (ScanHeaders.CapturedAt, "nope"),
            (ScanHeaders.Source, "banana"),
            (ScanHeaders.Timezone, "Not/AZone"));

        var error = await AssertErrorAsync(
            response,
            "invalid_metadata",
            "Missing or invalid x-document-scan-* headers.");

        var fieldErrors = error.GetProperty("details").GetProperty("fieldErrors");
        Assert.Equal(
            new[] { "capturedAt", "source", "timezone" },
            fieldErrors.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Invalid datetime", fieldErrors.GetProperty("capturedAt")[0].GetString());
        Assert.Equal(
            "Invalid enum value. Expected 'camera' | 'pdf' | 'gallery', received 'banana'",
            fieldErrors.GetProperty("source")[0].GetString());
        Assert.Equal("must be a valid IANA timezone", fieldErrors.GetProperty("timezone")[0].GetString());
    }

    [Fact]
    public async Task rejects_a_captured_at_without_a_trailing_Z()
    {
        // zod's .datetime() defaults to offset:false, so an offset form is refused
        // even though it is a perfectly good RFC 3339 timestamp.
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            MinimalPdf,
            "application/pdf",
            (ScanHeaders.CapturedAt, "2026-08-09T10:00:00+02:00"));

        var error = await AssertErrorAsync(
            response,
            "invalid_metadata",
            "Missing or invalid x-document-scan-* headers.");

        Assert.Equal(
            "Invalid datetime",
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("capturedAt")[0].GetString());
    }

    [Fact]
    public async Task rejects_a_pdf_it_cannot_read_before_touching_storage()
    {
        var response = await UploadAsync(
            ObjectId.GenerateNewId(),
            Encoding.UTF8.GetBytes("this is not a pdf at all"),
            "application/pdf",
            (ScanHeaders.CapturedAt, CapturedAt));

        await AssertErrorAsync(
            response,
            "invalid_pdf",
            "Could not read that PDF — it may be corrupt or password-protected.");
    }

    // ---- Lookup semantics. No database row needed for the miss cases. -----

    [Fact]
    public async Task maps_a_malformed_id_to_the_global_cast_error_404()
    {
        // A DIFFERENT body from the route's own scanned_document_not_found, and both
        // are contract.
        var response = await SendAsync(HttpMethod.Get, "/me/document-scans/not-an-object-id", ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "not_found", "Not found");
    }

    [Fact]
    public async Task reports_a_well_formed_but_unknown_id_with_the_route_specific_404()
    {
        var response = await SendAsync(
            HttpMethod.Get,
            "/me/document-scans/6a78c437aa461ae1dc64ffff",
            ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "scanned_document_not_found", "Scanned document no longer exists.");
    }

    [Fact]
    public async Task deletes_idempotently_rather_than_404ing_on_a_second_call()
    {
        // Unlike the ICS and Google deletes, which DO 404 the second time. A
        // double-tap must not surface an error the client has to special-case.
        var response = await SendAsync(
            HttpMethod.Delete,
            "/me/document-scans/6a78c437aa461ae1dc64ffff",
            ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task still_404s_a_malformed_id_on_delete_despite_the_idempotency()
    {
        // The CastError is thrown by the lookup, BEFORE the idempotent branch.
        var response = await SendAsync(HttpMethod.Delete, "/me/document-scans/nope", ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "not_found", "Not found");
    }

    [Fact]
    public async Task routes_the_quota_meter_before_the_id_parameter()
    {
        // Both routes can answer 404 here, so the STATUS proves nothing — the code
        // is the discriminator. If /{id} had won, "quota" would be parsed as an id
        // and the global CastError would give `not_found`. `user_not_found` can only
        // come from the meter's own handler, so the literal segment matched first.
        var response = await SendAsync(HttpMethod.Get, "/me/document-scans/quota", ObjectId.GenerateNewId());

        var code = (await ReadJsonAsync(response)).GetProperty("error").GetProperty("code").GetString();
        Assert.Equal("user_not_found", code);
    }

    // ---- Full lifecycle. Mongo required. ----------------------------------

    [Fact]
    public async Task accepts_an_upload_with_202_and_holds_it_pending()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);

        // Act
        var response = await UploadAsync(
            userId,
            MinimalPdf,
            "application/pdf",
            (ScanHeaders.CapturedAt, CapturedAt),
            (ScanHeaders.Source, "camera"),
            (ScanHeaders.Timezone, "Africa/Cairo"));

        // Assert — 202, not 201: extraction has NOT run.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var scan = (await ReadJsonAsync(response)).GetProperty("scannedDocument");
        Assert.Equal("pending", scan.GetProperty("status").GetString());
        Assert.Equal("camera", scan.GetProperty("sourceType").GetString());
        Assert.Equal("application/pdf", scan.GetProperty("mimeType").GetString());
        Assert.Equal(1, scan.GetProperty("pageCount").GetInt32());
        Assert.Equal(MinimalPdf.Length, scan.GetProperty("byteSize").GetInt32());
        Assert.Equal("Africa/Cairo", scan.GetProperty("timezone").GetString());
        Assert.Equal(userId.ToString(), scan.GetProperty("userId").GetString());
        Assert.False(scan.GetProperty("canRetry").GetBoolean());
        Assert.Empty(scan.GetProperty("candidates").EnumerateArray());
        Assert.False(scan.TryGetProperty("storageKey", out _));
    }

    [Fact]
    public async Task defaults_the_source_to_pdf_when_the_header_is_absent()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);

        // A PNG still defaults to sourceType "pdf" — the header describes the CAPTURE
        // route, not the file format, and Node does not infer one from the other.
        var response = await UploadAsync(
            userId,
            Encoding.UTF8.GetBytes("fake-png-bytes"),
            "image/png",
            (ScanHeaders.CapturedAt, CapturedAt));

        var scan = (await ReadJsonAsync(response)).GetProperty("scannedDocument");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("pdf", scan.GetProperty("sourceType").GetString());
        Assert.Equal(1, scan.GetProperty("pageCount").GetInt32());
    }

    [Fact]
    public async Task streams_the_original_bytes_back_inline_and_cacheable()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);

        // Act
        var response = await SendAsync(HttpMethod.Get, $"/me/document-scans/{scanId}/file", userId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(MinimalPdf, await response.Content.ReadAsByteArrayAsync());

        // NonValidated, deliberately. Both `Headers.CacheControl` and
        // `TryGetValues` run the value through CacheControlHeaderValue, which
        // re-serialises in its own canonical order ("max-age=86400, private") — that
        // would assert the CLIENT's formatting rather than the bytes the server put
        // on the wire, and the reference sends "private, max-age=86400".
        Assert.Equal(
            "private, max-age=86400",
            string.Join(", ", response.Headers.NonValidated["Cache-Control"]));
    }

    [Fact]
    public async Task answers_errors_on_the_file_route_with_json_not_bytes()
    {
        var response = await SendAsync(
            HttpMethod.Get,
            "/me/document-scans/6a78c437aa461ae1dc64ffff/file",
            ObjectId.GenerateNewId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        await AssertErrorAsync(response, "scanned_document_not_found", "Scanned document no longer exists.");
    }

    [Fact]
    public async Task answers_reprocess_on_a_pending_scan_with_200_not_202_and_not_409()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);

        // Act
        var response = await SendAsync(HttpMethod.Post, $"/me/document-scans/{scanId}/reprocess", userId);

        // Assert — an idempotent no-op. The client polls every 4s, so a retry tapped
        // after the worker already recovered must read as success. 202 is reserved
        // for a genuinely failed scan being re-queued.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var scan = (await ReadJsonAsync(response)).GetProperty("scannedDocument");
        Assert.Equal("pending", scan.GetProperty("status").GetString());
    }

    [Fact]
    public async Task re_queues_a_failed_scan_with_202_and_clears_its_failure_reason()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange — drive the row to the state the worker leaves behind.
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);
        await MarkFailedAsync(scans, scanId, manualRetries: 0);

        var before = (await ReadJsonAsync(await SendAsync(
            HttpMethod.Get, $"/me/document-scans/{scanId}", userId))).GetProperty("scannedDocument");
        Assert.True(before.GetProperty("canRetry").GetBoolean());
        Assert.Equal("boom", before.GetProperty("failureReason").GetString());

        // Act
        var response = await SendAsync(HttpMethod.Post, $"/me/document-scans/{scanId}/reprocess", userId);

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var scan = (await ReadJsonAsync(response)).GetProperty("scannedDocument");
        Assert.Equal("pending", scan.GetProperty("status").GetString());
        Assert.False(scan.GetProperty("canRetry").GetBoolean());
        Assert.False(scan.TryGetProperty("failureReason", out _));
    }

    [Fact]
    public async Task refuses_a_fourth_manual_retry()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);
        await MarkFailedAsync(scans, scanId, manualRetries: 3);

        // canRetry is already false, so the button is not offered — the gate and the
        // derived flag agree because they read the same constant.
        var view = (await ReadJsonAsync(await SendAsync(
            HttpMethod.Get, $"/me/document-scans/{scanId}", userId))).GetProperty("scannedDocument");
        Assert.False(view.GetProperty("canRetry").GetBoolean());

        // Act
        var response = await SendAsync(HttpMethod.Post, $"/me/document-scans/{scanId}/reprocess", userId);

        // Assert
        await AssertErrorAsync(
            response,
            "document_scan_retry_exhausted",
            "This document has failed too many times to keep retrying. Try scanning it again.");
    }

    [Fact]
    public async Task does_not_refund_the_quota_slot_on_reprocess_or_delete()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);
        Assert.Equal(1, await ReadUsedAsync(userId));

        await MarkFailedAsync(scans, scanId, manualRetries: 0);

        // Act
        await SendAsync(HttpMethod.Post, $"/me/document-scans/{scanId}/reprocess", userId);
        Assert.Equal(1, await ReadUsedAsync(userId));

        await SendAsync(HttpMethod.Delete, $"/me/document-scans/{scanId}", userId);

        // Assert — still 1. Refunding on delete would make scan-then-delete an
        // unlimited loop around the monthly cap; charging the retry would bill the
        // user for our own failure.
        Assert.Equal(1, await ReadUsedAsync(userId));
    }

    [Fact]
    public async Task refuses_an_upload_at_the_cap_with_the_scan_shaped_402()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId, count: 20);

        // Act
        var response = await UploadAsync(
            userId, MinimalPdf, "application/pdf", (ScanHeaders.CapturedAt, CapturedAt));

        // Assert
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);

        var error = await AssertErrorAsync(
            response,
            "document_scan_quota_exceeded",
            "You've hit this month's limit of 20 document scans.");

        // {tier,limit,used} — and NOT the AI 402's {kind,tier,limit,used,resetAt}.
        var details = error.GetProperty("details");
        Assert.Equal(
            new[] { "tier", "limit", "used" },
            details.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("free", details.GetProperty("tier").GetString());
        Assert.Equal(20, details.GetProperty("limit").GetInt32());
        Assert.Equal(20, details.GetProperty("used").GetInt32());
    }

    [Fact]
    public async Task admits_a_pro_user_as_free_while_the_meter_reports_their_real_tier()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange — the route hard-codes `const tier = 'free' as const`; GET /quota
        // reads the real one. Reproduced deliberately, verified live on :4100.
        var userId = ObjectId.GenerateNewId();
        await SeedUserAsync(userId, tier: "pro");
        await ResetQuotaAsync(userId, count: 20);

        // Act
        var meter = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/me/document-scans/quota", userId));
        var upload = await UploadAsync(
            userId, MinimalPdf, "application/pdf", (ScanHeaders.CapturedAt, CapturedAt));

        // Assert — the meter says pro/200 …
        Assert.Equal("pro", meter.GetProperty("tier").GetString());
        Assert.Equal(200, meter.GetProperty("quota").GetProperty("limit").GetInt32());
        Assert.Equal(180, meter.GetProperty("quota").GetProperty("remaining").GetInt32());

        // … and the upload is still refused at the free-tier ceiling of 20.
        var error = await AssertErrorAsync(
            upload,
            "document_scan_quota_exceeded",
            "You've hit this month's limit of 20 document scans.");
        Assert.Equal("free", error.GetProperty("details").GetProperty("tier").GetString());
        Assert.Equal(20, error.GetProperty("details").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task reports_a_missing_user_on_the_quota_meter_but_not_on_the_list()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // A cryptographically valid token outlives the account it names. Only the
        // meter loads the user, so only the meter 404s.
        var ghost = ObjectId.GenerateNewId();

        var meter = await SendAsync(HttpMethod.Get, "/me/document-scans/quota", ghost);
        Assert.Equal(HttpStatusCode.NotFound, meter.StatusCode);
        await AssertErrorAsync(meter, "user_not_found", "Account no longer exists.");

        var list = await SendAsync(HttpMethod.Get, "/me/document-scans", ghost);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task refuses_a_review_before_the_scan_is_ready_even_with_a_malformed_body()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);

        // Act — the status gate runs BEFORE body validation, so this reports
        // scan_not_ready rather than invalid_review.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/me/document-scans/{scanId}/review")
        {
            Content = new StringContent("{\"accepts\":[{\"key\":\"\"}]}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        var response = await _factory.CreateApiClient().SendAsync(request);

        // Assert
        await AssertErrorAsync(response, "scan_not_ready", "This scan is not ready for review yet.");
    }

    [Fact]
    public async Task rejects_a_review_payload_keyed_on_its_top_level_field()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange — a scan that IS ready, so body validation is reached.
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);
        await MarkReadyAsync(scans, scanId);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, $"/me/document-scans/{scanId}/review")
        {
            Content = new StringContent("{\"accepts\":[{\"key\":\"\"}]}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        var response = await _factory.CreateApiClient().SendAsync(request);

        // Assert — zod's flatten() keys the nested issue under path[0], "accepts".
        var error = await AssertErrorAsync(response, "invalid_review", "Invalid review payload.");
        Assert.Equal(
            "String must contain at least 1 character(s)",
            error.GetProperty("details").GetProperty("fieldErrors").GetProperty("accepts")[0].GetString());
    }

    [Fact]
    public async Task accepts_an_absent_review_body_as_accept_nothing_discard_nothing()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        if (scans is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);
        await MarkReadyAsync(scans, scanId);

        // Act — `req.body ?? {}`, so no body at all is valid.
        var response = await SendAsync(HttpMethod.Post, $"/me/document-scans/{scanId}/review", userId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ReadJsonAsync(response);
        Assert.Empty(json.GetProperty("tasks").EnumerateArray());
        Assert.Equal("ready_for_review", json.GetProperty("scannedDocument").GetProperty("status").GetString());
    }

    [Fact]
    public async Task turns_an_accepted_candidate_into_a_task_and_stamps_the_review()
    {
        var scans = TryGetCollection(MongoCollections.ScannedDocuments);
        var tasks = TryGetCollection(MongoCollections.Tasks);
        if (scans is null || tasks is null)
        {
            return;
        }

        // Arrange
        var userId = ObjectId.GenerateNewId();
        await ResetQuotaAsync(userId);
        var scanId = await UploadAndReadIdAsync(userId);
        await MarkReadyAsync(scans, scanId, "keep-me", "drop-me");

        // Act — one accept with a title override, one discard.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/me/document-scans/{scanId}/review")
        {
            Content = new StringContent(
                """{"accepts":[{"key":"keep-me","title":"Pay the electricity bill"}],"discards":["drop-me"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        var json = await ReadJsonAsync(await _factory.CreateApiClient().SendAsync(request));

        // Assert
        var created = json.GetProperty("tasks").EnumerateArray().Single();
        Assert.Equal("Pay the electricity bill", created.GetProperty("title").GetString());
        Assert.Equal("open", created.GetProperty("status").GetString());
        Assert.Equal(scanId, created.GetProperty("sourceDocumentId").GetString());

        var scan = json.GetProperty("scannedDocument");

        // The discarded candidate is gone; the accepted one carries its new taskId.
        var candidate = scan.GetProperty("candidates").EnumerateArray().Single();
        Assert.Equal("keep-me", candidate.GetProperty("key").GetString());
        Assert.Equal(created.GetProperty("id").GetString(), candidate.GetProperty("taskId").GetString());

        // Nothing is left un-filed, so the pass is closed.
        Assert.True(scan.TryGetProperty("reviewedAt", out _));
    }

    // ---- Helpers ----------------------------------------------------------

    private static class ScanHeaders
    {
        public const string CapturedAt = "x-document-scan-captured-at";
        public const string Source = "x-document-scan-source";
        public const string Timezone = "x-document-scan-timezone";
    }

    private Task<HttpResponseMessage> UploadAsync(
        ObjectId userId,
        byte[] bytes,
        string contentType,
        params (string Name, string Value)[] headers)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        var request = new HttpRequestMessage(HttpMethod.Post, "/me/document-scans") { Content = content };
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
            userId, MinimalPdf, "application/pdf", (ScanHeaders.CapturedAt, CapturedAt));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("scannedDocument").GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, ObjectId userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));
        return _factory.CreateApiClient().SendAsync(request);
    }

    private static string TokenFor(ObjectId userId) =>
        KernelPipelineTests.NodeShapedToken(userId.ToString(), "scans@probe.com");

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

    private static async Task MarkFailedAsync(
        IMongoCollection<BsonDocument> scans,
        string scanId,
        int manualRetries)
    {
        await scans.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(scanId)),
            Builders<BsonDocument>.Update
                .Set("status", "failed")
                .Set("failureReason", "boom")
                .Set("lastError", "boom")
                .Set("manualRetries", manualRetries));
    }

    private static async Task MarkReadyAsync(
        IMongoCollection<BsonDocument> scans,
        string scanId,
        params string[] candidateKeys)
    {
        var candidates = new BsonArray(candidateKeys.Select(key => new BsonDocument
        {
            ["key"] = key,
            ["title"] = $"held {key}",
            ["domain"] = "finance",
            ["priority"] = "normal",
            ["confidence"] = "medium",
        }));

        await scans.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(scanId)),
            Builders<BsonDocument>.Update
                .Set("status", "ready_for_review")
                .Set("candidates", candidates));
    }

    private static async Task SeedUserAsync(ObjectId userId, string tier)
    {
        var users = TryGetCollection(MongoCollections.Users);
        if (users is null)
        {
            return;
        }

        await users.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            new BsonDocument
            {
                ["_id"] = userId,
                ["email"] = $"{userId}@probe.com",

                // Distinct per seeded user: `users` carries a UNIQUE index on
                // identityUserId, so two rows both leaving it null collide.
                ["identityUserId"] = Guid.NewGuid().ToString(),
                ["subscription"] = new BsonDocument { ["tier"] = tier },
                ["createdAt"] = DateTime.UtcNow,
                ["updatedAt"] = DateTime.UtcNow,
            },
            new ReplaceOptions { IsUpsert = true });
    }

    private static async Task ResetQuotaAsync(ObjectId userId, int count = 0)
    {
        var counters = TryGetCollection(MongoCollections.DocumentScanUsageCounters);
        if (counters is null)
        {
            return;
        }

        await counters.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("userId", userId),
                Builders<BsonDocument>.Filter.Eq("month", DateTime.UtcNow.ToString("yyyy-MM"))),
            new BsonDocument
            {
                ["userId"] = userId,
                ["month"] = DateTime.UtcNow.ToString("yyyy-MM"),
                ["count"] = count,
            },
            new ReplaceOptions { IsUpsert = true });
    }

    private static async Task<int> ReadUsedAsync(ObjectId userId)
    {
        var counters = TryGetCollection(MongoCollections.DocumentScanUsageCounters);
        if (counters is null)
        {
            return -1;
        }

        var row = await counters
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("userId", userId),
                Builders<BsonDocument>.Filter.Eq("month", DateTime.UtcNow.ToString("yyyy-MM"))))
            .FirstOrDefaultAsync();

        return row is null ? 0 : row["count"].ToInt32();
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
                .GetDatabase(DocumentScanWebApplicationFactory.ScanDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database.GetCollection<BsonDocument>(name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
