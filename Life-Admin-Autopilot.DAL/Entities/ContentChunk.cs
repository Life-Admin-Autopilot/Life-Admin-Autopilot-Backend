using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Entities
{
    // One searchable piece of the user's world - a task or a document - stored next to the
    // vector that lets Copilot Chat find it by meaning rather than by keyword (FR-7.x).
    public class ContentChunk
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("UserId")]
        public string UserId { get; set; } = string.Empty;

        // "task" or "document".
        [BsonElement("SourceType")]
        public string SourceType { get; set; } = string.Empty;

        // The _id of the task or document this chunk describes. Stored as an ObjectId to
        // match the existing rows, so joining back works the same for both.
        [BsonElement("SourceId")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string SourceId { get; set; } = string.Empty;

        [BsonElement("Text")]
        public string Text { get; set; } = string.Empty;

        [BsonElement("Embedding")]
        public float[] Embedding { get; set; } = [];

        // Not in the original schema. Vectors from two different models are not
        // comparable, and mixing them degrades search with no error to notice - recording
        // the model makes that auditable and back-fillable instead of invisible.
        [BsonElement("EmbeddingModel")]
        [BsonIgnoreIfNull]
        public string? EmbeddingModel { get; set; }
    }
}
