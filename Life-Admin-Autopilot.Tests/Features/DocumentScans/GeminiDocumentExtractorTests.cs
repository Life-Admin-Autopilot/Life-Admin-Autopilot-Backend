using System.Net;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Life_Admin_Autopilot.Tests.Features.DocumentScans
{
    /// <summary>
    /// The reader that replaced <see cref="NullDocumentExtractor"/>.
    ///
    /// <para>
    /// Document scanning failed for every user with "AI is not configured" because
    /// nothing had ever been registered in the null one's place — the parity stub was
    /// the only implementation in the graph. These tests pin the behaviour of the
    /// real one, so it cannot silently regress to that state again.
    /// </para>
    /// </summary>
    public class GeminiDocumentExtractorTests
    {
        /// <summary>Gemini's envelope, with the model's JSON as the part's text.</summary>
        private static string Envelope(string modelJson)
        {
            var payload = new
            {
                candidates = new[]
                {
                    new { content = new { parts = new[] { new { text = modelJson } } } },
                },
            };

            return JsonSerializer.Serialize(payload);
        }

        private const string ElectricityBill = """
        {
          "documentType": "bill",
          "documentTitle": "Electricity bill",
          "documentSubtitle": "Account 4471-90 - July",
          "issuer": "North Delta Electricity",
          "documentSummary": "A July electricity bill for 812 EGP, due on the 28th.",
          "candidates": [
            {
              "title": "Pay the July electricity bill",
              "domain": "finance",
              "priority": "high",
              "confidence": "high",
              "dueAt": "2026-07-28T00:00:00Z",
              "notes": "812 EGP",
              "sourcePage": 1,
              "estimateMinMinutes": 5,
              "estimateMaxMinutes": 10
            }
          ]
        }
        """;

        [Fact]
        public async Task ExtractAsync_ReturnsTheCandidatesAndTheDocumentsIdentity()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Envelope(ElectricityBill));

            var extraction = await Extractor(handler).ExtractAsync(PdfRequest());

            Assert.Equal("bill", extraction.DocumentType);
            Assert.Equal("Electricity bill", extraction.DocumentTitle);
            Assert.Equal("North Delta Electricity", extraction.Issuer);

            var candidate = Assert.Single(extraction.Candidates);
            Assert.Equal("Pay the July electricity bill", candidate.Title);
            Assert.Equal("finance", candidate.Domain);
            Assert.Equal("high", candidate.Priority);
            Assert.Equal(new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc), candidate.DueAt);
            Assert.Equal(1, candidate.SourcePage);
            Assert.Equal(5, candidate.EstimateMinMinutes);
        }

        // The bytes have to reach the model as an inline blob with the RIGHT mime type.
        // A PDF announced as an image is rejected by the provider with a 400 that the
        // worker would report to the user as an unreadable document.
        [Fact]
        public async Task ExtractAsync_SendsTheDocumentInlineWithItsOwnMimeType()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Envelope(ElectricityBill));

            await Extractor(handler).ExtractAsync(PdfRequest());

            using var sent = JsonDocument.Parse(handler.LastRequestBody!);
            var blob = sent.RootElement
                .GetProperty("contents")[0]
                .GetProperty("parts")[0]
                .GetProperty("inline_data");

            Assert.Equal("application/pdf", blob.GetProperty("mime_type").GetString());
            Assert.Equal(Convert.ToBase64String(Bytes), blob.GetProperty("data").GetString());
        }

        // The model is told the enums but is not bound by them. Dropping a candidate
        // whose domain came back as "utilities" would lose a real obligation the user
        // needs; falling back to a renderable value keeps it reviewable.
        [Fact]
        public async Task ExtractAsync_KeepsACandidateWhoseVocabularyIsOffScript()
        {
            var offScript = """
            {
              "documentType": "utility",
              "candidates": [
                { "title": "Renew the car licence", "domain": "vehicle",
                  "priority": "immediately", "confidence": "certain" }
              ]
            }
            """;

            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Envelope(offScript));

            var extraction = await Extractor(handler).ExtractAsync(PdfRequest());

            var candidate = Assert.Single(extraction.Candidates);
            Assert.Equal("Renew the car licence", candidate.Title);
            Assert.Equal("home", candidate.Domain);
            Assert.Equal("normal", candidate.Priority);
            Assert.Equal("medium", candidate.Confidence);
            Assert.Equal("other", extraction.DocumentType);
        }

        // Not 503: the retry classifier treats 503 as transient, so an unreadable file
        // would burn the whole attempt ladder — roughly twenty seconds of backoff — to
        // arrive at the same answer it had immediately.
        [Fact]
        public async Task ExtractAsync_RefusesAFileTypeTheModelCannotRead_WithoutCallingIt()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Envelope(ElectricityBill));

            var error = await Assert.ThrowsAsync<AppException>(() =>
                Extractor(handler).ExtractAsync(PdfRequest() with { MimeType = "application/zip" }));

            Assert.Equal(415, error.Status);
            Assert.False(DocumentScanRetryPolicy.IsTransient(error));
            Assert.Equal(0, handler.CallCount);
        }

        // Free-tier capacity flaps per model, so a 503 is an answer about ONE model.
        [Fact]
        public async Task ExtractAsync_FallsBackToTheNextModel_WhenTheFirstIsBusy()
        {
            var handler = new StubHttpMessageHandler(request =>
                request.RequestUri!.ToString().Contains("gemini-3.7-flash", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("""{"error":{"message":"high demand"}}"""),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(Envelope(ElectricityBill)),
                    });

            var extraction = await Extractor(handler).ExtractAsync(PdfRequest());

            Assert.Equal(2, handler.CallCount);
            Assert.Single(extraction.Candidates);
        }

        // 503, so the worker retries rather than settling the document at failed — the
        // provider being busy is exactly what the attempt ladder is for.
        [Fact]
        public async Task ExtractAsync_ReportsATransientFailure_WhenEveryModelIsBusy()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "rate limited");

            var error = await Assert.ThrowsAsync<AppException>(() =>
                Extractor(handler).ExtractAsync(PdfRequest()));

            Assert.Equal(503, error.Status);
            Assert.True(DocumentScanRetryPolicy.IsTransient(error));
        }

        // A safety block on an ID card or a medical page comes back as an empty part.
        // The scan itself succeeded, so it settles at ready_for_review with nothing to
        // review — better than a failure the user cannot do anything about.
        [Fact]
        public async Task ExtractAsync_TreatsABlockedAnswerAsNothingToReview()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Envelope(string.Empty));

            var extraction = await Extractor(handler).ExtractAsync(PdfRequest());

            Assert.Empty(extraction.Candidates);
        }

        [Fact]
        public async Task ExtractAsync_ReportsAnUnusableAnswer_RatherThanCrashing()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Envelope("I could not read this document."));

            var error = await Assert.ThrowsAsync<AppException>(() =>
                Extractor(handler).ExtractAsync(PdfRequest()));

            Assert.Equal(502, error.Status);
        }

        // The registration gates on the key; this is the belt-and-braces path, and it
        // has to answer with the SAME sentence the null extractor uses or a half-built
        // graph would report a different error for the same condition.
        [Fact]
        public async Task ExtractAsync_SaysNotConfigured_WhenThereIsNoKey()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, Envelope(ElectricityBill));

            var error = await Assert.ThrowsAsync<AppException>(() =>
                Extractor(handler, apiKey: null).ExtractAsync(PdfRequest()));

            Assert.Equal(503, error.Status);
            Assert.Equal("ai_not_configured", error.Code);
            Assert.Equal(NullDocumentExtractor.NotConfiguredMessage, error.Message);
        }

        private static readonly byte[] Bytes = { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 };

        private static DocumentExtractionRequest PdfRequest() =>
            new(Bytes, "application/pdf", "Africa/Cairo", "en");

        private static GeminiDocumentExtractor Extractor(
            StubHttpMessageHandler handler,
            string? apiKey = "test-key") =>
            new(
                new HttpClient(handler),
                new DocumentExtractionOptions { ApiKey = apiKey },
                NullLogger<GeminiDocumentExtractor>.Instance);
    }
}
