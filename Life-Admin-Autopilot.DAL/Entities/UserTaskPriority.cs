using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Entities
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum UserTaskPriority
    {
        urgent,
        important,
        normal
    }
}
