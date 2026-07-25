using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Configurations
{
    public class MongoDbSettings
    {
        public const string SectionName = "MongoDbSettings";

        public string ConnectionString { get; set; } = null!;

        public string DatabaseName { get; set; } = null!;
    }
}
