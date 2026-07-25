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
    }
}
