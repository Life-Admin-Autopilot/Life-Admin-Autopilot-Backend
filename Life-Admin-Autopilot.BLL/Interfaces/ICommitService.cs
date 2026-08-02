using Life_Admin_Autopilot.BLL.Dtos;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface ICommitService
    {
        public Task<CommitTaskResponse> CommitTaskAndDocumentAsync(CommitTaskRequest request);
    }
}
