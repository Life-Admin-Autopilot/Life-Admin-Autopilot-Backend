using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class LangflowRequest
    {
        public string Mode { get; set; }

        public string Transcript { get; set; }

        public string PendingTasks { get; set; }

        public string Answers { get; set; }

        public string AccessToken { get; set; }
    }
}
