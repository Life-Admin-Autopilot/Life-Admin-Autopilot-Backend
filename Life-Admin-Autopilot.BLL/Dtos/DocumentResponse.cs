using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class DocumentResponse
    {
        public string? Id { get; set; }

        public string TaskId { get; set; } = null!;

        public string UserId { get; set; } = null!;

        public string BlobUrl { get; set; } = null!;

        public JsonElement? ExtractedFields { get; set; }

        public string? Category { get; set; }

        public DocumentSourceType SourceType { get; set; }

        public DateTime UploadedAt { get; set; }

        public DateTime? ExpiryDate { get; set; }
    }
}
