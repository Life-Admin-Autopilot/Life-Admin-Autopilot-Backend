using System.Text.Json;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Claude;
using Life_Admin_Autopilot.DAL.Claude.Models;
using Life_Admin_Autopilot.DAL.Storage;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class DocumentExtractionService : IDocumentExtractionService
    {
        // Kept tight on purpose. The model is being asked to read, not to reason: every
        // extra instruction is another chance for it to fill a gap with a plausible
        // invention, and an invented due date on a bill is the failure that matters.
        private const string SystemPrompt =
            "You read a single document and report only what it says. Reply with JSON and " +
            "nothing else, in this shape:\n" +
            "{\"description\":\"one or two sentences: what this document is, who issued it, " +
            "and what it asks the reader to do\"," +
            "\"dueDate\":\"the date the document states as due or expiring, as YYYY-MM-DD, " +
            "or null\"," +
            "\"amount\":\"the amount payable including its currency, or null\"," +
            "\"issuer\":\"the organisation that issued it, or null\"," +
            "\"category\":\"one of Financial, Vehicle, Home, Health, Work/University, " +
            "Personal, General\"}\n" +
            "Never guess. If the document does not state a due date, dueDate is null - do " +
            "not infer one from the issue date or from how bills usually work. The same " +
            "goes for every other field. Write the description in the document's own " +
            "language.";

        private readonly IFileStorageService _fileStorageService;
        private readonly IClaudeService _claudeService;
        private readonly ILogger<DocumentExtractionService> _logger;

        public DocumentExtractionService(
            IFileStorageService fileStorageService,
            IClaudeService claudeService,
            ILogger<DocumentExtractionService> logger)
        {
            _fileStorageService = fileStorageService;
            _claudeService = claudeService;
            _logger = logger;
        }

        public async Task<DocumentExtractionResponse> ExtractAsync(
            string userId,
            string path,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DocumentExtractionResponse.Fail("EXTRACT_NO_PATH", "No document path was given.");
            }

            // The blob name starts with the owner's id, so ownership is provable from the
            // path itself - no database lookup, and no reading another user's passport
            // scan by passing its path.
            if (!BlobPath.TrySplit(path, out _, out var blobName) || !BlobPath.IsOwnedBy(blobName, userId))
            {
                return DocumentExtractionResponse.Fail(
                    "EXTRACT_ACCESS_DENIED",
                    "That document belongs to another user.");
            }

            var download = await _fileStorageService.DownloadAsync(path, cancellationToken);

            if (download.IsFailure)
            {
                return DocumentExtractionResponse.Fail(download.Error!.Code, download.Error.Message);
            }

            var file = download.Value!;

            // Built directly rather than through ForImageExtraction, which has no system
            // prompt parameter - and the system prompt is what keeps the model reporting
            // instead of inventing.
            var request = new ClaudeCompletionRequest
            {
                SystemPrompt = SystemPrompt,
                Messages = new[]
                {
                    new ClaudeMessage
                    {
                        Role = "user",
                        Content = "Read this document and report what it says.",
                        Images = new[]
                        {
                            new ClaudeImageAttachment
                            {
                                Format = FormatFor(file.ContentType, path),
                                DataBase64 = Convert.ToBase64String(file.Content)
                            }
                        }
                    }
                }
            };

            var completion = await _claudeService.GetCompletionAsync(request, cancellationToken);

            if (completion.IsFailure)
            {
                _logger.LogWarning(
                    "Extraction failed for {Path}: {Code} {Message}",
                    path, completion.Error!.Code, completion.Error.Message);

                return DocumentExtractionResponse.Fail(completion.Error.Code, completion.Error.Message);
            }

            return Parse(completion.Value!.CompletionText, path);
        }

        private DocumentExtractionResponse Parse(string completionText, string path)
        {
            // Models wrap JSON in prose or a fenced block often enough that trusting the
            // whole string to parse is not worth the failed extraction.
            var start = completionText.IndexOf('{');
            var end = completionText.LastIndexOf('}');

            if (start < 0 || end <= start)
            {
                _logger.LogWarning("No JSON object in the extraction reply for {Path}", path);

                // The prose is still useful to the agent even when the shape is wrong, so
                // it is passed through as the description rather than discarded.
                return new DocumentExtractionResponse
                {
                    Succeeded = true,
                    Description = Truncate(completionText)
                };
            }

            try
            {
                using var document = JsonDocument.Parse(completionText[start..(end + 1)]);
                var root = document.RootElement;

                return new DocumentExtractionResponse
                {
                    Succeeded = true,
                    Description = ReadString(root, "description"),
                    DueDate = ReadString(root, "dueDate"),
                    Amount = ReadString(root, "amount"),
                    Issuer = ReadString(root, "issuer"),
                    Category = ReadString(root, "category")
                };
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Could not parse the extraction reply for {Path}", path);

                return new DocumentExtractionResponse
                {
                    Succeeded = true,
                    Description = Truncate(completionText)
                };
            }
        }

        private static string? ReadString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString();

            // "null" and "" both come back as strings often enough to be worth folding
            // into a real null, so callers only have one absent case to handle.
            return string.IsNullOrWhiteSpace(text) || text.Equals("null", StringComparison.OrdinalIgnoreCase)
                ? null
                : text;
        }

        private static string FormatFor(string contentType, string path)
        {
            if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return "pdf";
            }

            var slash = contentType.LastIndexOf('/');

            return slash >= 0 && slash < contentType.Length - 1
                ? contentType[(slash + 1)..].ToLowerInvariant()
                : "png";
        }

        private static string Truncate(string value) =>
            value.Length <= 800 ? value.Trim() : value[..800].Trim();
    }
}
