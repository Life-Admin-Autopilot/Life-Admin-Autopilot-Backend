using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Settings
{
    public class LangflowSettings
    {
        public const string SectionName = "Langflow";

        public string Url { get; set; }

        public string ApiKey { get; set; }

        public string FlowId { get; set; }
    }
}
