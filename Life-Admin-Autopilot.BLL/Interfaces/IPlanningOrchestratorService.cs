using Life_Admin_Autopilot.BLL.Dtos;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IPlanningOrchestratorService
    {
        Task<PlanningResponse> ProcessTranscriptAsync(
            TranscriptRequest request,
            string accessToken);

        Task<PlanningResponse> ProcessClarificationAsync(
            ClarificationRequest request,
            string userId,
            string accessToken);
    }
}
