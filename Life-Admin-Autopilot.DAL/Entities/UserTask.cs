using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Entities
{
    public class UserTask
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("UserId")]
        public string UserId { get; set; }

        [BsonElement("Title")]
        public string Title { get; set; }

        [BsonElement("DueDate")]
        public DateTime? DueDate { get; set; }

        [BsonElement("Status")]
        public string Status { get; set; }

        [BsonElement("SourceType")]
        public string SourceType { get; set; }

        // Present on 70 of the 76 task documents already in Atlas, written by the
        // Planning Agent's commit path. Without these properties the entity silently
        // dropped both fields on every write that went through this API, so a task
        // saved here lost the category and priority the user had just confirmed.
        // Nullable and ignored when null so the older documents still deserialize.
        [BsonElement("Category")]
        [BsonIgnoreIfNull]
        public string? Category { get; set; }

        [BsonElement("Priority")]
        [BsonIgnoreIfNull]
        public string? Priority { get; set; }
    }
}
