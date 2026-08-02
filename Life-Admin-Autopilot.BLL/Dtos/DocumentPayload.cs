using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class DocumentPayload
    {
        public string BlobUrl { get; set; }
        public JsonElement? ExtractedFields { get; set; }
        public DocumentSourceType SourceType { get; set; }
        public DateTime UploadedAt { get; set; }
        public DateTime? ExpiryDate { get; set; }
    }
}
