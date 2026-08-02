using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IEmbeddingService
    {
        public Task EmbeddTask(string text);
    }
}
