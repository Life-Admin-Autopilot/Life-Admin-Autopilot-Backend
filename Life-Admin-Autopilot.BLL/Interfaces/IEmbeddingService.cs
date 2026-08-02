using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IEmbeddingService
    {
        Task EmbedAsync(UserTask task,Document? document = null);
    }
}
