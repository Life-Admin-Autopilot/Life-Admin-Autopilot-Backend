using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IEmbeddingProvider
    {
        Task<float[]> GenerateEmbeddingAsync(string text);
    }
}
