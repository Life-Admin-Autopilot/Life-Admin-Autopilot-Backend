using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public interface IContentChunksRepository
    {
        Task<ContentChunks> CreateAsync(ContentChunks contentChunks);
    }
}
