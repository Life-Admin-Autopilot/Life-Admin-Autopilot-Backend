using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class ClarificationRequest
    {
        public List<ClarificationAnswerDto> Answers { get; set; } = [];
    }
}
