using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class ClarificationAnswerDto
    {
        public string TaskId { get; set; } = string.Empty;

        public string Field { get; set; } = string.Empty;

        public string Question { get; set; } = string.Empty;

        public string Answer { get; set; } = string.Empty;
    }
}
