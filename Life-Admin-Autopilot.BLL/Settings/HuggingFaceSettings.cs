using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Settings
{
    public class HuggingFaceSettings
    {
        public const string SectionName = "HuggingFace";

        public string ApiKey { get; set; } = string.Empty;

        public string EmbeddingModelUrl { get; set; } = string.Empty;
    }
}
