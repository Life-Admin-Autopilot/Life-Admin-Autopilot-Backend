using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class ClarificationQuestionDto
    {
        public string TaskId { get; set; } = string.Empty;

        public string Field { get; set; } = string.Empty;

        public string Question { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public List<string> Answer { get; set; } = [];

        public string Task { get; set; } = string.Empty;
    }
}
