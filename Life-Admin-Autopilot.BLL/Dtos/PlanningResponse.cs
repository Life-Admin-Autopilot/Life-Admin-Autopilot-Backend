using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class PlanningResponse
    {
        public string Text { get; set; } = string.Empty;

        public List<ClarificationQuestionDto> Questions { get; set; } = [];
    }
}
