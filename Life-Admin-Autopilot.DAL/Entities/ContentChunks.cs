using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Entities
{
    public class ContentChunks
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("UserId")]
        public string UserId { get; set; }

        [BsonElement("SourceType")]
        [BsonRepresentation(BsonType.String)]
        public ChunkSourceType SourceType { get; set; }

        [BsonElement("SourceId")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string SourceId { get; set; }

        [BsonElement("Text")]
        public string Text { get; set; }

        [BsonElement("Embedding")]
        public float[] Embedding { get; set; }
    }
}
