using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class TaskPayload
    {
        //public string UserId { get; set; }
        public string Title { get; set; }

        public DateTime? DueDate { get; set; }

        public string Category { get; set; }

        public UserTaskPriority Priority { get; set; }

        public string SourceType { get; set; }
        public string Status { get; set; }
    }
}
