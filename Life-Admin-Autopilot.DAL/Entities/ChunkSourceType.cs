using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Entities
{
    // check if Json Converter is required
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ChunkSourceType
    {
        task,
        document
    }
}
