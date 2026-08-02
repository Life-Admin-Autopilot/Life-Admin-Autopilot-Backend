using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Dtos
{
    public class CommitTaskResponse
    {
        public UserTask Task { get; set; }
        public Document? Document { get; set; }
    }
}
