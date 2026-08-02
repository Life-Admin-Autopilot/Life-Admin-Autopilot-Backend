using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class CommitTaskRequest
    {
        public TaskPayload Task { get; set; }
        public DocumentPayload? Document { get; set; }
    }
}
