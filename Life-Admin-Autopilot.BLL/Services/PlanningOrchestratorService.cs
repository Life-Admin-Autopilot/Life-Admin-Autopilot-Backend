using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class PlanningOrchestratorService : IPlanningOrchestratorService
    {
        private readonly ILangflowClientService _langflowClientService;
        private readonly IUserTaskService _userTaskService;
        public PlanningOrchestratorService(ILangflowClientService langflowClientService,
            IUserTaskService userTaskService)
        {
            _langflowClientService = langflowClientService;
            _userTaskService = userTaskService;
        }
        public async Task<PlanningResponse> ProcessTranscriptAsync(TranscriptRequest request, string accessToken)
        {
            if (string.IsNullOrWhiteSpace(request.Transcript))
                throw new ArgumentException("Transcript cannot be empty.");

            var langflowRequest = new LangflowRequest
            {
                Mode = "transcript",
                Transcript = request.Transcript,
                PendingTasks = "[]",
                Answers = "[]",
                AccessToken = accessToken
            };

            return await _langflowClientService.RunAsync(langflowRequest);
        }
        public async Task<PlanningResponse> ProcessClarificationAsync(ClarificationRequest request, string userId, string accessToken)
        {
            if (request.Answers.Count == 0)
                throw new ArgumentException("Answers are required.");

            var taskIds = request.Answers
                .Select(x => x.TaskId)
                .Distinct()
                .ToList();

            var pendingTasks = await _userTaskService
                .GetDraftTasksByIdsAsync(taskIds, userId);

            if (!pendingTasks.Any())
                throw new InvalidOperationException("No draft tasks found.");

            var langflowRequest = new LangflowRequest
            {
                Mode = "clarification",
                Transcript = string.Empty,
                PendingTasks = JsonSerializer.Serialize(pendingTasks),
                Answers = JsonSerializer.Serialize(request.Answers),
                AccessToken = accessToken
            };

            return await _langflowClientService.RunAsync(langflowRequest);
        }

        
    }
}
