using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Entities
{
    public class Document
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("TaskId")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string TaskId { get; set; }

        [BsonElement("UserId")]
        public string UserId { get; set; }

        [BsonElement("BlobUrl")]
        public string BlobUrl { get; set; }

        [BsonElement("ExtractedFields")]
        public BsonDocument? ExtractedFields { get; set; }

        [BsonElement("Category")]
        public string? Category { get; set; }

        [BsonElement("SourceType")]
        [BsonRepresentation(BsonType.String)]
        public DocumentSourceType SourceType { get; set; }

        [BsonElement("UploadedAt")]
        public DateTime UploadedAt { get; set; }

        [BsonElement("ExpiryDate")]
        public DateTime? ExpiryDate { get; set; }
    }
}
